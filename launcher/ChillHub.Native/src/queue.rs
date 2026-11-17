//! A single operation queue with durable intent and resumable file transfers.
use crate::transfer::{
    CleanupFile, SyncScope, TransferControl, TransferEngine, TransferError, TransferEvent,
    TransferOutcome, TransferRequest, atomic_write,
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
const DEPENDENCY_INTERRUPTED: &str = "dependency_interrupted";
const PREVIOUS_DOWNLOAD_INCOMPLETE: &str =
    "Предыдущая загрузка не завершена. Сначала завершите установку игры";
const PREVIOUS_DOWNLOAD_MISSING: &str = "Предыдущая загрузка отсутствует. Сначала установите игру";
const DEPENDENCY_CYCLE: &str = "Задачи зависят друг от друга. Добавьте загрузку заново";

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
    #[serde(default)]
    pub cleanup_files: Vec<CleanupFile>,
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
                | TransferError::ReplacementDeferred(_)
                | TransferError::PendingReplacement
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
            TransferError::ReplacementDeferred(_) => Some("replacement_deferred"),
            TransferError::PendingReplacement => Some("pending_replacement"),
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
            item.request.approved_cleanup_files.clear();
            if !item.state.terminal()
                && (item.state != QueueState::NeedsConfirmation || item.cleanup_files.is_empty())
            {
                pause_for_shutdown(item);
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
    /// Explicit user recovery preserves unreadable metadata. Game files and
    /// partial downloads are never touched.
    pub fn reset_unreadable(&self) -> Result<(), TransferError> {
        let mut state = self
            .shared
            .state
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        if state.recovery_error.is_none() {
            return Ok(());
        }
        if state.stopping || state.active.is_some() {
            return Err(TransferError::Busy);
        }
        // Another process may have repaired the file while the dialog was open.
        // Adopt that queue on pause rather than replace healthy work with [] .
        if let Ok(mut items) = read_items(&self.shared.path) {
            for item in &mut items {
                item.request.approved_cleanup_files.clear();
                if !item.state.terminal()
                    && (item.state != QueueState::NeedsConfirmation
                        || item.cleanup_files.is_empty())
                {
                    pause_for_shutdown(item);
                }
            }
            state.items = items;
            state.recovery_error = None;
            publish(&mut state);
            return Ok(());
        }
        let parent = self
            .shared
            .path
            .parent()
            .ok_or_else(|| TransferError::UnsafePath("queue parent missing".into()))?;
        let _guard = crate::manifest::DirectoryGuard::acquire(parent, true)?;
        let backup = parent.join(format!(
            "downloads-unreadable-{}.json",
            uuid::Uuid::new_v4()
        ));
        let archived = self.shared.path.try_exists()?;
        if archived {
            drop(crate::manifest::open_regular(
                &self.shared.path,
                false,
                false,
                false,
            )?);
            crate::transfer::atomic_replace(&self.shared.path, &backup)?;
        }
        let previous_error = state.recovery_error.take();
        if let Err(error) = persist(&self.shared.path, &mut state) {
            state.recovery_error = previous_error;
            if archived
                && let Err(restore_error) =
                    crate::transfer::atomic_replace(&backup, &self.shared.path)
            {
                crate::diagnostics::log(
                    parent,
                    "download.recovery-restore",
                    &restore_error.to_string(),
                );
            }
            return Err(error);
        }
        publish(&mut state);
        self.shared.wake.notify_all();
        Ok(())
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
            cleanup_files: vec![],
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
                state.items[index].state = QueueState::Pausing;
            } else if state.items[index].state == QueueState::Waiting {
                state.items[index].state = QueueState::Paused;
            }
            Ok(())
        })
    }
    pub fn resume(&self, id: &str) -> Result<(), TransferError> {
        self.change(id, |state, index| {
            // An automatically paused modpack is a continuation of its base install.
            // Validate the chain before mutating state or persisting any work.
            let chain = resume_chain(&state.items, index)?;
            for index in chain.into_iter().rev() {
                let item = &mut state.items[index];
                if let Some(active) = state.active.as_mut().filter(|active| active.id == item.id) {
                    if item.state == QueueState::Pausing {
                        active.requeue = true;
                        item.request.approved_cleanup_files.clear();
                    }
                } else if matches!(
                    item.state,
                    QueueState::Paused | QueueState::Failed | QueueState::Cancelled
                ) {
                    queue_again(item);
                    let resumed = item.id.clone();
                    queue_interrupted_dependents(&mut state.items, resumed);
                }
            }
            Ok(())
        })
    }
    pub fn confirm_cleanup(
        &self,
        id: &str,
        displayed: &[CleanupFile],
    ) -> Result<(), TransferError> {
        let mut confirmed = false;
        self.change(id, |state, index| {
            let item = &mut state.items[index];
            if item.state == QueueState::NeedsConfirmation
                && !displayed.is_empty()
                && item.cleanup_files == displayed
            {
                item.request.approved_cleanup_files = std::mem::take(&mut item.cleanup_files);
                item.state = QueueState::Waiting;
                item.error = None;
                confirmed = true;
            }
            Ok(())
        })?;
        if !confirmed {
            return Err(TransferError::CleanupConfirmationChanged);
        }
        Ok(())
    }
    pub fn cancel(&self, id: &str) -> Result<(), TransferError> {
        self.change(id, |state, index| {
            if let Some(active) = state.active.as_mut().filter(|a| a.id == id) {
                active.requeue = false;
                state.items[index].state = QueueState::Cancelling;
            } else if !state.items[index].state.terminal() {
                state.items[index].state = QueueState::Cancelled;
            }
            Ok(())
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
        let previous_items = state.items.clone();
        let previous_requeue = state.active.as_ref().map(|active| active.requeue);
        let item = state.items.remove(index);
        let position = before
            .and_then(|target| state.items.iter().position(|i| i.id == target))
            .unwrap_or(state.items.len());
        state.items.insert(position, item);
        if runnable(&state.items[position], &state.items) {
            let active_position = state
                .active
                .as_ref()
                .and_then(|a| state.items.iter().position(|i| i.id == a.id));
            if active_position
                .is_some_and(|p| position < p && state.items[p].state == QueueState::Running)
            {
                if let Some(active) = &mut state.active {
                    active.requeue = true;
                }
                if let Some(index) = active_position {
                    state.items[index].state = QueueState::Pausing;
                }
            }
        }
        persist_transition(
            &self.shared.path,
            &mut state,
            previous_items,
            previous_requeue,
        )?;
        cancel_stopping_active(&state);
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
        update: impl FnOnce(&mut State, usize) -> Result<(), TransferError>,
    ) -> Result<(), TransferError> {
        let mut state = self.shared.state.lock().unwrap_or_else(|e| e.into_inner());
        let Some(index) = state.items.iter().position(|i| i.id == id) else {
            return Ok(());
        };
        let previous_items = state.items.clone();
        let previous_requeue = state.active.as_ref().map(|active| active.requeue);
        let was_terminal = state.items[index].state.terminal();
        update(&mut state, index)?;
        let finished = (!was_terminal && state.items[index].state.terminal())
            .then(|| state.items[index].clone());
        persist_transition(
            &self.shared.path,
            &mut state,
            previous_items,
            previous_requeue,
        )?;
        cancel_stopping_active(&state);
        if let Some(item) = finished {
            publish_finished(&mut state, item);
        }
        publish(&mut state);
        self.shared.wake.notify_all();
        Ok(())
    }
}
// Publish and irreversible worker cancellation happen only after the new
// queue state is durable. A failed write must not silently authorize work.
fn persist_transition(
    path: &Path,
    state: &mut State,
    previous_items: Vec<QueueItem>,
    previous_requeue: Option<bool>,
) -> Result<(), TransferError> {
    if let Err(error) = persist(path, state) {
        state.items = previous_items;
        if let (Some(active), Some(requeue)) = (&mut state.active, previous_requeue) {
            active.requeue = requeue;
        }
        return Err(error);
    }
    Ok(())
}
fn cancel_stopping_active(state: &State) {
    if let Some(active) = &state.active
        && state.items.iter().any(|item| {
            item.id == active.id
                && matches!(item.state, QueueState::Pausing | QueueState::Cancelling)
        })
    {
        active.control.cancel();
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
            pause_for_shutdown(item);
        }
    }
    if state.recovery_error.is_none() {
        let _ = persist(&shared.path, &mut state);
    }
    publish(&mut state);
    shared.wake.notify_all();
}

