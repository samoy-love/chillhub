//! Content synchronization. A failed or cancelled transfer cannot publish a version marker.
mod cleanup;
pub use cleanup::CleanupFile;
mod deferred;
mod hash_cache;
use crate::manifest::{self, DirectoryGuard, Manifest, ManifestFile, open_regular, safe_join};
use base64::Engine as _;
use fs2::FileExt;
use hash_cache::{FileHashes, HashCache, VerifiedStamp};
use reqwest::{
    StatusCode,
    blocking::Client,
    header::{ACCEPT_ENCODING, CONTENT_RANGE, RANGE},
};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    cmp::Reverse,
    collections::{HashMap, HashSet},
    fmt, fs,
    io::{self, BufWriter, Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::{
        Arc, Condvar, Mutex,
        atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering},
    },
    time::{Duration, Instant},
};
use url::Url;

const BUFFER: usize = 256 * 1024;
const HASH_WORKERS: usize = 4;
const PROGRESS_INTERVAL: Duration = Duration::from_millis(100);
const MAX_MANIFEST: u64 = 32 * 1024 * 1024;
const PRIME: u64 = 0x100000001b3;

#[derive(Debug)]
pub enum TransferError {
    Cancelled,
    InvalidManifest(String),
    UnsafePath(String),
    Network(String),
    Integrity(String),
    Protocol(String),
    Io(io::Error),
    AntivirusBlocked(String),
    ReplacementDeferred(String),
    PendingReplacement,
    DependencyBlocked(&'static str),
    Busy,
    NoSpace {
        drive: String,
        available: u64,
        required: u64,
    },
    NeedsCleanupConfirmation(Vec<CleanupFile>),
    CleanupConfirmationChanged,
}
impl fmt::Display for TransferError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Cancelled => write!(f, "Загрузка отменена"),
            Self::InvalidManifest(_) => {
                write!(f, "Список файлов повреждён. Попробуйте обновить его позже")
            }
            Self::UnsafePath(_) => write!(f, "Папка содержит небезопасный путь или ссылку"),
            Self::Network(_) => write!(f, "Не удалось скачать файлы. Проверьте подключение"),
            Self::Integrity(p) => write!(f, "Скачанный файл не прошёл проверку: {p}"),
            Self::Protocol(_) => write!(f, "Сервер прислал неполные или неверные данные"),
            Self::Io(e) => write!(f, "Не удалось записать или прочитать файлы: {e}"),
            Self::AntivirusBlocked(path) => write!(
                f,
                "Антивирус заблокировал файл {path}. Откройте защиту Windows и проверьте историю защиты"
            ),
            Self::Busy => write!(f, "Эту папку уже обновляет другой процесс"),
            Self::PendingReplacement => write!(
                f,
                "Сначала завершите предыдущую загрузку для этой папки и повторите действие"
            ),
            Self::DependencyBlocked(message) => f.write_str(message),
            Self::ReplacementDeferred(path) => write!(
                f,
                "Файл {path} занят. Закройте игру и повторите загрузку. Если это не поможет, перезагрузите компьютер: скачанный файл сохранён для замены."
            ),
            Self::NoSpace {
                drive,
                available,
                required,
            } => write!(
                f,
                "На диске {drive} не хватает места. Освободите {} и продолжите загрузку: скачанное сохранено",
                display_size(required.saturating_sub(*available).max(1))
            ),
            Self::NeedsCleanupConfirmation(paths) => write!(
                f,
                "Нужно подтвердить удаление лишних файлов ({})",
                paths.len()
            ),
            Self::CleanupConfirmationChanged => {
                write!(f, "Список файлов изменился. Откройте подтверждение снова")
            }
        }
    }
}
impl std::error::Error for TransferError {}
impl From<io::Error> for TransferError {
    fn from(e: io::Error) -> Self {
        Self::Io(e)
    }
}
impl From<reqwest::Error> for TransferError {
    fn from(e: reqwest::Error) -> Self {
        Self::Network(format!("{e:?}"))
    }
}
type Result<T> = std::result::Result<T, TransferError>;

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum SyncScope {
    #[default]
    WholeRoot,
    OwnFilesOnly,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct TransferRequest {
    pub depends_on: Option<String>,
    pub game_id: String,
    pub version: String,
    pub root: PathBuf,
    pub manifest_url: String,
    pub content_base_url: String,
    pub scope: SyncScope,
    pub previous_owned_paths: Vec<String>,
    pub foreign_paths: Vec<String>,
    pub preserve_paths: Vec<String>,
    pub donor_roots: Vec<PathBuf>,
    pub force_rehash: bool,
    pub approved_cleanup_files: Vec<CleanupFile>,
    pub max_parallel: usize,
    pub bytes_per_second: u64,
    pub write_markers: bool,
}
impl Default for TransferRequest {
    fn default() -> Self {
        Self {
            depends_on: None,
            game_id: String::new(),
            version: String::new(),
            root: PathBuf::new(),
            manifest_url: String::new(),
            content_base_url: String::new(),
            scope: SyncScope::WholeRoot,
            previous_owned_paths: vec![],
            foreign_paths: vec![],
            preserve_paths: vec![],
            donor_roots: vec![],
            force_rehash: false,
            approved_cleanup_files: vec![],
            max_parallel: 4,
            bytes_per_second: 0,
            write_markers: true,
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum TransferStage {
    Checking,
    Downloading,
    Verifying,
    Activating,
    Completed,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct TransferEvent {
    pub stage: TransferStage,
    pub current_file: String,
    pub completed_bytes: u64,
    pub total_bytes: u64,
    pub network_bytes: u64,
    pub reused_bytes: u64,
    pub completed_files: usize,
    pub total_files: usize,
}
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct TransferOutcome {
    pub downloaded_files: usize,
    pub deleted_files: usize,
    pub network_bytes: u64,
    pub reused_bytes: u64,
    pub total_bytes: u64,
}
#[derive(Debug, Clone)]
pub struct TransferPlan {
    pub manifest: Manifest,
    pub downloads: Vec<ManifestFile>,
    pub to_delete: Vec<CleanupFile>,
    pub required_free_bytes: u64,
    pub total_download_bytes: u64,
    pub total_manifest_bytes: u64,
    pub hash_mismatches: usize,
    request: TransferRequest,
}

pub(crate) struct TransferInspection {
    pub manifest: Manifest,
    pub needs_repair: bool,
    pub required_free_bytes: u64,
    pub total_download_bytes: u64,
}

#[derive(Default)]
struct ControlState {
    paused: bool,
    cancelled: bool,
    speed_limit: Option<u64>,
}
#[derive(Clone, Default)]
pub struct TransferControl {
    shared: Arc<(Mutex<ControlState>, Condvar)>,
}
impl TransferControl {
    /// Overrides a persisted request's speed without restarting its transfer.
    pub fn set_speed_limit(&self, bytes_per_second: u64) {
        self.shared
            .0
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .speed_limit = Some(bytes_per_second);
        self.shared.1.notify_all();
    }
    fn speed_limit(&self, fallback: u64) -> u64 {
        self.shared
            .0
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .speed_limit
            .unwrap_or(fallback)
    }
    pub fn pause(&self) {
        self.shared
            .0
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .paused = true;
    }
    pub fn resume(&self) {
        self.shared
            .0
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .paused = false;
        self.shared.1.notify_all();
    }
    pub fn cancel(&self) {
        self.shared
            .0
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .cancelled = true;
        self.shared.1.notify_all();
    }
    pub fn is_paused(&self) -> bool {
        self.shared
            .0
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .paused
    }
    pub fn is_cancelled(&self) -> bool {
        self.shared
            .0
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .cancelled
    }
    pub fn checkpoint(&self) -> Result<()> {
        let mut state = self.shared.0.lock().unwrap_or_else(|e| e.into_inner());
        while state.paused && !state.cancelled {
            state = self.shared.1.wait(state).unwrap_or_else(|e| e.into_inner());
        }
        if state.cancelled {
            Err(TransferError::Cancelled)
        } else {
            Ok(())
        }
    }
    fn delay(&self, delay: Duration) -> Result<()> {
        let end = Instant::now() + delay;
        while Instant::now() < end {
            self.checkpoint()?;
            let duration = end
                .saturating_duration_since(Instant::now())
                .min(Duration::from_millis(50));
            let state = self.shared.0.lock().unwrap_or_else(|e| e.into_inner());
            drop(
                self.shared
                    .1
                    .wait_timeout(state, duration)
                    .unwrap_or_else(|e| e.into_inner()),
            );
        }
        self.checkpoint()
    }
}

#[derive(Clone)]
pub struct TransferEngine {
    client: Client,
    hash_cache_dir: Option<PathBuf>,
    diagnostic_dir: Option<PathBuf>,
}
impl TransferEngine {
    pub fn new() -> Result<Self> {
        Self::with_redirects(reqwest::redirect::Policy::custom(|attempt| {
            let target = attempt.url();
            let allowed = attempt.previous().first().is_some_and(|initial| {
                initial.origin() == target.origin()
                    && target.username().is_empty()
                    && target.password().is_none()
            });
            if attempt.previous().len() >= 5 || !allowed {
                attempt.error("Content redirects must stay on the original trusted origin")
            } else {
                attempt.follow()
            }
        }))
    }
    pub fn for_update() -> Result<Self> {
        Self::with_redirects(reqwest::redirect::Policy::none())
    }
    fn with_redirects(policy: reqwest::redirect::Policy) -> Result<Self> {
        let builder = Client::builder()
            .connect_timeout(Duration::from_secs(10))
            .redirect(policy)
            .user_agent(concat!("ChillHub/", env!("CARGO_PKG_VERSION")));
        // Blocking Response::read applies this timeout to each individual read;
        // a large file therefore has no whole-download deadline. Using the
        // async client's read_timeout here would require a reactor on the reader.
        let client = builder.timeout(Duration::from_secs(15)).build()?;
        Ok(Self {
            client,
            hash_cache_dir: None,
            diagnostic_dir: None,
        })
    }
    /// Only game synchronization enables this cache; update executables are always rehashed.
    pub fn with_hash_cache_dir(mut self, directory: impl Into<PathBuf>) -> Self {
        self.hash_cache_dir = Some(directory.into());
        self
    }
    pub(crate) fn with_diagnostics(mut self, directory: impl Into<PathBuf>) -> Self {
        self.diagnostic_dir = Some(directory.into());
        self
    }
    pub fn fetch_manifest(&self, address: &str, control: &TransferControl) -> Result<Manifest> {
        control.checkpoint()?;
        checked_url(address)?;
        let send = || {
            self.client
                .get(address)
                .header(ACCEPT_ENCODING, "identity")
                .send()
                .and_then(|response| response.error_for_status())
        };
        let response = match send() {
            Ok(response) => response,
            Err(error) => {
                let retry = match error.status() {
                    Some(
                        StatusCode::REQUEST_TIMEOUT
                        | StatusCode::INTERNAL_SERVER_ERROR
                        | StatusCode::BAD_GATEWAY
                        | StatusCode::SERVICE_UNAVAILABLE
                        | StatusCode::GATEWAY_TIMEOUT,
                    ) => true,
                    Some(_) => false,
                    None => {
                        !error.is_redirect()
                            && (error.is_timeout() || error.is_connect() || error.is_request())
                    }
                };
                self.log_manifest_http_error(&error, 1, retry);
                if !retry {
                    return Err(error.into());
                }
                control.delay(Duration::from_millis(250))?;
                send().inspect_err(|error| {
                    self.log_manifest_http_error(error, 2, false);
                })?
            }
        };
        if response.content_length().is_some_and(|n| n > MAX_MANIFEST) {
            return Err(TransferError::InvalidManifest("manifest too large".into()));
        }
        let mut bytes = Vec::new();
        response.take(MAX_MANIFEST + 1).read_to_end(&mut bytes)?;
        control.checkpoint()?;
        if bytes.len() as u64 > MAX_MANIFEST {
            return Err(TransferError::InvalidManifest("manifest too large".into()));
        }
        let manifest: Manifest = serde_json::from_slice(&bytes)
            .map_err(|e| TransferError::InvalidManifest(e.to_string()))?;
        manifest::validate_manifest(&manifest).map_err(TransferError::InvalidManifest)?;
        Ok(manifest)
    }
    fn log_manifest_http_error(&self, error: &reqwest::Error, attempt: u8, retry: bool) {
        let Some(directory) = &self.diagnostic_dir else {
            return;
        };
        let status = error.status().map(|status| status.as_u16());
        let cause = if status.is_some() {
            "http"
        } else if error.is_timeout() {
            "timeout"
        } else if error.is_redirect() {
            "redirect"
        } else if error.is_connect() {
            "connect"
        } else if error.is_request() {
            "request"
        } else {
            "other"
        };
        // The request address and reqwest error chain can contain signed URLs.
        crate::diagnostics::event(
            directory,
            "download.manifest_http_error",
            serde_json::json!({"attempt": attempt, "cause": cause, "status": status, "retry": retry}),
        );
    }
    pub fn plan(
        &self,
        request: &TransferRequest,
        control: &TransferControl,
        event: &(dyn Fn(TransferEvent) + Sync),
    ) -> Result<TransferPlan> {
        validate_request(request)?;
        let manifest = self.fetch_manifest(&request.manifest_url, control)?;
        self.plan_manifest(request, manifest, control, event)
    }
    pub fn plan_manifest(
        &self,
        request: &TransferRequest,
        manifest: Manifest,
        control: &TransferControl,
        event: &(dyn Fn(TransferEvent) + Sync),
    ) -> Result<TransferPlan> {
        self.plan_files(request, manifest, control, event, true)
    }

    /// A size hint or background repair check needs only manifest-owned content.
    /// Only planning an executable transfer scans extra files for cleanup consent.
    pub(crate) fn inspect_manifest(
        &self,
        request: &TransferRequest,
        manifest: Manifest,
        control: &TransferControl,
    ) -> Result<TransferInspection> {
        let plan = self.plan_files(request, manifest, control, &|_| {}, false)?;
        Ok(TransferInspection {
            manifest: plan.manifest,
            needs_repair: !plan.downloads.is_empty(),
            required_free_bytes: plan.required_free_bytes,
            total_download_bytes: plan.total_download_bytes,
        })
    }

    fn plan_files(
        &self,
        request: &TransferRequest,
        mut manifest: Manifest,
        control: &TransferControl,
        event: &(dyn Fn(TransferEvent) + Sync),
        include_cleanup: bool,
    ) -> Result<TransferPlan> {
        validate_request(request)?;
        manifest.normalize_empty_dirs();
        manifest::validate_manifest(&manifest).map_err(TransferError::InvalidManifest)?;
        if !manifest.game_id.is_empty()
            && !request.game_id.is_empty()
            && !manifest.game_id.eq_ignore_ascii_case(&request.game_id)
        {
            return Err(TransferError::InvalidManifest("gameId mismatch".into()));
        }
        if !manifest.version.is_empty()
            && !request.version.is_empty()
            && manifest.version != request.version
        {
            return Err(TransferError::InvalidManifest("version mismatch".into()));
        }
        let _guard = DirectoryGuard::acquire(&request.root, false)
            .map_err(|e| TransferError::UnsafePath(e.to_string()))?;
        let root_present = request.root.try_exists()?;
        let foreign = folded(&request.foreign_paths);
        let preserve = folded(&request.preserve_paths);
        let wanted: HashSet<_> = manifest
            .files
            .iter()
            .map(|f| f.path.to_lowercase())
            .collect();
        let total_bytes = manifest
            .files
            .iter()
            .try_fold(0u64, |n, f| n.checked_add(f.size))
            .ok_or_else(|| TransferError::InvalidManifest("size overflow".into()))?;
        let hashes = Mutex::new(HashCache::load(
            self.hash_cache_dir.as_deref(),
            &request.root,
        ));
        let checking = Progress::new(total_bytes, manifest.files.len(), 0);
        checking.emit(TransferStage::Checking, "", event, true);
        let next = AtomicUsize::new(0);
        let stopped = AtomicBool::new(false);
        let workers = hash_worker_count().min(manifest.files.len());
        let mut replacements = std::thread::scope(|scope| {
            let workers: Vec<_> = (0..workers)
                .map(|_| {
                    scope.spawn(|| -> Result<_> {
                        let mut replacements = Vec::new();
                        let mut buffer = vec![0; BUFFER];
                        let result = (|| {
                            while !stopped.load(Ordering::Relaxed) {
                                let index = next.fetch_add(1, Ordering::Relaxed);
                                let Some(file) = manifest.files.get(index) else {
                                    break;
                                };
                                control.checkpoint()?;
                                let key = file.path.to_lowercase();
                                let eligible = !manifest::is_service_path(&file.path)
                                    && !foreign.contains(&key);
                                if eligible && !root_present {
                                    // No descendants can exist yet. Execute revalidates paths.
                                    replacements.push((index, None, 0));
                                } else if eligible {
                                    let path = path_in(&request.root, &file.path)?;
                                    if !(preserve.contains(&key) && path.is_file()) {
                                        let (valid, size) = HashCache::verify(
                                            &hashes,
                                            &path,
                                            file,
                                            request.force_rehash,
                                            control,
                                            &mut buffer,
                                        )
                                        .map_err(|error| antivirus_error(error, &file.path))?;
                                        if !valid {
                                            replacements.push((
                                                index,
                                                size,
                                                allocated_part_bytes(&path),
                                            ));
                                        }
                                    }
                                }
                                checking.completed.fetch_add(file.size, Ordering::Relaxed);
                                checking.files.fetch_add(1, Ordering::Relaxed);
                                checking.emit(TransferStage::Checking, &file.path, event, false);
                            }
                            Ok(replacements)
                        })();
                        if result.is_err() {
                            stopped.store(true, Ordering::Relaxed);
                        }
                        result
                    })
                })
                .collect();
            let mut replacements = Vec::new();
            let mut error = None;
            for worker in workers {
                match worker.join().expect("hash worker panicked") {
                    Ok(mut result) => replacements.append(&mut result),
                    Err(e) => {
                        error.get_or_insert(e);
                    }
                }
            }
            error.map_or(Ok(replacements), Err)
        })?;
        hashes
            .into_inner()
            .unwrap_or_else(|e| e.into_inner())
            .save();
        let mut to_delete = Vec::new();
        if include_cleanup && request.root.exists() {
            let candidates = match request.scope {
                SyncScope::OwnFilesOnly => request.previous_owned_paths.clone(),
                SyncScope::WholeRoot => collect_files(&request.root)?,
            };
            for path in candidates {
                let key = path.to_lowercase();
                if manifest::is_service_path(&path)
                    || wanted.contains(&key)
                    || foreign.contains(&key)
                    || preserve.contains(&key)
                {
                    continue;
                }
                if let Some(base) = key
                    .strip_suffix(".part")
                    .or_else(|| key.strip_suffix(".new"))
                    && wanted.contains(base)
                {
                    continue;
                }
                let candidate = path_in(&request.root, &path)?;
                if candidate.is_file()
                    && let Some(file) = cleanup::snapshot(&candidate, &path, control)
                        .map_err(|error| antivirus_error(error, &path))?
                {
                    to_delete.push(file);
                }
            }
        }
        to_delete.sort_by(|a, b| a.path.cmp(&b.path));
        to_delete.dedup_by(|a, b| a.path == b.path);
        checking.emit(TransferStage::Checking, "", event, true);
        replacements.sort_unstable_by_key(|(index, _, _)| *index);
        let mut downloads = Vec::with_capacity(replacements.len());
        let mut allocations = Vec::with_capacity(replacements.len());
        let mut hash_mismatches = 0;
        for (index, old, existing_part) in replacements {
            let file = &manifest.files[index];
            hash_mismatches += usize::from(old.as_ref().is_some_and(|old| old.size == file.size));
            let old_size = old.map_or(0, |old| old.allocated);
            let remaining = file.size.saturating_sub(existing_part);
            allocations.push((remaining, old_size));
            downloads.push(file.clone());
        }
        let total_download_bytes: u64 = downloads.iter().map(|f| f.size).sum();
        let required_free_bytes =
            required_transfer_space(&downloads, &allocations, request.max_parallel.clamp(1, 16));
        Ok(TransferPlan {
            manifest,
            downloads,
            to_delete,
            required_free_bytes,
            total_download_bytes,
            total_manifest_bytes: total_bytes,
            hash_mismatches,
            request: request.clone(),
        })
    }
    pub fn run(
        &self,
        request: &TransferRequest,
        control: &TransferControl,
        event: &(dyn Fn(TransferEvent) + Sync),
    ) -> Result<TransferOutcome> {
        let plan = self.prepare_plan(request, control, event)?;
        self.execute(request, &plan, control, event)
    }
    /// Fetch and validate the requested manifest before replaying an interrupted
    /// replacement. Plain planning remains read-only with respect to game files.
    pub fn prepare_plan(
        &self,
        request: &TransferRequest,
        control: &TransferControl,
        event: &(dyn Fn(TransferEvent) + Sync),
    ) -> Result<TransferPlan> {
        let plan = self.plan(request, control, event)?;
        if deferred::recover(request, &plan.downloads, control)? {
            self.plan_manifest(request, plan.manifest, control, event)
        } else {
            Ok(plan)
        }
    }
    pub fn execute(
        &self,
        request: &TransferRequest,
        plan: &TransferPlan,
        control: &TransferControl,
        event: &(dyn Fn(TransferEvent) + Sync),
    ) -> Result<TransferOutcome> {
        self.execute_inner(request, plan, control, event)
            .map_err(|error| disk_error(error, &request.root, plan.total_download_bytes))
    }
    fn execute_inner(
        &self,
        request: &TransferRequest,
        plan: &TransferPlan,
        control: &TransferControl,
        event: &(dyn Fn(TransferEvent) + Sync),
    ) -> Result<TransferOutcome> {
        validate_request(request)?;
        // A UI may approve the same plan; changing roots/URLs/ownership requires a fresh plan.
        let mut approved = request.clone();
        approved.approved_cleanup_files = plan.request.approved_cleanup_files.clone();
        if serde_json::to_vec(&approved).ok() != serde_json::to_vec(&plan.request).ok() {
            return Err(TransferError::InvalidManifest(
                "request changed after planning".into(),
            ));
        }
        manifest::validate_manifest(&plan.manifest).map_err(TransferError::InvalidManifest)?;
        if !cleanup::is_approved(&plan.to_delete, &request.approved_cleanup_files) {
            return Err(TransferError::NeedsCleanupConfirmation(
                plan.to_delete.clone(),
            ));
        }
        control.checkpoint()?;
        let _guard = DirectoryGuard::acquire(&request.root, true)
            .map_err(|e| TransferError::UnsafePath(e.to_string()))?;
        let _lock = acquire_transfer_lock(&request.root)?;
        ensure_not_running(&request.root)?;
        let mut hashes = HashCache::load(self.hash_cache_dir.as_deref(), &request.root);
        // Drop old fingerprints before touching files, including failed/interrupted updates.
        for file in &plan.downloads {
            hashes.invalidate(&file.path);
        }
        for file in &plan.to_delete {
            hashes.invalidate(&file.path);
        }
        hashes.save();
        let hashes = Mutex::new(hashes);
        let available = fs2::available_space(&request.root)?;
        if available < plan.required_free_bytes {
            return Err(TransferError::NoSpace {
                drive: drive_label(&request.root),
                available,
                required: plan.required_free_bytes,
            });
        }
        let marker = format!(
            "version={}\r\npid={}\r\n",
            request.version,
            std::process::id()
        );
        atomic_write(&request.root.join(".updating"), marker.as_bytes())?;
        let stats = Progress::new(
            plan.total_download_bytes,
            plan.downloads.len(),
            request.bytes_per_second,
        );
        stats.emit(TransferStage::Downloading, "", event, true);
        let schedule = download_order(&plan.downloads);
        let next = AtomicUsize::new(0);
        let stopped = AtomicBool::new(false);
        let error = Mutex::new(None);
        std::thread::scope(|scope| {
            for _ in 0..request.max_parallel.clamp(1, 16).min(schedule.len()) {
                scope.spawn(|| {
                    let mut buffer = vec![0; BUFFER];
                    while !stopped.load(Ordering::Acquire) {
                        let index = next.fetch_add(1, Ordering::Relaxed);
                        let Some(&file_index) = schedule.get(index) else {
                            break;
                        };
                        let file = &plan.downloads[file_index];
                        if let Err(e) = self.transfer_file(
                            request,
                            &plan.manifest,
                            file,
                            control,
                            &stats,
                            event,
                            &hashes,
                            &mut buffer,
                        ) {
                            stopped.store(true, Ordering::Release);
                            let mut saved = error.lock().unwrap_or_else(|e| e.into_inner());
                            if saved.is_none() {
                                *saved = Some(antivirus_error(e, &file.path));
                            }
                            break;
                        }
                    }
                });
            }
        });
        hashes.lock().unwrap_or_else(|e| e.into_inner()).save();
        if let Some(error) = error.into_inner().unwrap_or_else(|e| e.into_inner()) {
            // A short failed response may never hit the normal 100 ms progress interval.
            // Publish its real traffic before returning, including cancellation and failures.
            stats.emit(TransferStage::Downloading, "", event, true);
            return Err(disk_error(
                error,
                &request.root,
                plan.total_download_bytes
                    .saturating_sub(stats.completed.load(Ordering::Relaxed)),
            ));
        }
        control.checkpoint()?;
        ensure_not_running(&request.root)?;
        stats.emit(TransferStage::Activating, "", event, true);
        let foreign = folded(&request.foreign_paths);
        let preserve = folded(&request.preserve_paths);
        let mut deleted = Vec::new();
        let mut changed = Vec::new();
        for file in &plan.to_delete {
            control.checkpoint()?;
            let relative = &file.path;
            if manifest::is_service_path(relative)
                || foreign.contains(&relative.to_lowercase())
                || preserve.contains(&relative.to_lowercase())
            {
                continue;
            }
            let path = path_in(&request.root, relative)?;
            match cleanup::remove_approved(&path, file, control)? {
                cleanup::CleanupResult::Removed => deleted.push(path),
                cleanup::CleanupResult::Missing => {}
                cleanup::CleanupResult::Changed(file) => changed.push(file),
            }
        }
        if !changed.is_empty() {
            return Err(TransferError::NeedsCleanupConfirmation(changed));
        }
        for dir in &plan.manifest.empty_dirs {
            let path = path_in(&request.root, dir)?;
            DirectoryGuard::acquire(&path, true)?;
        }
        prune_deleted_parents(&request.root, &deleted, &plan.manifest.empty_dirs);
        control.checkpoint()?;
        if request.write_markers {
            if request.scope == SyncScope::OwnFilesOnly {
                crate::launch::set_doorstop_enabled(&request.root, false)
                    .map_err(TransferError::Protocol)?;
            }
            write_install_markers(request, &plan.manifest)?;
        }
        remove_regular(&request.root.join(".updating"))?;
        if request.scope == SyncScope::WholeRoot {
            crate::integrity::invalidate(&request.root);
        }
        stats.emit(TransferStage::Completed, "", event, true);
        Ok(TransferOutcome {
            downloaded_files: plan.downloads.len(),
            deleted_files: deleted.len(),
            network_bytes: stats.network.load(Ordering::Relaxed),
            reused_bytes: stats.reused.load(Ordering::Relaxed),
            total_bytes: plan.total_download_bytes,
        })
    }

    #[allow(clippy::too_many_arguments)]
    fn transfer_file(
        &self,
        request: &TransferRequest,
        manifest: &Manifest,
        file: &ManifestFile,
        control: &TransferControl,
        stats: &Progress,
        event: &(dyn Fn(TransferEvent) + Sync),
        hashes: &Mutex<HashCache>,
        buffer: &mut [u8],
    ) -> Result<()> {
        control.checkpoint()?;
        manifest::validate_relative(&file.path).map_err(TransferError::UnsafePath)?;
        let part_name = format!("{}.part", file.path);
        manifest::validate_relative(&part_name).map_err(TransferError::UnsafePath)?;
        let mut destination = request.root.clone();
        destination.extend(file.path.split('/'));
        let parent = destination.parent().unwrap();
        let _guard = DirectoryGuard::acquire(parent, true)?;
        let part = destination.with_file_name(part_name.rsplit('/').next().unwrap());
        // Both paths have the same validated parent, held through the rename.
        manifest::reject_link(&destination)
            .map_err(|e| TransferError::UnsafePath(e.to_string()))?;
        manifest::reject_link(&part).map_err(|e| TransferError::UnsafePath(e.to_string()))?;
        let (mut ready, mut verified_stamp) =
            hash_cache::verify_for_promotion(&part, file, control)?;
        let mut credited = 0u64;
        if ready {
            open_regular(&part, true, false, false)?.sync_all()?;
            set_credit(stats, &mut credited, file.size);
        }
        if !ready {
            for donor in &request.donor_roots {
                control.checkpoint()?;
                let source = path_in(donor, &file.path)?;
                if source == destination || !source.is_file() {
                    continue;
                }
                let _donor_guard = DirectoryGuard::acquire(source.parent().unwrap(), false)?;
                let mut source_file = open_regular(&source, false, false, false)?;
                if source_file.metadata()?.len() != file.size
                    || (fs::metadata(&part).is_ok_and(|meta| meta.len() != 0)
                        && !verify_file(&source, file, control)?)
                {
                    continue;
                }
                let mut out = open_regular(&part, true, true, true)?;
                let mut digests = FileHashes::new(file);
                let mut remaining = file.size;
                while remaining != 0 {
                    control.checkpoint()?;
                    let next = remaining.min(buffer.len() as u64) as usize;
                    let n = source_file.read(&mut buffer[..next])?;
                    if n == 0 {
                        break;
                    }
                    out.write_all(&buffer[..n])?;
                    digests.update(&buffer[..n]);
                    remaining -= n as u64;
                }
                match digests.seal(out, &part, file, control) {
                    Ok(stamp) => {
                        verified_stamp = stamp;
                        stats.reused.fetch_add(file.size, Ordering::Relaxed);
                        set_credit(stats, &mut credited, file.size);
                        ready = true;
                        break;
                    }
                    Err(TransferError::Integrity(_)) => remove_regular(&part)?,
                    Err(error) => return Err(error),
                }
            }
        }
        let url = file_url(&request.content_base_url, &file.path)?;
        if !ready
            && destination.is_file()
            && let Some(blocks) = Blocks::parse(manifest.block_size, file)
        {
            match self.assemble_blocks(
                &url,
                &destination,
                &part,
                &blocks,
                file,
                control,
                stats,
                &mut credited,
                event,
            ) {
                Ok(stamp) => {
                    ready = true;
                    verified_stamp = stamp;
                }
                Err(TransferError::Cancelled) => return Err(TransferError::Cancelled),
                Err(TransferError::Io(error)) => return Err(TransferError::Io(error)),
                // A server without Range support falls back to the verified prefix.
                Err(_) => (),
            }
        }
        if !ready {
            let mut last = None;
            for attempt in 0..3 {
                control.checkpoint()?;
                match self.download_whole(
                    &url,
                    &part,
                    file,
                    control,
                    stats,
                    &mut credited,
                    event,
                    buffer,
                ) {
                    Ok(stamp) => {
                        ready = true;
                        verified_stamp = stamp;
                        break;
                    }
                    Err(TransferError::Cancelled) => return Err(TransferError::Cancelled),
                    Err(TransferError::Io(error)) => return Err(TransferError::Io(error)),
                    Err(error) => {
                        if matches!(error, TransferError::Integrity(_)) {
                            remove_regular(&part)?;
                            set_credit(stats, &mut credited, 0);
                        }
                        last = Some(error);
                        if attempt < 2 {
                            control.delay(Duration::from_millis(300 * (1 << attempt)))?;
                        }
                    }
                }
            }
            if !ready {
                return Err(last.unwrap_or_else(|| TransferError::Integrity(file.path.clone())));
            }
        }
        control.checkpoint()?;
        // Every successful path above has verified the complete file, including donors
        // and block reconstruction. Do not reread multi-gigabyte files a second time.
        debug_assert!(ready);
        if verified_stamp
            .as_ref()
            .is_some_and(|stamp| !stamp.still_matches(&part))
        {
            return Err(TransferError::Integrity(file.path.clone()));
        }
        if let Err(error) = atomic_replace(&part, &destination) {
            if cfg!(windows) && deferred::is_locked(&error) {
                return deferred::save_pending(request, &part, file);
            }
            return Err(error);
        }
        hashes
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .remember_verified(&destination, file, verified_stamp);
        #[cfg(unix)]
        if file.executable {
            use std::os::unix::fs::PermissionsExt;
            let mut permissions = fs::metadata(&destination)?.permissions();
            permissions.set_mode(permissions.mode() | 0o111);
            fs::set_permissions(&destination, permissions)?;
        }
        stats.files.fetch_add(1, Ordering::Relaxed);
        stats.emit(TransferStage::Downloading, &file.path, event, false);
        Ok(())
    }

    #[allow(clippy::too_many_arguments)] // Explicit per-transfer IO/progress context, same as block assembly.
    fn download_whole(
        &self,
        url: &Url,
        part: &Path,
        file: &ManifestFile,
        control: &TransferControl,
        stats: &Progress,
        credited: &mut u64,
        event: &(dyn Fn(TransferEvent) + Sync),
        buffer: &mut [u8],
    ) -> Result<Option<VerifiedStamp>> {
        let mut offset = fs::metadata(part).map(|m| m.len()).unwrap_or(0);
        if offset >= file.size && part.exists() {
            if offset == file.size {
                let verified = hash_cache::verify_for_promotion(part, file, control)?;
                if verified.0 {
                    open_regular(part, true, false, false)?.sync_all()?;
                    set_credit(stats, credited, file.size);
                    return Ok(verified.1);
                }
            }
            open_regular(part, true, false, true)?;
            offset = 0;
        }
        set_credit(stats, credited, offset);
        control.checkpoint()?;
        let mut request = self
            .client
            .get(url.clone())
            .header(ACCEPT_ENCODING, "identity");
        if offset > 0 {
            request = request.header(RANGE, format!("bytes={offset}-"));
        }
        let mut response = request.send()?;
        if response.status() == StatusCode::RANGE_NOT_SATISFIABLE {
            // The manifest is authoritative; a stale partial must not loop on 416.
            if part.exists() {
                open_regular(part, true, false, true)?;
            }
            set_credit(stats, credited, 0);
            return Err(TransferError::Protocol(
                "server rejected resume range".into(),
            ));
        }
        if response.status() == StatusCode::PARTIAL_CONTENT {
            check_content_range(&response, offset, file.size.saturating_sub(1), file.size)?;
        } else if response.status() == StatusCode::OK {
            offset = 0;
            set_credit(stats, credited, 0);
            if response.content_length().is_some_and(|n| n != file.size) {
                return Err(TransferError::Protocol("incorrect content length".into()));
            }
        } else {
            return Err(TransferError::Network(format!(
                "HTTP {}",
                response.status()
            )));
        }
        let mut output = open_regular(part, true, true, offset == 0)?;
        output.set_len(offset)?;
        let mut digests = FileHashes::new(file);
        let mut prefix = offset;
        while prefix != 0 {
            control.checkpoint()?;
            let n = prefix.min(buffer.len() as u64) as usize;
            output.read_exact(&mut buffer[..n])?;
            digests.update(&buffer[..n]);
            prefix -= n as u64;
        }
        let capacity = (file.size - offset).min(BUFFER as u64) as usize;
        let mut output = BufWriter::with_capacity(capacity, output);
        let result = (|| {
            loop {
                control.checkpoint()?;
                let n = response
                    .read(buffer)
                    .map_err(|e| TransferError::Network(e.to_string()))?;
                if n == 0 {
                    break;
                }
                stats.received(n as u64, control)?;
                if n as u64 > file.size - offset {
                    return Err(TransferError::Protocol(
                        "response exceeds manifest size".into(),
                    ));
                }
                output.write_all(&buffer[..n])?;
                digests.update(&buffer[..n]);
                offset += n as u64;
                set_credit(stats, credited, offset);
                stats.emit(TransferStage::Downloading, &file.path, event, false);
            }
            Ok(())
        })();
        // Commit the buffered prefix even on cancellation or a broken response;
        // the next attempt reads and hashes those bytes before appending to them.
        let output = output.into_inner().map_err(|err| err.into_error())?;
        result?;
        if offset != file.size {
            return Err(TransferError::Network("truncated response".into()));
        }
        digests.seal(output, part, file, control)
    }

    #[allow(clippy::too_many_arguments)]
    fn assemble_blocks(
        &self,
        url: &Url,
        old: &Path,
        part: &Path,
        blocks: &Blocks,
        file: &ManifestFile,
        control: &TransferControl,
        stats: &Progress,
        credited: &mut u64,
        event: &(dyn Fn(TransferEvent) + Sync),
    ) -> Result<Option<VerifiedStamp>> {
        let mut buffer = vec![0; blocks.block_size];
        let mut digests = FileHashes::new(file);
        let (mut next, output) = blocks.verify_prefix(part, &mut buffer, &mut digests, control)?;
        let mut position = blocks.offset(next);
        set_credit(stats, credited, position);
        if next == blocks.count() {
            return digests.seal(output.expect("verified block prefix"), part, file, control);
        }
        let offsets = blocks.find_in_old(old, control)?;
        let mut source = open_regular(old, false, false, false)?;
        let mut output = match output {
            Some(file) => file,
            None => open_regular(part, true, true, true)?,
        };
        output.seek(SeekFrom::Start(position))?;
        while next < blocks.count() {
            control.checkpoint()?;
            let length = blocks.length(next);
            if let Some(offset) = offsets[next] {
                source.seek(SeekFrom::Start(offset))?;
                if source.read_exact(&mut buffer[..length]).is_ok()
                    && blocks.matches(next, &buffer[..length])
                {
                    output.write_all(&buffer[..length])?;
                    digests.update(&buffer[..length]);
                    position += length as u64;
                    stats.reused.fetch_add(length as u64, Ordering::Relaxed);
                    set_credit(stats, credited, position);
                    next += 1;
                    stats.emit(TransferStage::Downloading, &file.path, event, false);
                    continue;
                }
            }
            let first = next;
            let mut end = next + 1;
            while end < blocks.count() && end - first < 32 && offsets[end].is_none() {
                end += 1;
            }
            let end_offset = blocks.offset(end) - 1;
            let mut failures = 0;
            while next < end {
                control.checkpoint()?;
                let attempt_start = next;
                let result = (|| -> Result<()> {
                    let start = blocks.offset(next);
                    let mut response = self
                        .client
                        .get(url.clone())
                        .header(ACCEPT_ENCODING, "identity")
                        .header(RANGE, format!("bytes={start}-{end_offset}"))
                        .send()?;
                    if response.status() != StatusCode::PARTIAL_CONTENT {
                        return Err(TransferError::Protocol("block Range unsupported".into()));
                    }
                    check_content_range(&response, start, end_offset, blocks.file_size)?;
                    while next < end {
                        let length = blocks.length(next);
                        let mut read = 0;
                        while read < length {
                            control.checkpoint()?;
                            let n = response
                                .read(&mut buffer[read..length])
                                .map_err(|e| TransferError::Network(e.to_string()))?;
                            if n == 0 {
                                return Err(TransferError::Network("short block response".into()));
                            }
                            stats.received(n as u64, control)?;
                            read += n;
                        }
                        if !blocks.matches(next, &buffer[..length]) {
                            return Err(TransferError::Integrity(file.path.clone()));
                        }
                        output.write_all(&buffer[..length])?;
                        digests.update(&buffer[..length]);
                        position += length as u64;
                        set_credit(stats, credited, position);
                        next += 1;
                        stats.emit(TransferStage::Downloading, &file.path, event, false);
                    }
                    let mut extra = [0];
                    if response
                        .read(&mut extra)
                        .map_err(|e| TransferError::Network(e.to_string()))?
                        != 0
                    {
                        return Err(TransferError::Protocol("oversized range response".into()));
                    }
                    Ok(())
                })();
                match result {
                    Ok(()) => (),
                    Err(
                        e @ (TransferError::Cancelled
                        | TransferError::Io(_)
                        | TransferError::Protocol(_)),
                    ) => return Err(e),
                    Err(e) => {
                        // A weak connection that delivers another verified block is making
                        // progress. Only consecutive failures without progress exhaust retries.
                        failures = if next > attempt_start {
                            0
                        } else {
                            failures + 1
                        };
                        if failures >= 3 {
                            return Err(e);
                        }
                        control.delay(Duration::from_millis(300 * failures.max(1)))?;
                    }
                }
            }
        }
        digests.seal(output, part, file, control)
    }
}

fn set_credit(stats: &Progress, credited: &mut u64, now: u64) {
    if now >= *credited {
        stats
            .completed
            .fetch_add(now - *credited, Ordering::Relaxed);
    } else {
        stats
            .completed
            .fetch_sub(*credited - now, Ordering::Relaxed);
    }
    *credited = now;
}

fn check_content_range(
    response: &reqwest::blocking::Response,
    from: u64,
    to: u64,
    total: u64,
) -> Result<()> {
    let actual = response
        .headers()
        .get(CONTENT_RANGE)
        .and_then(|s| s.to_str().ok());
    let expected = format!("bytes {from}-{to}/{total}");
    if actual != Some(expected.as_str())
        || to < from
        || response
            .content_length()
            .is_some_and(|length| length != to - from + 1)
    {
        return Err(TransferError::Protocol(format!(
            "invalid Content-Range: {actual:?}"
        )));
    }
    Ok(())
}

struct Blocks {
    block_size: usize,
    file_size: u64,
    digests: Vec<u8>,
}
impl Blocks {
    fn parse(block_size: u64, file: &ManifestFile) -> Option<Self> {
        if !(64 * 1024..=64 * 1024 * 1024).contains(&block_size) {
            return None;
        }
        let count = file.size.div_ceil(block_size);
        let encoded = file.blocks.as_ref()?;
        if count < 2 || count.checked_mul(16)? > MAX_MANIFEST {
            return None;
        }
        let digests = base64::engine::general_purpose::STANDARD
            .decode(encoded)
            .ok()?;
        if digests.len() as u64 != count * 16 {
            return None;
        }
        Some(Self {
            block_size: block_size as usize,
            file_size: file.size,
            digests,
        })
    }
    fn count(&self) -> usize {
        self.digests.len() / 16
    }
    fn offset(&self, index: usize) -> u64 {
        ((index as u64) * self.block_size as u64).min(self.file_size)
    }
    fn length(&self, index: usize) -> usize {
        (self.file_size - self.offset(index)).min(self.block_size as u64) as usize
    }
    fn weak(&self, index: usize) -> u64 {
        u64::from_le_bytes(self.digests[index * 16..index * 16 + 8].try_into().unwrap())
    }
    fn matches(&self, index: usize, data: &[u8]) -> bool {
        data.len() == self.length(index)
            && Sha256::digest(data)[..8] == self.digests[index * 16 + 8..index * 16 + 16]
    }
    fn verify_prefix(
        &self,
        part: &Path,
        buffer: &mut [u8],
        digests: &mut FileHashes,
        control: &TransferControl,
    ) -> Result<(usize, Option<fs::File>)> {
        let mut file = match open_regular(part, true, false, false) {
            Ok(file) => file,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok((0, None)),
            Err(error) => return Err(error.into()),
        };
        let size = file.metadata()?.len();
        let mut good = 0;
        while good < self.count() {
            control.checkpoint()?;
            let length = self.length(good);
            if self.offset(good) + length as u64 > size {
                break;
            }
            file.read_exact(&mut buffer[..length])?;
            if !self.matches(good, &buffer[..length]) {
                break;
            }
            digests.update(&buffer[..length]);
            good += 1;
        }
        file.set_len(self.offset(good))?;
        Ok((good, Some(file)))
    }
    fn find_in_old(&self, path: &Path, control: &TransferControl) -> Result<Vec<Option<u64>>> {
        self.find_in_old_counted(path, control, 1024 * 1024 * 1024)
            .map(|(offsets, _)| offsets)
    }
    fn find_in_old_counted(
        &self,
        path: &Path,
        control: &TransferControl,
        false_byte_budget: u64,
    ) -> Result<(Vec<Option<u64>>, u64)> {
        let mut offsets = vec![None; self.count()];
        let mut source = open_regular(path, false, false, false)?;
        let size = source.metadata()?.len();
        // Group identical blocks once. A zero-filled archive can contain thousands
        // of copies: one match fills that whole group. Keep its digest so later
        // copies can still be skipped without walking the list of resolved indices.
        let mut groups: HashMap<(u64, [u8; 8]), Vec<usize>> = HashMap::new();
        for i in 0..self.count() {
            if self.length(i) == self.block_size {
                let strong = self.digests[i * 16 + 8..i * 16 + 16].try_into().unwrap();
                groups.entry((self.weak(i), strong)).or_default().push(i);
            }
        }
        let mut remaining = groups.len();
        let mut wanted: HashMap<u64, HashMap<[u8; 8], Vec<usize>>> = HashMap::new();
        for ((weak, strong), indices) in groups {
            wanted.entry(weak).or_default().insert(strong, indices);
        }
        let mut inspected = 0;
        if size >= self.block_size as u64 && !wanted.is_empty() {
            let mut bits = 16usize;
            while bits < 24 && (1usize << bits) < wanted.len().saturating_mul(1024) {
                bits += 1;
            }
            let shift = 64 - bits;
            let mut mask = vec![0u64; 1 << (bits - 6)];
            for weak in wanted.keys() {
                let key = (weak >> shift) as usize;
                mask[key >> 6] |= 1u64 << (key & 63);
            }
            let window = self.block_size;
            // A contiguous window avoids a full-window copy on every hash hit.
            let read_ahead = (window * 4).min(4 * 1024 * 1024);
            let mut buffer = vec![0u8; window + read_ahead];
            let mut filled = read_available(&mut source, &mut buffer)?;
            if filled < window {
                return Ok((offsets, 0));
            }
            let mut pos = 0;
            let mut base = 0u64;
            let mut hash = rolling(&buffer[..window]);
            let factor = PRIME.wrapping_pow(window as u32 - 1);
            let mut false_bytes = 0u64;
            let mut aligned_only = false;
            loop {
                if inspected % BUFFER as u64 == 0 {
                    control.checkpoint()?;
                }
                inspected += 1;
                let key = (hash >> shift) as usize;
                let mut matched = false;
                if mask[key >> 6] & (1u64 << (key & 63)) != 0
                    && let Some(candidates) = wanted.get_mut(&hash)
                {
                    let strong: [u8; 8] = Sha256::digest(&buffer[pos..pos + window])[..8]
                        .try_into()
                        .unwrap();
                    if let Some(indices) = candidates.get_mut(&strong) {
                        if !indices.is_empty() {
                            for index in indices.drain(..) {
                                offsets[index] = Some(base + pos as u64);
                            }
                            remaining -= 1;
                        }
                        matched = true;
                    } else {
                        // Polynomial weak hashes can collide adversarially.
                        // After at most 1 GiB of fruitless SHA work, fall back
                        // to block steps; the final file hash remains mandatory.
                        false_bytes = false_bytes.saturating_add(window as u64);
                        aligned_only |= false_bytes >= false_byte_budget;
                    }
                    if remaining == 0 {
                        break;
                    }
                }
                let step = if matched || aligned_only { window } else { 1 };
                let outgoing = buffer[pos];
                pos += step;
                if pos + window > filled {
                    let keep = filled - pos;
                    buffer.copy_within(pos..filled, 0);
                    base += pos as u64;
                    pos = 0;
                    filled = keep + read_available(&mut source, &mut buffer[keep..])?;
                    control.checkpoint()?;
                    if filled < window {
                        break;
                    }
                }
                if step == 1 {
                    hash = hash
                        .wrapping_sub((outgoing as u64).wrapping_mul(factor))
                        .wrapping_mul(PRIME)
                        .wrapping_add(buffer[pos + window - 1] as u64);
                } else {
                    hash = rolling(&buffer[pos..pos + window]);
                }
            }
        }
        // The short last block normally remains at EOF or at its original offset.
        // Do not rescan gigabytes with a second, smaller rolling window.
        let last = self.count() - 1;
        let tail = self.length(last);
        if tail < self.block_size && size >= tail as u64 {
            let mut buffer = vec![0; tail];
            for position in [size - tail as u64, self.offset(last)] {
                control.checkpoint()?;
                if position + tail as u64 > size {
                    continue;
                }
                source.seek(SeekFrom::Start(position))?;
                source.read_exact(&mut buffer)?;
                if self.matches(last, &buffer) {
                    offsets[last] = Some(position);
                    break;
                }
            }
        }
        Ok((offsets, inspected))
    }
}

fn rolling(bytes: &[u8]) -> u64 {
    bytes.iter().fold(0u64, |hash, byte| {
        hash.wrapping_mul(PRIME).wrapping_add(*byte as u64)
    })
}
fn hash_worker_count() -> usize {
    std::thread::available_parallelism().map_or(1, |n| n.get().min(HASH_WORKERS))
}

fn download_order(downloads: &[ManifestFile]) -> Vec<usize> {
    let mut order: Vec<_> = (0..downloads.len()).collect();
    // Start large transfers early and retain manifest order for equal sizes.
    order.sort_by_key(|&index| Reverse(downloads[index].size));
    order
}

fn required_transfer_space(
    downloads: &[ManifestFile],
    allocations: &[(u64, u64)],
    workers: usize,
) -> u64 {
    if workers == 1 {
        let mut used = 0i128;
        let mut peak = 0i128;
        for index in download_order(downloads) {
            let (remaining, old) = allocations[index];
            used += i128::from(remaining);
            peak = peak.max(used);
            // The next file cannot start before this one is promoted.
            used -= i128::from(old);
        }
        return u64::try_from(peak).unwrap_or(u64::MAX);
    }

    // Parallel completion may differ from the scheduled order. Count all
    // positive growth, plus the largest simultaneous old/new file overlaps.
    let mut growth = 0;
    let mut overlap = Vec::with_capacity(allocations.len());
    for &(remaining, old) in allocations {
        growth += remaining.saturating_sub(old);
        overlap.push(remaining.min(old));
    }
    overlap.sort_unstable_by(|a, b| b.cmp(a));
    growth + overlap.into_iter().take(workers).sum::<u64>()
}

fn allocated_part_bytes(destination: &Path) -> u64 {
    let mut name = destination.file_name().unwrap().to_os_string();
    name.push(".part");
    let part = destination.with_file_name(name);
    let Ok(file) = open_regular(&part, false, false, false) else {
        return 0;
    };
    file.metadata()
        .map_or(0, |meta| allocated_bytes(&file, &meta))
}

fn allocated_bytes(_file: &fs::File, metadata: &fs::Metadata) -> u64 {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        use std::os::windows::io::AsRawHandle;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_ATTRIBUTE_COMPRESSED, FILE_ATTRIBUTE_SPARSE_FILE, FILE_STANDARD_INFO,
            FileStandardInfo, GetFileInformationByHandleEx,
        };
        if metadata.file_attributes() & (FILE_ATTRIBUTE_COMPRESSED | FILE_ATTRIBUTE_SPARSE_FILE)
            != 0
        {
            return 0;
        }
        let mut info = FILE_STANDARD_INFO::default();
        let ok = unsafe {
            GetFileInformationByHandleEx(
                _file.as_raw_handle().cast(),
                FileStandardInfo,
                (&mut info as *mut FILE_STANDARD_INFO).cast(),
                size_of::<FILE_STANDARD_INFO>() as u32,
            )
        };
        if ok != 0 && info.NumberOfLinks == 1 {
            return u64::try_from(info.AllocationSize)
                .unwrap_or(0)
                .min(metadata.len());
        }
    }
    #[cfg(unix)]
    {
        use std::os::unix::fs::MetadataExt;
        if metadata.nlink() == 1 {
            return metadata.blocks().saturating_mul(512).min(metadata.len());
        }
    }
    0
}
fn read_available(source: &mut fs::File, buffer: &mut [u8]) -> io::Result<usize> {
    let mut read = 0;
    while read < buffer.len() {
        match source.read(&mut buffer[read..]) {
            Ok(0) => break,
            Ok(n) => read += n,
            Err(e) if e.kind() == io::ErrorKind::Interrupted => continue,
            Err(e) => return Err(e),
        }
    }
    Ok(read)
}

fn folded(paths: &[String]) -> HashSet<String> {
    paths.iter().map(|p| p.to_lowercase()).collect()
}
fn acquire_transfer_lock(root: &Path) -> Result<fs::File> {
    let lock = open_regular(&root.join(".chillhub.lock"), true, true, false).map_err(|error| {
        if cfg!(windows) && matches!(error.raw_os_error(), Some(32 | 33)) {
            TransferError::Busy
        } else {
            TransferError::Io(error)
        }
    })?;
    lock.try_lock_exclusive().map_err(|error| {
        if error.raw_os_error() == fs2::lock_contended_error().raw_os_error() {
            TransferError::Busy
        } else {
            TransferError::Io(error)
        }
    })?;
    Ok(lock)
}
fn ensure_not_running(root: &Path) -> Result<()> {
    #[cfg(windows)]
    {
        if !crate::playtime::processes_in(root)
            .map_err(|e| TransferError::Protocol(e.to_string()))?
            .is_empty()
        {
            return Err(TransferError::Busy);
        }
    }
    #[cfg(not(windows))]
    let _ = root;
    Ok(())
}
fn path_in(root: &Path, path: &str) -> Result<PathBuf> {
    safe_join(root, path).map_err(TransferError::UnsafePath)
}
fn validate_request(request: &TransferRequest) -> Result<()> {
    manifest::check_no_links(&request.root)
        .map_err(|e| TransferError::UnsafePath(e.to_string()))?;
    checked_url(&request.manifest_url)?;
    checked_url(&request.content_base_url)?;
    for path in request
        .previous_owned_paths
        .iter()
        .chain(&request.foreign_paths)
        .chain(&request.preserve_paths)
        .chain(request.approved_cleanup_files.iter().map(|file| &file.path))
    {
        manifest::validate_relative(path).map_err(TransferError::UnsafePath)?;
    }
    for donor in &request.donor_roots {
        manifest::check_no_links(donor).map_err(|e| TransferError::UnsafePath(e.to_string()))?;
    }
    Ok(())
}
fn checked_url(address: &str) -> Result<Url> {
    let url = Url::parse(address).map_err(|e| TransferError::Protocol(e.to_string()))?;
    if !matches!(url.scheme(), "https" | "http")
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
    {
        return Err(TransferError::Protocol("unsupported content URL".into()));
    }
    Ok(url)
}
fn file_url(base: &str, relative: &str) -> Result<Url> {
    let mut url = checked_url(base)?;
    url.set_query(None);
    url.set_fragment(None);
    url.path_segments_mut()
        .map_err(|_| TransferError::Protocol("invalid content base".into()))?
        .pop_if_empty()
        .extend(relative.split('/'));
    Ok(url)
}

pub fn verify_file(
    path: &Path,
    expected: &ManifestFile,
    control: &TransferControl,
) -> Result<bool> {
    hash_cache::verify_uncached(path, expected, control)
}
fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn collect_files(root: &Path) -> Result<Vec<String>> {
    let mut files = Vec::new();
    for entry in walkdir::WalkDir::new(root).follow_links(false).min_depth(1) {
        let entry = entry.map_err(|e| TransferError::Io(io::Error::other(e)))?;
        let meta = fs::symlink_metadata(entry.path())?;
        if manifest::is_reparse(&meta) {
            return Err(TransferError::UnsafePath(
                entry.path().display().to_string(),
            ));
        }
        if meta.is_file() {
            let relative = entry
                .path()
                .strip_prefix(root)
                .map_err(|e| TransferError::UnsafePath(e.to_string()))?
                .to_str()
                .ok_or_else(|| TransferError::UnsafePath("invalid filename".into()))?
                .replace('\\', "/");
            // Unrelated NTFS names may be legal on disk but unaddressable by our
            // canonical path contract. Leave them untouched instead of blocking an
            // otherwise valid update. Published paths and reparse points stay strict.
            if manifest::validate_relative(&relative).is_err() {
                continue;
            }
            files.push(relative);
        }
    }
    Ok(files)
}

pub fn atomic_write(path: &Path, contents: &[u8]) -> Result<()> {
    let parent = path
        .parent()
        .ok_or_else(|| TransferError::UnsafePath("missing parent".into()))?;
    let _guard = DirectoryGuard::acquire(parent, true)?;
    let tmp = parent.join(format!(".chillhub-write-{}.tmp", uuid::Uuid::new_v4()));
    let result = (|| {
        let mut file = open_regular(&tmp, true, true, true)?;
        file.write_all(contents)?;
        file.sync_all()?;
        drop(file);
        atomic_replace(&tmp, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(tmp);
    }
    result
}

pub fn atomic_replace(source: &Path, destination: &Path) -> Result<()> {
    manifest::check_no_links(source).map_err(|e| TransferError::UnsafePath(e.to_string()))?;
    manifest::check_no_links(destination).map_err(|e| TransferError::UnsafePath(e.to_string()))?;
    #[cfg(windows)]
    {
        use windows_sys::Win32::Storage::FileSystem::{
            MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
        };
        let src = windows_move_path(source);
        let dst = windows_move_path(destination);
        let replace = || unsafe {
            MoveFileExW(
                src.as_ptr(),
                dst.as_ptr(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
        };
        if replace() == 0 {
            let first = io::Error::last_os_error();
            let old = fs::metadata(destination)
                .ok()
                .map(|meta| meta.permissions());
            if first.kind() != io::ErrorKind::PermissionDenied
                || !old.as_ref().is_some_and(|p| p.readonly())
            {
                return Err(first.into());
            }
            let mut writable = old.clone().unwrap();
            #[allow(clippy::permissions_set_readonly_false)] // This entire branch is Windows-only.
            writable.set_readonly(false);
            fs::set_permissions(destination, writable)?;
            if replace() == 0 {
                let failure = io::Error::last_os_error();
                // Failed activation must not silently change the surviving old file.
                let _ = fs::set_permissions(destination, old.unwrap());
                return Err(failure.into());
            }
        }
    }
    #[cfg(not(windows))]
    {
        fs::rename(source, destination)?;
        fs::File::open(destination.parent().unwrap())?.sync_all()?;
    }
    Ok(())
}

#[cfg(windows)]
fn windows_move_path(path: &Path) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    let slash = u16::from(b'\\');
    let mut wide: Vec<_> = path
        .as_os_str()
        .encode_wide()
        .map(|ch| if ch == u16::from(b'/') { slash } else { ch })
        .collect();
    let verbatim = [slash, slash, u16::from(b'?'), slash];
    // Rust's std::fs handles extended Windows paths internally. MoveFileExW
    // needs the prefix supplied by us on systems without long-path opt-in.
    if wide.len() >= 259 && !wide.starts_with(&verbatim) {
        let mut extended = verbatim.to_vec();
        if wide.starts_with(&[slash, slash]) {
            extended.extend(r"UNC\".encode_utf16());
            extended.extend(&wide[2..]);
        } else {
            extended.extend(&wide);
        }
        wide = extended;
    }
    wide.push(0);
    wide
}

fn drive_label(path: &Path) -> String {
    path.ancestors()
        .last()
        .unwrap_or(path)
        .display()
        .to_string()
}
fn disk_error(error: TransferError, root: &Path, remaining: u64) -> TransferError {
    let error = antivirus_error(error, "игры");
    let TransferError::Io(io_error) = &error else {
        return error;
    };
    let windows_disk_full = cfg!(windows) && matches!(io_error.raw_os_error(), Some(39 | 112));
    if io_error.kind() != io::ErrorKind::StorageFull && !windows_disk_full {
        return error;
    }
    let available = fs2::available_space(root).unwrap_or(0);
    TransferError::NoSpace {
        drive: drive_label(root),
        available,
        required: available.saturating_add(remaining.max(1)),
    }
}
fn antivirus_error(error: TransferError, path: &str) -> TransferError {
    if cfg!(windows)
        && matches!(&error, TransferError::Io(error) if matches!(error.raw_os_error(), Some(225 | 226)))
    {
        TransferError::AntivirusBlocked(path.to_owned())
    } else {
        error
    }
}
fn display_size(bytes: u64) -> String {
    let (amount, unit) = if bytes >= 1024 * 1024 * 1024 {
        (bytes as f64 / (1024.0 * 1024.0 * 1024.0), "ГБ")
    } else if bytes >= 1024 * 1024 {
        (bytes as f64 / (1024.0 * 1024.0), "МБ")
    } else {
        (bytes as f64 / 1024.0, "КБ")
    };
    format!("{:.1} {unit}", amount.max(0.1)).replace('.', ",")
}

fn remove_regular(path: &Path) -> Result<()> {
    let meta = match fs::symlink_metadata(path) {
        Ok(m) => m,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(e) => return Err(e.into()),
    };
    if manifest::is_reparse(&meta) || !meta.is_file() {
        return Err(TransferError::UnsafePath(path.display().to_string()));
    }
    #[cfg(windows)]
    if meta.permissions().readonly() {
        let mut permissions = meta.permissions();
        #[allow(clippy::permissions_set_readonly_false)]
        // Windows DOS attribute, never Unix mode bits.
        permissions.set_readonly(false);
        fs::set_permissions(path, permissions)?;
    }
    fs::remove_file(path)?;
    Ok(())
}

fn write_install_markers(request: &TransferRequest, manifest: &Manifest) -> Result<()> {
    match request.scope {
        SyncScope::WholeRoot => {
            atomic_write(&request.root.join(".version"), request.version.as_bytes())
        }
        SyncScope::OwnFilesOnly => {
            let bytes = serde_json::to_vec(manifest)
                .map_err(|e| TransferError::InvalidManifest(e.to_string()))?;
            atomic_write(&request.root.join(".mods.manifest.json"), &bytes)?;
            let mut files: Vec<_> = manifest.files.iter().collect();
            files.sort_by(|a, b| a.path.as_bytes().cmp(b.path.as_bytes()));
            let mut hash = Sha256::new();
            for file in files {
                hash.update(file.path.as_bytes());
                hash.update(b"\n");
                hash.update(file.blake3.as_bytes());
                hash.update(b"\n");
            }
            atomic_write(
                &request.root.join(".mods.revision"),
                hex(&hash.finalize()[..16]).as_bytes(),
            )?;
            atomic_write(
                &request.root.join(".mods.version"),
                request.version.as_bytes(),
            )
        }
    }
}

fn prune_deleted_parents(root: &Path, deleted: &[PathBuf], keep: &[String]) {
    let keep = folded(keep);
    for file in deleted {
        let mut current = file.parent();
        while let Some(dir) = current {
            if dir == root {
                break;
            }
            let Ok(relative) = dir.strip_prefix(root) else {
                break;
            };
            let relative = relative.to_string_lossy().replace('\\', "/").to_lowercase();
            if relative == "freetp"
                || keep.contains(&relative)
                || manifest::check_no_links(dir).is_err()
                || fs::remove_dir(dir).is_err()
            {
                break;
            }
            current = dir.parent();
        }
    }
}

struct Progress {
    completed: AtomicU64,
    network: AtomicU64,
    reused: AtomicU64,
    files: AtomicUsize,
    total: u64,
    file_count: usize,
    last_event: Mutex<Instant>,
    throttle: Mutex<RateBucket>,
    limit: u64,
}
struct RateBucket {
    tokens: f64,
    updated: Instant,
    rate: u64,
}
impl Progress {
    fn new(total: u64, file_count: usize, limit: u64) -> Self {
        Self {
            completed: AtomicU64::new(0),
            network: AtomicU64::new(0),
            reused: AtomicU64::new(0),
            files: AtomicUsize::new(0),
            total,
            file_count,
            last_event: Mutex::new(Instant::now()),
            throttle: Mutex::new(RateBucket {
                tokens: 0.0,
                updated: Instant::now(),
                rate: limit,
            }),
            limit,
        }
    }
    fn emit(
        &self,
        stage: TransferStage,
        file: &str,
        callback: &(dyn Fn(TransferEvent) + Sync),
        force: bool,
    ) {
        let mut last = if force {
            self.last_event.lock().unwrap_or_else(|e| e.into_inner())
        } else if let Ok(last) = self.last_event.try_lock() {
            last
        } else {
            return;
        };
        if !force && last.elapsed() < PROGRESS_INTERVAL {
            return;
        }
        *last = Instant::now();
        callback(TransferEvent {
            stage,
            current_file: file.into(),
            completed_bytes: self.completed.load(Ordering::Relaxed).min(self.total),
            total_bytes: self.total,
            network_bytes: self.network.load(Ordering::Relaxed),
            reused_bytes: self.reused.load(Ordering::Relaxed),
            completed_files: self.files.load(Ordering::Relaxed),
            total_files: self.file_count,
        });
    }
    fn received(&self, bytes: u64, control: &TransferControl) -> Result<()> {
        self.network.fetch_add(bytes, Ordering::Relaxed);
        loop {
            control.checkpoint()?;
            let rate = control.speed_limit(self.limit);
            let delay = {
                let mut bucket = self.throttle.lock().unwrap_or_else(|e| e.into_inner());
                let now = Instant::now();
                if rate == 0 {
                    bucket.tokens = 0.0;
                    bucket.updated = now;
                    bucket.rate = 0;
                    return Ok(());
                }
                if bucket.rate != rate {
                    // Switching limits never carries an old reservation in time.
                    // All workers observe the current value on the next 50 ms tick.
                    bucket.tokens = bucket.tokens.min(rate as f64).max(0.0);
                    bucket.updated = now;
                    bucket.rate = rate;
                }
                let elapsed = now.saturating_duration_since(bucket.updated).as_secs_f64();
                bucket.tokens = (bucket.tokens + elapsed * rate as f64).min(rate as f64);
                bucket.updated = now;
                let enough = bytes.min(rate) as f64;
                if bucket.tokens >= enough {
                    bucket.tokens -= bytes as f64;
                    return Ok(());
                }
                Duration::from_secs_f64((enough - bucket.tokens) / rate as f64)
                    .clamp(Duration::from_millis(1), Duration::from_millis(50))
            };
            control.delay(delay)?;
        }
    }
}

#[cfg(test)]
#[path = "transfer/scanner_tests.rs"]
mod scanner_tests;
