//! A single operation queue with durable intent and resumable file transfers.
use crate::transfer::{
    SyncScope, TransferControl, TransferEngine, TransferError, TransferEvent, TransferOutcome,
    TransferRequest, atomic_write,
};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::{self, Read, Write},
    path::{Path, PathBuf},
    sync::{
        Arc, Condvar, Mutex,
        mpsc::{self, Receiver, Sender},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

const MAX_QUEUE_BYTES: usize = 16 * 1024 * 1024;
const COMPLETED_HISTORY: usize = 50;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum QueueState {
    Waiting,
    Running,
    Pausing,
    Paused,
    Cancelling,
    Completed,
    Failed,
    Cancelled,
    NeedsConfirmation,
}
impl QueueState {
    fn terminal(self) -> bool {
        matches!(self, Self::Completed | Self::Failed | Self::Cancelled)
    }
}
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct QueueItem {
    pub id: String,
    pub request: TransferRequest,
    pub state: QueueState,
    pub progress: Option<TransferEvent>,
    pub outcome: Option<TransferOutcome>,
    pub error: Option<String>,
    pub cleanup_paths: Vec<String>,
    #[serde(default)]
    pub report: QueueReport,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum TransferOperation {
    Install,
    Update,
    Verify,
}
#[derive(Debug, Clone, Default, Serialize, Deserialize)]
#[serde(default)]
pub struct QueueReport {
    pub operation: Option<TransferOperation>,
    pub duration_ms: u64,
    pub files_total: usize,
    pub files_downloaded: usize,
    pub full_bytes: u64,
    pub planned_bytes: u64,
    pub hash_mismatches: usize,
    pub error_code: Option<String>,
    pub transient_error: bool,
}
impl QueueReport {
    pub fn metric_event(&self, scope: SyncScope) -> Option<&'static str> {
        match self.operation? {
            TransferOperation::Verify => Some("integrity_check"),
            TransferOperation::Install if scope == SyncScope::WholeRoot => Some("game_install"),
            TransferOperation::Update if scope == SyncScope::WholeRoot => Some("game_update"),
            _ => None,
        }
    }
    fn begin(request: &TransferRequest) -> Self {
        let marker = if request.scope == SyncScope::OwnFilesOnly {
            ".mods.version"
        } else {
            ".version"
        };
        Self {
            operation: Some(if request.force_rehash {
                TransferOperation::Verify
            } else if crate::launch::read_marker(&request.root, marker).is_empty() {
                TransferOperation::Install
            } else {
                TransferOperation::Update
            }),
            ..Self::default()
        }
    }
    fn failure(&mut self, error: &TransferError) {
        self.transient_error = matches!(
            error,
            TransferError::Network(_)
                | TransferError::Busy
                | TransferError::NoSpace { .. }
                | TransferError::AntivirusBlocked(_)
                | TransferError::Cancelled
                | TransferError::NeedsCleanupConfirmation(_)
        );
        self.error_code = match error {
            TransferError::Network(_) => Some("network"),
            TransferError::InvalidManifest(_) | TransferError::UnsafePath(_) => {
                Some("manifest_invalid")
            }
            TransferError::NoSpace { .. } => Some("no_disk_space"),
            TransferError::AntivirusBlocked(_) => Some("sync_security_blocked"),
            TransferError::Integrity(_) => Some("hash_mismatch"),
            TransferError::Io(_) => Some("sync_io"),
            TransferError::Protocol(_) => Some("sync_failed"),
            _ => None,
        }
        .map(str::to_owned);
        if matches!(error, TransferError::Busy) {
            self.operation = None;
        }
    }
}
#[derive(Debug, Clone)]
pub enum QueueEvent {
    Changed(Vec<QueueItem>),
    Finished(Box<QueueItem>),
    PersistenceError(String),
}
struct Active {
    id: String,
    control: TransferControl,
    requeue: bool,
    last_progress: Instant,
}
struct State {
    items: Vec<QueueItem>,
    active: Option<Active>,
    subscribers: Vec<Sender<QueueEvent>>,
    stopping: bool,
    logged_states: std::collections::HashMap<String, QueueState>,
    recovery_error: Option<String>,
    speed_limit: Option<u64>,
}
struct Shared {
    state: Mutex<State>,
    wake: Condvar,
    path: PathBuf,
}
pub struct DownloadQueue {
    shared: Arc<Shared>,
    worker: Mutex<Option<JoinHandle<()>>>,
}

impl DownloadQueue {
    /// Restored unfinished work is paused until the user explicitly resumes it.
    pub fn new(state_file: impl AsRef<Path>) -> Result<Self, TransferError> {
        let path = state_file.as_ref().to_path_buf();
        crate::manifest::check_no_links(&path)
            .map_err(|e| TransferError::UnsafePath(e.to_string()))?;
        let (mut items, recovery_error) = match read_items(&path) {
            Ok(items) => (items, None),
            Err(error) => {
                if let Some(directory) = path.parent() {
                    crate::diagnostics::log(directory, "download.recovery", &error.to_string());
                }
                (vec![], Some("Не удалось открыть сохранённую очередь. Файл оставлен без изменений; новые загрузки временно недоступны.".to_owned()))
            }
        };
        for item in &mut items {
            if !item.state.terminal() && item.state != QueueState::NeedsConfirmation {
                item.state = QueueState::Paused;
            }
        }
        compact_completed(&mut items);
        let shared = Arc::new(Shared {
            state: Mutex::new(State {
                items,
                active: None,
                subscribers: vec![],
                stopping: false,
                logged_states: Default::default(),
                recovery_error,
                speed_limit: None,
            }),
            wake: Condvar::new(),
            path,
        });
        let mut engine = TransferEngine::new()?;
        if let Some(directory) = shared.path.parent() {
            engine = engine.with_hash_cache_dir(directory.join("hashcache"));
        }
        let worker_shared = shared.clone();
        let worker = thread::Builder::new()
            .name("chillhub-download-queue".into())
            .spawn(move || work(worker_shared, engine))?;
        Ok(Self {
            shared,
            worker: Mutex::new(Some(worker)),
        })
    }
    pub fn snapshot(&self) -> Vec<QueueItem> {
        self.shared
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .items
            .clone()
    }
    /// Resolves filesystem aliases; call from an operation/background worker.
    /// Exact paths still match when a removable drive is currently unavailable.
    pub fn has_pending_for_root(&self, root: &Path) -> bool {
        let pending: Vec<PathBuf> = self
            .shared
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .items
            .iter()
            .filter(|item| !item.state.terminal())
            .map(|item| item.request.root.clone())
            .collect();
        if pending.iter().any(|path| path == root) {
            return true;
        }
        if pending.is_empty() {
            return false;
        }
        let Some(identity) = crate::paths::path_identity(root) else {
            return false;
        };
        pending
            .iter()
            .any(|path| crate::paths::path_identity(path).as_deref() == Some(identity.as_str()))
    }
    pub fn recovery_error(&self) -> Option<String> {
        self.shared
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .recovery_error
            .clone()
    }
    /// Applies immediately to active work and to every job started afterwards.
    /// Persisted request snapshots remain intact; the setting lives in AppConfig.
    pub fn set_speed_limit(&self, bytes_per_second: u64) {
        let mut state = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
        state.speed_limit = Some(bytes_per_second);
        if let Some(active) = &state.active {
            active.control.set_speed_limit(bytes_per_second);
        }
    }
    pub fn subscribe(&self) -> Receiver<QueueEvent> {
        let (tx, rx) = mpsc::channel();
        let mut state = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
        let _ = tx.send(QueueEvent::Changed(state.items.clone()));
        if let Some(error) = &state.recovery_error {
            let _ = tx.send(QueueEvent::PersistenceError(error.clone()));
        }
        state.subscribers.push(tx);
        rx
    }
    pub fn enqueue(&self, request: TransferRequest) -> Result<String, TransferError> {
        let root_identity = crate::paths::path_identity(&request.root);
        let mut state = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
        if state.stopping {
            return Err(TransferError::Cancelled);
        }
        require_writable(&state)?;
        if let Some(existing) = state.items.iter().find(|item| {
            !item.state.terminal()
                && item.request.scope == request.scope
                && item.request.game_id.eq_ignore_ascii_case(&request.game_id)
                && (item.request.root == request.root
                    || root_identity.as_deref().is_some_and(|identity| {
                        crate::paths::path_identity(&item.request.root).as_deref() == Some(identity)
                    }))
        }) {
            if existing.request.version != request.version
                || existing.request.manifest_url != request.manifest_url
                || existing.request.content_base_url != request.content_base_url
                || existing.request.depends_on != request.depends_on
            {
                return Err(TransferError::Busy);
            }
            return Ok(existing.id.clone());
        }
        let id = uuid::Uuid::new_v4().to_string();
        state.items.push(QueueItem {
            id: id.clone(),
            request,
            state: QueueState::Waiting,
            progress: None,
            outcome: None,
            error: None,
            cleanup_paths: vec![],
            report: QueueReport::default(),
        });
        if let Err(e) = persist(&self.shared.path, &mut state) {
            state.items.pop();
            return Err(e);
        }
        publish(&mut state);
        self.shared.wake.notify_all();
        Ok(id)
    }
    pub fn pause(&self, id: &str) -> Result<(), TransferError> {
        self.change(id, |state, index| {
            if let Some(active) = state.active.as_mut().filter(|a| a.id == id) {
                active.requeue = false;
                active.control.cancel();
                state.items[index].state = QueueState::Pausing;
            } else if state.items[index].state == QueueState::Waiting {
                state.items[index].state = QueueState::Paused;
            }
        })
    }
    pub fn resume(&self, id: &str) -> Result<(), TransferError> {
        self.change(id, |state, index| {
            if let Some(active) = state.active.as_mut().filter(|a| a.id == id) {
                if state.items[index].state == QueueState::Pausing {
                    active.requeue = true;
                }
            } else if matches!(
                state.items[index].state,
                QueueState::Paused | QueueState::Failed | QueueState::Cancelled
            ) {
                state.items[index].state = QueueState::Waiting;
                state.items[index].error = None;
                state.items[index].progress = None;
            }
        })
    }
    pub fn confirm_cleanup(&self, id: &str) -> Result<(), TransferError> {
        self.change(id, |state, index| {
            if state.items[index].state == QueueState::NeedsConfirmation {
                state.items[index].request.allow_cleanup = true;
                state.items[index].state = QueueState::Waiting;
                state.items[index].error = None;
            }
        })
    }
    pub fn cancel(&self, id: &str) -> Result<(), TransferError> {
        self.change(id, |state, index| {
            if let Some(active) = state.active.as_mut().filter(|a| a.id == id) {
                active.requeue = false;
                active.control.cancel();
                state.items[index].state = QueueState::Cancelling;
            } else if !state.items[index].state.terminal() {
                state.items[index].state = QueueState::Cancelled;
            }
        })
    }
    pub fn remove(&self, id: &str) -> Result<(), TransferError> {
        let mut state = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
        if state.active.as_ref().is_some_and(|a| a.id == id) {
            return Err(TransferError::Busy);
        }
        let before = state.items.clone();
        if state
            .items
            .iter()
            .any(|item| item.id == id && item.state == QueueState::Completed)
        {
            for item in &mut state.items {
                if item.request.depends_on.as_deref() == Some(id) {
                    item.request.depends_on = None;
                }
            }
        }
        state.items.retain(|item| item.id != id);
        if let Err(error) = persist(&self.shared.path, &mut state) {
            state.items = before;
            return Err(error);
        }
        publish(&mut state);
        self.shared.wake.notify_all();
        Ok(())
    }
    /// Passing `None` moves the item to the end; moving ahead of an active job preempts it.
    pub fn move_before(&self, id: &str, before: Option<&str>) -> Result<(), TransferError> {
        let mut state = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
        let Some(index) = state.items.iter().position(|i| i.id == id) else {
            return Ok(());
        };
        if state.active.as_ref().is_some_and(|a| a.id == id) || before == Some(id) {
            return Ok(());
        }
        let item = state.items.remove(index);
        let position = before
            .and_then(|target| state.items.iter().position(|i| i.id == target))
            .unwrap_or(state.items.len());
        state.items.insert(position, item);
        if state.items[position].state == QueueState::Waiting {
            let active_position = state
                .active
                .as_ref()
                .and_then(|a| state.items.iter().position(|i| i.id == a.id));
            if active_position
                .is_some_and(|p| position < p && state.items[p].state == QueueState::Running)
            {
                if let Some(active) = &mut state.active {
                    active.requeue = true;
                    active.control.cancel();
                }
                if let Some(index) = active_position {
                    state.items[index].state = QueueState::Pausing;
                }
            }
        }
        persist(&self.shared.path, &mut state)?;
        publish(&mut state);
        self.shared.wake.notify_all();
        Ok(())
    }
    /// Call off the UI thread when all file handles must be closed before exiting.
    pub fn shutdown(&self) {
        stop(&self.shared);
        if let Some(worker) = self.worker.lock().unwrap_or_else(|e| e.into_inner()).take() {
            let _ = worker.join();
        }
    }
    fn change(
        &self,
        id: &str,
        update: impl FnOnce(&mut State, usize),
    ) -> Result<(), TransferError> {
        let mut state = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
        let Some(index) = state.items.iter().position(|i| i.id == id) else {
            return Ok(());
        };
        let was_terminal = state.items[index].state.terminal();
        update(&mut state, index);
        let finished = (!was_terminal && state.items[index].state.terminal())
            .then(|| state.items[index].clone());
        persist(&self.shared.path, &mut state)?;
        if let Some(item) = finished {
            publish_finished(&mut state, item);
        }
        publish(&mut state);
        self.shared.wake.notify_all();
        Ok(())
    }
}
impl Drop for DownloadQueue {
    fn drop(&mut self) {
        stop(&self.shared);
    }
}

fn stop(shared: &Shared) {
    let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
    state.stopping = true;
    if let Some(active) = &mut state.active {
        active.requeue = false;
        active.control.cancel();
    }
    for item in &mut state.items {
        if matches!(
            item.state,
            QueueState::Running | QueueState::Pausing | QueueState::Waiting
        ) {
            item.state = QueueState::Paused;
        }
    }
    if state.recovery_error.is_none() {
        let _ = persist(&shared.path, &mut state);
    }
    publish(&mut state);
    shared.wake.notify_all();
}
fn read_items(path: &Path) -> io::Result<Vec<QueueItem>> {
    let file = match fs::File::open(path) {
        Ok(file) => file,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(vec![]),
        Err(error) => return Err(error),
    };
    if file.metadata()?.len() > MAX_QUEUE_BYTES as u64 {
        return Err(queue_size_error());
    }
    let mut bytes = Vec::new();
    file.take(MAX_QUEUE_BYTES as u64 + 1)
        .read_to_end(&mut bytes)?;
    if bytes.len() > MAX_QUEUE_BYTES {
        return Err(queue_size_error());
    }
    serde_json::from_slice(&bytes).map_err(io::Error::other)
}
fn queue_size_error() -> io::Error {
    io::Error::other(
        "Очередь слишком большая. Уберите ненужные задачи из списка загрузок и попробуйте снова.",
    )
}
fn require_writable(state: &State) -> Result<(), TransferError> {
    match &state.recovery_error {
        Some(error) => Err(TransferError::Io(io::Error::other(error.clone()))),
        None => Ok(()),
    }
}
fn clear_completed_paths(item: &mut QueueItem) {
    item.request.previous_owned_paths = Vec::new();
    item.request.foreign_paths = Vec::new();
    item.request.preserve_paths = Vec::new();
    item.request.donor_roots = Vec::new();
}
fn compact_completed(items: &mut Vec<QueueItem>) {
    let mut remaining = items
        .iter()
        .filter(|item| item.state == QueueState::Completed)
        .count()
        .saturating_sub(COMPLETED_HISTORY);
    let mut removed = std::collections::HashSet::new();
    items.retain_mut(|item| {
        if item.state != QueueState::Completed {
            return true;
        }
        clear_completed_paths(item);
        item.progress = None;
        item.cleanup_paths = Vec::new();
        if remaining == 0 {
            return true;
        }
        remaining -= 1;
        removed.insert(item.id.clone());
        false
    });
    for item in items {
        if item
            .request
            .depends_on
            .as_ref()
            .is_some_and(|id| removed.contains(id))
        {
            item.request.depends_on = None;
        }
    }
}
fn persist(path: &Path, state: &mut State) -> Result<(), TransferError> {
    require_writable(state)?;
    let mut items = state.items.clone();
    compact_completed(&mut items);
    struct BoundedBytes(Vec<u8>);
    impl Write for BoundedBytes {
        fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
            if self.0.len().saturating_add(bytes.len()) > MAX_QUEUE_BYTES {
                return Err(queue_size_error());
            }
            self.0.extend_from_slice(bytes);
            Ok(bytes.len())
        }
        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }
    let mut bytes = BoundedBytes(Vec::new());
    serde_json::to_writer(&mut bytes, &items)
        .map_err(|error| TransferError::Io(io::Error::other(error)))?;
    atomic_write(path, &bytes.0)?;
    state.items = items;
    Ok(())
}
fn publish_finished(state: &mut State, mut item: QueueItem) {
    // Terminal events keep outcome/progress for metrics, but not file inventories.
    clear_completed_paths(&mut item);
    item.cleanup_paths = Vec::new();
    let event = QueueEvent::Finished(Box::new(item));
    state
        .subscribers
        .retain(|sender| sender.send(event.clone()).is_ok());
}
fn publish(state: &mut State) {
    let event = QueueEvent::Changed(state.items.clone());
    state
        .subscribers
        .retain(|sender| sender.send(event.clone()).is_ok());
}
fn save_and_publish(shared: &Shared, state: &mut State) {
    if let Some(directory) = shared.path.parent() {
        for item in &state.items {
            if state.logged_states.insert(item.id.clone(), item.state) != Some(item.state) {
                crate::diagnostics::event(
                    directory,
                    "download.state",
                    serde_json::json!({
                        "task": item.id, "game": item.request.game_id, "version": item.request.version,
                        "state": item.state, "error": item.error
                    }),
                );
            }
        }
        state
            .logged_states
            .retain(|id, _| state.items.iter().any(|i| &i.id == id));
    }
    if let Err(error) = persist(&shared.path, state) {
        if let Some(directory) = shared.path.parent() {
            crate::diagnostics::log(directory, "download.persistence_error", &error.to_string());
        }
        let event = QueueEvent::PersistenceError(error.to_string());
        state
            .subscribers
            .retain(|sender| sender.send(event.clone()).is_ok());
    }
    publish(state);
}
fn work(shared: Arc<Shared>, engine: TransferEngine) {
    loop {
        let (id, request, control) = {
            let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
            if settle_dependencies(&mut state) {
                save_and_publish(&shared, &mut state);
            }
            while !state.stopping && !state.items.iter().any(|i| runnable(i, &state.items)) {
                state = shared.wake.wait(state).unwrap_or_else(|e| e.into_inner());
                if settle_dependencies(&mut state) {
                    save_and_publish(&shared, &mut state);
                }
            }
            if state.stopping {
                return;
            }
            let index = state
                .items
                .iter()
                .position(|i| runnable(i, &state.items))
                .unwrap();
            let item = &mut state.items[index];
            item.state = QueueState::Running;
            item.error = None;
            item.progress = None;
            item.report = QueueReport::default();
            let id = item.id.clone();
            let request = item.request.clone();
            let control = TransferControl::default();
            if let Some(limit) = state.speed_limit {
                control.set_speed_limit(limit);
            }
            state.active = Some(Active {
                id: id.clone(),
                control: control.clone(),
                requeue: false,
                last_progress: Instant::now(),
            });
            save_and_publish(&shared, &mut state);
            (id, request, control)
        };
        let started = Instant::now();
        let report = QueueReport::begin(&request);
        {
            let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
            if let Some(item) = state.items.iter_mut().find(|i| i.id == id) {
                item.report = report;
            }
        }
        let callback = |progress: TransferEvent| {
            let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
            let mut stage_changed = false;
            if let Some(item) = state.items.iter_mut().find(|i| i.id == id) {
                stage_changed = item
                    .progress
                    .as_ref()
                    .is_none_or(|previous| previous.stage != progress.stage);
                if stage_changed && let Some(directory) = shared.path.parent() {
                    crate::diagnostics::event(
                        directory,
                        "download.stage",
                        serde_json::json!({
                            "task": id, "game": request.game_id, "stage": progress.stage
                        }),
                    );
                }
                item.progress = Some(progress);
            }
            let should_publish = state.active.as_mut().is_some_and(|active| {
                if stage_changed || active.last_progress.elapsed() >= Duration::from_millis(100) {
                    active.last_progress = Instant::now();
                    true
                } else {
                    false
                }
            });
            if should_publish {
                publish(&mut state);
            }
        };
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let plan = engine.plan(&request, &control, &callback)?;
            {
                let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
                if let Some(item) = state.items.iter_mut().find(|i| i.id == id) {
                    item.report.files_total = plan.manifest.files.len();
                    item.report.files_downloaded = plan.downloads.len();
                    item.report.full_bytes = plan.total_manifest_bytes;
                    item.report.planned_bytes = plan.total_download_bytes;
                    item.report.hash_mismatches = plan.hash_mismatches;
                }
            }
            engine.execute(&request, &plan, &control, &callback)
        }))
        .unwrap_or_else(|_| {
            Err(TransferError::Protocol(
                "worker interrupted unexpectedly".into(),
            ))
        });
        let mut state = shared.state.lock().unwrap_or_else(|e| e.into_inner());
        let requeue = state.active.take().is_some_and(|a| a.requeue);
        let stopping = state.stopping;
        let mut finished = None;
        if let Some(item) = state.items.iter_mut().find(|i| i.id == id) {
            item.report.duration_ms = started.elapsed().as_millis().min(u64::MAX as u128) as u64;
            if let Err(error) = &result {
                item.report.failure(error);
            }
            if stopping {
                item.state = QueueState::Paused;
            } else if requeue {
                item.state = QueueState::Waiting;
                item.error = None;
            } else if item.state == QueueState::Pausing {
                item.state = QueueState::Paused;
            } else if item.state == QueueState::Cancelling {
                item.state = QueueState::Cancelled;
            } else {
                match result {
                    Ok(outcome) => {
                        item.state = QueueState::Completed;
                        item.outcome = Some(outcome);
                        item.cleanup_paths.clear();
                    }
                    Err(TransferError::NeedsCleanupConfirmation(paths)) => {
                        item.state = QueueState::NeedsConfirmation;
                        item.cleanup_paths = paths;
                    }
                    Err(TransferError::Cancelled) => item.state = QueueState::Cancelled,
                    Err(error) => {
                        item.state = QueueState::Failed;
                        item.error = Some(error.to_string());
                    }
                }
            }
            if item.state.terminal() {
                finished = Some(item.clone());
            }
        }
        save_and_publish(&shared, &mut state);
        if let Some(item) = finished {
            publish_finished(&mut state, item);
        }
    }
}