fn pause_for_shutdown(item: &mut QueueItem) {
    // A child that was waiting for its game may resume with that game. A child
    // deliberately paused by the player stays paused until explicitly resumed.
    if item.state == QueueState::Waiting && item.request.depends_on.is_some() {
        item.report.error_code = Some(DEPENDENCY_INTERRUPTED.into());
    }
    item.state = QueueState::Paused;
}

fn dependency_interrupted(item: &QueueItem) -> bool {
    item.state == QueueState::Paused
        && (item.report.error_code.as_deref() == Some(DEPENDENCY_INTERRUPTED)
            || matches!(
                item.error.as_deref(),
                Some(PREVIOUS_DOWNLOAD_INCOMPLETE | PREVIOUS_DOWNLOAD_MISSING | DEPENDENCY_CYCLE)
            ))
}

fn resume_chain(items: &[QueueItem], index: usize) -> Result<Vec<usize>, TransferError> {
    let mut chain = vec![index];
    if !matches!(
        items[index].state,
        QueueState::Paused | QueueState::Failed | QueueState::Cancelled
    ) {
        return Ok(chain);
    }
    let resume_parents = dependency_interrupted(&items[index]);
    let mut current = &items[index];
    while let Some(previous) = &current.request.depends_on {
        let parent = items
            .iter()
            .position(|other| &other.id == previous)
            .ok_or(TransferError::DependencyBlocked(PREVIOUS_DOWNLOAD_MISSING))?;
        if chain.contains(&parent) {
            return Err(TransferError::DependencyBlocked(DEPENDENCY_CYCLE));
        }
        current = &items[parent];
        match current.state {
            QueueState::Completed => break,
            QueueState::NeedsConfirmation => {
                return Err(TransferError::NeedsCleanupConfirmation(
                    current.cleanup_files.clone(),
                ));
            }
            _ => chain.push(parent),
        }
    }
    if !resume_parents {
        chain.truncate(1);
    }
    Ok(chain)
}

fn queue_again(item: &mut QueueItem) {
    item.state = QueueState::Waiting;
    item.request.approved_cleanup_files.clear();
    item.error = None;
    item.progress = None;
    item.report.error_code = None;
}

fn queue_interrupted_dependents(items: &mut [QueueItem], resumed: String) {
    let mut parents = vec![resumed];
    while let Some(parent) = parents.pop() {
        for child in &mut *items {
            if child.request.depends_on.as_deref() == Some(&parent) && dependency_interrupted(child)
            {
                queue_again(child);
                parents.push(child.id.clone());
            }
        }
    }
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
    item.request.approved_cleanup_files = Vec::new();
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
        item.cleanup_files = Vec::new();
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
    item.cleanup_files = Vec::new();
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
            // Consent belongs to this attempt only. A retry or automatic
            // requeue must not delete a file recreated under an approved name.
            item.request.approved_cleanup_files.clear();
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
            let plan = engine.prepare_plan(&request, &control, &callback)?;
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
            finish_attempt(item, result, stopping, requeue);
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

fn finish_attempt(
    item: &mut QueueItem,
    result: Result<TransferOutcome, TransferError>,
    stopping: bool,
    requeue: bool,
) {
    let error = match result {
        Ok(outcome) => {
            // A successful transfer has already written its installation markers.
            item.state = QueueState::Completed;
            item.outcome = Some(outcome);
            item.error = None;
            item.cleanup_files.clear();
            return;
        }
        Err(error) => error,
    };
    item.report.failure(&error);
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
        match error {
            TransferError::NeedsCleanupConfirmation(paths) => {
                item.state = QueueState::NeedsConfirmation;
                item.cleanup_files = paths;
            }
            TransferError::Cancelled => item.state = QueueState::Cancelled,
            error => {
                item.state = QueueState::Failed;
                item.error = Some(error.to_string());
            }
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
                changes.push((index, DEPENDENCY_CYCLE));
                break;
            }
            match state.items.iter().find(|i| i.id == id) {
                None => {
                    changes.push((index, PREVIOUS_DOWNLOAD_MISSING));
                    break;
                }
                Some(parent)
                    if matches!(parent.state, QueueState::Failed | QueueState::Cancelled) =>
                {
                    changes.push((index, PREVIOUS_DOWNLOAD_INCOMPLETE));
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
        state.items[index].report.error_code = Some(DEPENDENCY_INTERRUPTED.into());
    }
    changed
}

#[cfg(test)]
#[path = "queue_tests.rs"]
mod tests;