fn runnable(item: &QueueItem, all: &[QueueItem]) -> bool {
    item.state == QueueState::Waiting
        && item.request.depends_on.as_ref().is_none_or(|id| {
            all.iter()
                .any(|dependency| dependency.id == *id && dependency.state == QueueState::Completed)
        })
}

fn settle_dependencies(state: &mut State) -> bool {
    let mut changes = Vec::new();
    for (index, item) in state.items.iter().enumerate() {
        if item.state != QueueState::Waiting {
            continue;
        }
        let mut seen = std::collections::HashSet::new();
        seen.insert(item.id.as_str());
        let mut dependency = item.request.depends_on.as_deref();
        while let Some(id) = dependency {
            if !seen.insert(id) {
                changes.push((
                    index,
                    "Задачи зависят друг от друга. Добавьте загрузку заново",
                ));
                break;
            }
            match state.items.iter().find(|i| i.id == id) {
                None => {
                    changes.push((
                        index,
                        "Предыдущая загрузка отсутствует. Сначала установите игру",
                    ));
                    break;
                }
                Some(parent)
                    if matches!(parent.state, QueueState::Failed | QueueState::Cancelled) =>
                {
                    changes.push((
                        index,
                        "Предыдущая загрузка не завершена. Сначала завершите установку игры",
                    ));
                    break;
                }
                Some(parent) if parent.state == QueueState::Completed => break,
                Some(parent) => dependency = parent.request.depends_on.as_deref(),
            }
        }
    }
    let changed = !changes.is_empty();
    for (index, error) in changes {
        state.items[index].state = QueueState::Paused;
        state.items[index].error = Some(error.into());
    }
    changed
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::transfer::TransferStage;

    #[test]
    fn moving_another_job_never_requeues_cancelled_or_user_paused_work() {
        for stopping in [
            QueueState::Cancelling,
            QueueState::Pausing,
            QueueState::Running,
        ] {
            let temp = tempfile::tempdir().unwrap();
            let mut state = state(vec![
                item("active", stopping),
                item("waiting", QueueState::Waiting),
            ]);
            state.active = Some(Active {
                id: "active".into(),
                control: TransferControl::default(),
                requeue: false,
                last_progress: Instant::now(),
            });
            let queue = DownloadQueue {
                shared: Arc::new(Shared {
                    state: Mutex::new(state),
                    wake: Condvar::new(),
                    path: temp.path().join("queue.json"),
                }),
                worker: Mutex::new(None),
            };
            queue.move_before("waiting", Some("active")).unwrap();
            let state = queue.shared.state.lock().unwrap();
            assert_eq!(state.items[0].id, "waiting");
            assert_eq!(
                state.active.as_ref().unwrap().requeue,
                stopping == QueueState::Running
            );
            assert_eq!(
                state.items[1].state,
                if stopping == QueueState::Running {
                    QueueState::Pausing
                } else {
                    stopping
                }
            );
        }
    }

    #[test]
    fn queue_reporting_classifies_expected_outages_without_duplicate_mod_installs() {
        let mut report = QueueReport {
            operation: Some(TransferOperation::Install),
            ..Default::default()
        };
        assert_eq!(
            report.metric_event(SyncScope::WholeRoot),
            Some("game_install")
        );
        assert_eq!(report.metric_event(SyncScope::OwnFilesOnly), None);
        report.failure(&TransferError::Network("offline".into()));
        assert!(report.transient_error);
        assert_eq!(report.error_code.as_deref(), Some("network"));
        report.failure(&TransferError::Integrity("game.dll".into()));
        assert!(!report.transient_error);
        assert_eq!(report.error_code.as_deref(), Some("hash_mismatch"));
        report.failure(&TransferError::AntivirusBlocked("mod.dll".into()));
        assert!(report.transient_error);
        assert_eq!(report.error_code.as_deref(), Some("sync_security_blocked"));
        report.failure(&TransferError::Busy);
        assert_eq!(report.metric_event(SyncScope::WholeRoot), None);
        report.operation = Some(TransferOperation::Verify);
        assert_eq!(
            report.metric_event(SyncScope::OwnFilesOnly),
            Some("integrity_check")
        );
    }

    fn item(id: &str, state: QueueState) -> QueueItem {
        QueueItem {
            id: id.into(),
            state,
            request: TransferRequest {
                game_id: "test".into(),
                version: "1".into(),
                manifest_url: "file:///never-requested".into(),
                previous_owned_paths: vec!["BepInEx/plugins/owned.dll".into()],
                foreign_paths: vec!["Game_Data/shared.dat".into()],
                preserve_paths: vec!["Saves".into()],
                donor_roots: vec![PathBuf::from("donor")],
                ..Default::default()
            },
            progress: Some(TransferEvent {
                stage: TransferStage::Completed,
                current_file: "last.dll".into(),
                completed_bytes: 7,
                total_bytes: 7,
                network_bytes: 7,
                reused_bytes: 0,
                completed_files: 1,
                total_files: 1,
            }),
            outcome: Some(TransferOutcome {
                network_bytes: 7,
                ..Default::default()
            }),
            error: None,
            cleanup_paths: vec!["old.dll".into()],
            report: QueueReport::default(),
        }
    }
    fn state(items: Vec<QueueItem>) -> State {
        State {
            items,
            active: None,
            subscribers: vec![],
            stopping: false,
            logged_states: Default::default(),
            recovery_error: None,
            speed_limit: None,
        }
    }

    #[test]
    fn completed_history_is_bounded_without_losing_retry_data_or_dependencies() {
        let temp = tempfile::tempdir().unwrap();
        let mut items = (0..57)
            .map(|i| item(&format!("done-{i}"), QueueState::Completed))
            .collect::<Vec<_>>();
        items.push(item("failed", QueueState::Failed));
        items.push(item("cancelled", QueueState::Cancelled));
        let mut dependent = item("waiting-for-done", QueueState::Paused);
        dependent.request.depends_on = Some("done-0".into());
        items.push(dependent);
        let mut retry = item("waiting-for-failed", QueueState::Paused);
        retry.request.depends_on = Some("failed".into());
        items.push(retry);
        let mut state = state(items);
        persist(&temp.path().join("queue.json"), &mut state).unwrap();
        assert_eq!(state.items.len(), 54);
        assert_eq!(state.items[0].id, "done-7");
        for item in state
            .items
            .iter()
            .filter(|i| i.state == QueueState::Completed)
        {
            assert!(item.request.previous_owned_paths.is_empty());
            assert_eq!(item.request.foreign_paths.capacity(), 0);
            assert!(item.request.preserve_paths.is_empty() && item.request.donor_roots.is_empty());
            assert!(item.progress.is_none() && item.cleanup_paths.is_empty());
            assert_eq!(item.outcome.as_ref().unwrap().network_bytes, 7);
        }
        for id in [
            "failed",
            "cancelled",
            "waiting-for-done",
            "waiting-for-failed",
        ] {
            let item = state.items.iter().find(|i| i.id == id).unwrap();
            assert_eq!(
                item.request.previous_owned_paths,
                ["BepInEx/plugins/owned.dll"]
            );
            assert!(
                !item.request.foreign_paths.is_empty() && !item.request.preserve_paths.is_empty()
            );
            assert!(!item.request.donor_roots.is_empty() && !item.cleanup_paths.is_empty());
        }
        assert!(
            state
                .items
                .iter()
                .find(|i| i.id == "waiting-for-done")
                .unwrap()
                .request
                .depends_on
                .is_none()
        );
        assert_eq!(
            state
                .items
                .iter()
                .find(|i| i.id == "waiting-for-failed")
                .unwrap()
                .request
                .depends_on
                .as_deref(),
            Some("failed")
        );
        assert!(
            fs::metadata(temp.path().join("queue.json")).unwrap().len() <= MAX_QUEUE_BYTES as u64
        );
    }

    #[test]
    fn oversized_existing_queue_preserves_original_and_keeps_startup_available() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("queue.json");
        fs::write(&path, b"original-queue-preserved").unwrap();
        fs::OpenOptions::new()
            .write(true)
            .open(&path)
            .unwrap()
            .set_len(MAX_QUEUE_BYTES as u64 + 1)
            .unwrap();
        assert!(read_items(&path).is_err());
        let queue = DownloadQueue::new(&path).unwrap();
        assert!(queue.recovery_error().is_some());
        assert!(queue.snapshot().is_empty());
        let events = queue.subscribe();
        assert!(matches!(events.recv().unwrap(), QueueEvent::Changed(_)));
        assert!(matches!(
            events.recv().unwrap(),
            QueueEvent::PersistenceError(_)
        ));
        assert!(
            queue
                .enqueue(TransferRequest::default())
                .unwrap_err()
                .to_string()
                .contains("очередь")
        );
        queue.shutdown();
        drop(queue);
        assert_eq!(
            fs::metadata(&path).unwrap().len(),
            MAX_QUEUE_BYTES as u64 + 1
        );
        let mut prefix = [0; b"original-queue-preserved".len()];
        fs::File::open(&path)
            .unwrap()
            .read_exact(&mut prefix)
            .unwrap();
        assert_eq!(&prefix, b"original-queue-preserved");
        assert!(
            fs::read_to_string(temp.path().join("logs/launcher.log"))
                .unwrap()
                .contains("download.recovery")
        );
    }

    #[cfg(windows)]
    #[test]
    fn restored_queue_matches_short_and_canonical_ancestors_before_game_exists() {
        let temp = tempfile::tempdir().unwrap();
        let library = temp.path().join("Long library directory for queued games");
        fs::create_dir_all(&library).unwrap();
        let short = crate::paths::short_test_path(&library);
        let canonical = fs::canonicalize(&library).unwrap();
        let path = temp.path().join("queue.json");
        let mut paused = item("paused", QueueState::Paused);
        paused.request.root = short.join("not-installed-yet");
        fs::write(&path, serde_json::to_vec(&vec![paused.clone()]).unwrap()).unwrap();
        let queue = DownloadQueue::new(&path).unwrap();
        for alias in [&library, &canonical] {
            let mut request = paused.request.clone();
            request.root = alias.join("not-installed-yet");
            assert!(queue.has_pending_for_root(&request.root));
            assert_eq!(queue.enqueue(request.clone()).unwrap(), "paused");
            request.version = "another-version".into();
            assert!(matches!(queue.enqueue(request), Err(TransferError::Busy)));
        }
        assert!(!queue.has_pending_for_root(&library.join("different-game")));
        assert_eq!(queue.snapshot().len(), 1);
        assert_eq!(queue.snapshot()[0].state, QueueState::Paused);
        assert!(!library.join("not-installed-yet").exists());
        queue.shutdown();
    }

    #[test]
    fn unresolved_queue_identities_never_match_other_unknown_paths() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("queue.json");
        let mut paused = item("paused", QueueState::Paused);
        paused.request.root = PathBuf::from("unknown-one");
        fs::write(&path, serde_json::to_vec(&vec![paused]).unwrap()).unwrap();
        let queue = DownloadQueue::new(&path).unwrap();
        assert!(queue.has_pending_for_root(Path::new("unknown-one")));
        assert!(!queue.has_pending_for_root(Path::new("unknown-two")));
        queue.shutdown();
    }

    #[test]
    fn oversized_enqueue_rolls_back_and_failed_job_remains_retryable() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("queue.json");
        let mut failed = item("retry", QueueState::Failed);
        failed.request.root = temp.path().join("game");
        fs::write(&path, serde_json::to_vec(&vec![failed]).unwrap()).unwrap();
        let queue = DownloadQueue::new(&path).unwrap();
        let before = fs::read(&path).unwrap();
        assert!(
            queue
                .enqueue(TransferRequest {
                    version: "x".repeat(MAX_QUEUE_BYTES),
                    ..Default::default()
                })
                .is_err()
        );
        assert_eq!(fs::read(&path).unwrap(), before);
        assert_eq!(queue.snapshot().len(), 1);
        let events = queue.subscribe();
        queue.resume("retry").unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            assert!(Instant::now() < deadline, "retry did not finish");
            if let Ok(QueueEvent::Finished(item)) = events.recv_timeout(Duration::from_millis(100))
            {
                assert_eq!(item.id, "retry");
                assert_eq!(item.state, QueueState::Failed);
                break;
            }
        }
        let retried = queue.snapshot();
        assert_eq!(
            retried[0].request.previous_owned_paths,
            ["BepInEx/plugins/owned.dll"]
        );
        assert_eq!(retried[0].request.foreign_paths, ["Game_Data/shared.dat"]);
        assert_eq!(retried[0].request.preserve_paths, ["Saves"]);
        assert_eq!(retried[0].request.donor_roots, [PathBuf::from("donor")]);
        queue.shutdown();
    }

    #[test]
    fn finished_event_is_emitted_once_on_transition_and_never_on_restore() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("queue.json");
        fs::write(
            &path,
            serde_json::to_vec(&vec![
                item("done", QueueState::Completed),
                item("paused", QueueState::Paused),
            ])
            .unwrap(),
        )
        .unwrap();
        let queue = DownloadQueue::new(&path).unwrap();
        let events = queue.subscribe();
        assert!(matches!(events.recv().unwrap(), QueueEvent::Changed(_)));
        assert!(events.try_recv().is_err());
        queue.cancel("paused").unwrap();
        queue.cancel("paused").unwrap();
        let completed = events
            .try_iter()
            .filter_map(|event| {
                if let QueueEvent::Finished(item) = event {
                    Some(item)
                } else {
                    None
                }
            })
            .collect::<Vec<_>>();
        assert_eq!(completed.len(), 1);
        assert_eq!(completed[0].id, "paused");
        assert_eq!(completed[0].state, QueueState::Cancelled);
        assert_eq!(completed[0].progress.as_ref().unwrap().total_files, 1);
        assert!(
            !queue
                .snapshot()
                .iter()
                .find(|i| i.id == "paused")
                .unwrap()
                .request
                .previous_owned_paths
                .is_empty()
        );
        queue.shutdown();
    }
}
