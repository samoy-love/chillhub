use super::*;
use std::{
    collections::VecDeque,
    fs::{File, OpenOptions},
    io::Read,
    os::windows::{
        fs::OpenOptionsExt,
        io::{AsRawHandle, FromRawHandle, OwnedHandle},
    },
    ptr,
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicBool, AtomicUsize, Ordering},
        mpsc,
    },
    thread,
    time::SystemTime,
};
use windows_sys::Win32::{
    Foundation::*,
    Storage::FileSystem::{
        DELETE, FILE_DISPOSITION_INFO, FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_READ,
        FileDispositionInfo, SetFileInformationByHandle,
    },
    System::{LibraryLoader::GetModuleHandleW, Threading::*},
    UI::{Shell::*, WindowsAndMessaging::*},
};
fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain([0]).collect()
}
const REQUEST_LIMIT: u64 = 32768;
fn fresh_request(modified: SystemTime, now: SystemTime) -> bool {
    now.duration_since(modified)
        .unwrap_or_else(|e| e.duration())
        <= Duration::from_secs(120)
}
fn publish_request(data: &Path, args: &[String]) -> Result<PathBuf, String> {
    let bytes = serde_json::to_vec(args).map_err(|e| e.to_string())?;
    if bytes.len() as u64 > REQUEST_LIMIT {
        return Err("Запрос ярлыка слишком большой".into());
    }
    let directory = data.join("requests");
    let _guard = chillhub_native::manifest::DirectoryGuard::acquire(&directory, true)
        .map_err(|e| e.to_string())?;
    let path = directory.join(format!("{}.json", uuid::Uuid::new_v4()));
    chillhub_native::paths::atomic_write(&path, &bytes).map_err(|e| e.to_string())?;
    Ok(path)
}

fn await_activation_or_ownership(
    mutex: HANDLE,
    acknowledged: HANDLE,
    timeout_ms: u32,
) -> Result<bool, String> {
    let handles = [mutex, acknowledged];
    let result = unsafe { WaitForMultipleObjects(2, handles.as_ptr(), 0, timeout_ms) };
    if result == WAIT_OBJECT_0 || result == WAIT_ABANDONED_0 {
        Ok(true)
    } else if result == WAIT_OBJECT_0 + 1 || result == WAIT_TIMEOUT {
        Ok(false)
    } else {
        Err("Не удалось дождаться запущенного лаунчера".into())
    }
}
/// A scan pins the directory and its regular entries until the visible
/// launcher accepts the requests. A dropped scan leaves them untouched.
struct PreparedRequests {
    _guard: chillhub_native::manifest::DirectoryGuard,
    files: Vec<File>,
    commands: Vec<Vec<String>>,
    has_more: bool,
}

impl PreparedRequests {
    fn remove(self) -> std::io::Result<bool> {
        let disposition = FILE_DISPOSITION_INFO { DeleteFile: true };
        let mut error = None;
        for file in self.files {
            if unsafe {
                SetFileInformationByHandle(
                    file.as_raw_handle().cast(),
                    FileDispositionInfo,
                    (&raw const disposition).cast(),
                    size_of::<FILE_DISPOSITION_INFO>() as u32,
                )
            } == 0
            {
                error.get_or_insert_with(std::io::Error::last_os_error);
            }
        }
        error.map_or(Ok(self.has_more), Err)
    }
}

fn prepare_requests(
    data: &Path,
    now: SystemTime,
    promoted_request: Option<&Path>,
) -> Option<PreparedRequests> {
    let directory = data.join("requests");
    let guard = chillhub_native::manifest::DirectoryGuard::acquire(&directory, false).ok()?;
    let entries = std::fs::read_dir(&directory).ok()?;
    let mut requests = PreparedRequests {
        _guard: guard,
        files: Vec::new(),
        commands: Vec::new(),
        has_more: false,
    };
    let mut eligible = Vec::new();
    for entry in entries.flatten().take(128) {
        let path = entry.path();
        if path.extension().is_none_or(|x| x != "json") {
            continue;
        }
        let Ok(mut file) = OpenOptions::new()
            .access_mode(GENERIC_READ | DELETE)
            .share_mode(FILE_SHARE_READ)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
            .open(&path)
        else {
            continue;
        };
        let Ok(meta) = file.metadata() else {
            continue;
        };
        if !meta.is_file() || chillhub_native::manifest::is_reparse(&meta) {
            continue;
        }
        if promoted_request == Some(path.as_path()) {
            // The promoted launcher already received these arguments directly.
            requests.files.push(file);
            continue;
        }
        let decoded = (|| {
            let modified = meta.modified().ok()?;
            if meta.len() > REQUEST_LIMIT || !fresh_request(modified, now) {
                return None;
            }
            let mut bytes = Vec::new();
            file.by_ref()
                .take(REQUEST_LIMIT + 1)
                .read_to_end(&mut bytes)
                .ok()?;
            if bytes.len() as u64 > REQUEST_LIMIT {
                return None;
            }
            let args: Vec<String> = serde_json::from_slice(&bytes).ok()?;
            ShortcutRequest::parse_args(&args).ok().flatten()?;
            Some((modified, args))
        })();
        if let Some((modified, args)) = decoded {
            eligible.push((modified, path, file, args));
        } else {
            requests.files.push(file);
        }
    }
    // UUID names and filesystem enumeration do not represent arrival order.
    eligible.sort_unstable_by(|a, b| a.0.cmp(&b.0).then_with(|| a.1.cmp(&b.1)));
    requests.has_more = eligible.len() > 32;
    for (_, _, file, args) in eligible.into_iter().take(32) {
        requests.commands.push(args);
        requests.files.push(file);
    }
    Some(requests)
}
pub(super) struct Instance {
    pub primary: bool,
    mutex: HANDLE,
    events: Arc<ActivationEvents>,
    scan_name: Vec<u16>,
    promoted_request: Option<PathBuf>,
    data: PathBuf,
    listener: Option<ActivationListener>,
}

struct ActivationEvents {
    requested: OwnedHandle,
    acknowledged: OwnedHandle,
}

#[derive(PartialEq, Eq)]
enum ActivationWake {
    Startup,
    Requested,
}

struct ActivationControl {
    stop: OwnedHandle,
    active: AtomicBool,
    publication: Mutex<()>,
}

impl ActivationControl {
    fn is_active(&self) -> bool {
        self.active.load(Ordering::Acquire)
    }

    fn wait_for_scan<'a>(&self, scan: &'a OwnedHandle) -> Option<ShortcutScan<'a>> {
        let handles = [self.stop.as_raw_handle(), scan.as_raw_handle()];
        let result = unsafe { WaitForMultipleObjects(2, handles.as_ptr(), 0, INFINITE) };
        if result == WAIT_OBJECT_0 + 1 || result == WAIT_ABANDONED_0 + 1 {
            Some(ShortcutScan(scan))
        } else {
            None
        }
    }

    fn publish(&self, events: &ActivationEvents, acknowledge: bool, has_more: bool) -> bool {
        // No file access while holding the gate. Shutdown only waits for
        // any signal publication already in progress.
        let _gate = self.publication.lock().unwrap();
        if !self.is_active() {
            return false;
        }
        unsafe {
            if acknowledge {
                SetEvent(events.acknowledged.as_raw_handle());
            }
            if has_more {
                SetEvent(events.requested.as_raw_handle());
            }
        }
        true
    }
}

struct ShortcutScan<'a>(&'a OwnedHandle);

impl Drop for ShortcutScan<'_> {
    fn drop(&mut self) {
        unsafe { ReleaseMutex(self.0.as_raw_handle()) };
    }
}

struct ActivationListener {
    control: Arc<ActivationControl>,
}

impl ActivationListener {
    fn start(
        events: Arc<ActivationEvents>,
        mut activate: impl FnMut(&Arc<ActivationControl>, ActivationWake) -> bool + Send + 'static,
    ) -> Result<Self, String> {
        let stop = unsafe { CreateEventW(ptr::null(), 1, 0, ptr::null()) };
        if stop.is_null() {
            return Err("Не удалось открыть сигнал завершения лаунчера".into());
        }
        let control = Arc::new(ActivationControl {
            stop: unsafe { OwnedHandle::from_raw_handle(stop) },
            active: AtomicBool::new(true),
            publication: Mutex::new(()),
        });
        let worker_control = control.clone();
        let worker = thread::Builder::new()
            .name("chillhub-instance".into())
            .spawn(move || {
                if !activate(&worker_control, ActivationWake::Startup) {
                    return;
                }
                let handles = [
                    worker_control.stop.as_raw_handle(),
                    events.requested.as_raw_handle(),
                ];
                loop {
                    // Stop wins when both events are signaled.
                    let result =
                        unsafe { WaitForMultipleObjects(2, handles.as_ptr(), 0, INFINITE) };
                    if result != WAIT_OBJECT_0 + 1
                        || !activate(&worker_control, ActivationWake::Requested)
                    {
                        break;
                    }
                }
            })
            .map_err(|error| format!("Не удалось начать обработку ярлыков: {error}"))?;
        // The worker may be in synchronous filesystem I/O on a redirected
        // profile. Its owned handles outlive the UI without blocking shutdown.
        drop(worker);
        Ok(Self { control })
    }
}

impl Drop for ActivationListener {
    fn drop(&mut self) {
        // An event already queued on the UI thread must not consume requests
        // or use the acknowledgment handle once the instance has closed.
        let _gate = self.control.publication.lock().unwrap();
        self.control.active.store(false, Ordering::Release);
        unsafe { SetEvent(self.control.stop.as_raw_handle()) };
    }
}
fn instance_suffix(data: &Path, isolated: bool) -> Result<String, String> {
    if !isolated {
        return Ok(String::new());
    }
    let identity = chillhub_native::paths::path_identity(data)
        .ok_or("Не удалось определить папку настроек для блокировки лаунчера")?;
    Ok(format!(
        ".{}",
        &blake3::hash(identity.as_bytes()).to_hex()[..16]
    ))
}
impl Instance {
    pub fn acquire(data: &Path, args: &[String]) -> Result<Self, String> {
        Self::acquire_for_profile(data, args, std::env::var_os("CHILLHUB_DATA_DIR").is_some())
    }
    fn acquire_for_profile(data: &Path, args: &[String], isolated: bool) -> Result<Self, String> {
        let shortcut = ShortcutRequest::parse_args(args)?.is_some();
        let suffix = instance_suffix(data, isolated)?;
        let name = wide(&format!("Local\\ChillHub.SingleInstance{suffix}"));
        let event_name = wide(&format!("Local\\ChillHub.ShowRequested{suffix}"));
        let ack_name = wide(&format!("Local\\ChillHub.ShowAcknowledged{suffix}"));
        unsafe {
            let mutex = CreateMutexW(ptr::null(), 0, name.as_ptr());
            if mutex.is_null() {
                return Err("Не удалось открыть блокировку лаунчера".into());
            }
            let result = WaitForSingleObject(mutex, 0);
            let primary = result == WAIT_OBJECT_0 || result == WAIT_ABANDONED;
            if !primary && result != WAIT_TIMEOUT {
                CloseHandle(mutex);
                return Err("Не удалось проверить запущенный лаунчер".into());
            }
            let event = CreateEventW(ptr::null(), 0, 0, event_name.as_ptr());
            if event.is_null() {
                if primary {
                    ReleaseMutex(mutex);
                }
                CloseHandle(mutex);
                return Err("Не удалось открыть сигнал окна".into());
            }
            let acknowledged = CreateEventW(ptr::null(), 0, 0, ack_name.as_ptr());
            if acknowledged.is_null() {
                CloseHandle(event);
                if primary {
                    ReleaseMutex(mutex);
                }
                CloseHandle(mutex);
                return Err("Не удалось открыть подтверждение окна".into());
            }
            let mut instance = Self {
                primary,
                mutex,
                events: Arc::new(ActivationEvents {
                    requested: OwnedHandle::from_raw_handle(event),
                    acknowledged: OwnedHandle::from_raw_handle(acknowledged),
                }),
                scan_name: wide(&format!("Local\\ChillHub.ScanRequests{suffix}")),
                promoted_request: None,
                data: data.into(),
                listener: None,
            };
            if !primary {
                let request = if shortcut {
                    Some(publish_request(data, args)?)
                } else {
                    None
                };
                ResetEvent(acknowledged);
                SetEvent(event);
                // A previous instance may already be shutting down and unable
                // to show its window. Take over after its mutex is released;
                // normal activation returns immediately when the UI confirms.
                instance.primary = await_activation_or_ownership(mutex, acknowledged, 7000)?;
                if instance.primary {
                    instance.promoted_request = request;
                }
            }
            Ok(instance)
        }
    }
    pub fn listen(&mut self, ui: slint::Weak<AppWindow>) -> Result<(), String> {
        self.listen_with(move |control, requests, accepted| {
            let callback_control = control.clone();
            let weak = ui.clone();
            slint::invoke_from_event_loop(move || {
                if callback_control.is_active()
                    && let Some(ui) = weak.upgrade()
                {
                    deliver_activation(
                        || show(&ui).is_ok(),
                        || {
                            for args in requests {
                                controller::external_request(&ui, args);
                            }
                        },
                        accepted,
                    );
                }
            })
            .is_ok()
        })
    }

    fn listen_with(
        &mut self,
        deliver: impl Fn(&Arc<ActivationControl>, Vec<Vec<String>>, mpsc::Sender<bool>) -> bool
        + Send
        + 'static,
    ) -> Result<(), String> {
        if !self.primary || self.listener.is_some() {
            return Ok(());
        }
        let scan = unsafe { CreateMutexW(ptr::null(), 0, self.scan_name.as_ptr()) };
        if scan.is_null() {
            return Err("Не удалось открыть очередь ярлыков лаунчера".into());
        }
        let scan = unsafe { OwnedHandle::from_raw_handle(scan) };
        let promoted = self.promoted_request.take();
        let events = self.events.clone();
        let data = self.data.clone();
        self.listener = Some(ActivationListener::start(
            self.events.clone(),
            move |control, wake| {
                // A previous listener may still be finishing accepted files.
                // Serialize only the workers so that takeover never blocks the UI.
                let Some(_scan) = control.wait_for_scan(&scan) else {
                    return false;
                };
                if !control.is_active() {
                    return false;
                }
                let mut batch = prepare_requests(&data, SystemTime::now(), promoted.as_deref());
                if !control.is_active() {
                    return false;
                }
                let requests = batch
                    .as_mut()
                    .map(|batch| std::mem::take(&mut batch.commands))
                    .unwrap_or_default();
                let acknowledge = wake == ActivationWake::Requested;
                if !acknowledge && requests.is_empty() {
                    let has_more = batch.is_some_and(|batch| finish_shortcut_batch(batch, &data));
                    return control.publish(&events, false, has_more);
                }
                let (accepted, receipt) = mpsc::channel();
                if !deliver(control, requests, accepted) {
                    return false;
                }
                // Waiting on the receipt keeps all disk work on this thread.
                // Shutdown can stop the wait even when Slint has already exited.
                loop {
                    match receipt.recv_timeout(Duration::from_millis(25)) {
                        Ok(true) => break,
                        Ok(false) => return true,
                        Err(mpsc::RecvTimeoutError::Timeout) if control.is_active() => {}
                        Err(_) => return false,
                    }
                }
                let has_more = batch.is_some_and(|batch| finish_shortcut_batch(batch, &data));
                control.publish(&events, acknowledge, has_more)
            },
        )?);
        Ok(())
    }
}
impl Drop for Instance {
    fn drop(&mut self) {
        drop(self.listener.take());
        unsafe {
            if self.primary {
                ReleaseMutex(self.mutex);
            }
            CloseHandle(self.mutex);
        }
    }
}
fn finish_shortcut_batch(batch: PreparedRequests, data: &Path) -> bool {
    batch.remove().unwrap_or_else(|error| {
        chillhub_native::diagnostics::log(data, "shortcut.cleanup_error", &error.to_string());
        false
    })
}

fn deliver_activation(
    show: impl FnOnce() -> bool,
    dispatch: impl FnOnce(),
    receipt: mpsc::Sender<bool>,
) {
    let shown = show();
    if shown {
        dispatch();
    }
    let _ = receipt.send(shown);
}

fn show(ui: &AppWindow) -> Result<(), slint::PlatformError> {
    use slint::winit_030::WinitWindowAccessor;
    ui.show()?;
    ui.window().set_minimized(false);
    ui.window().with_winit_window(|w| w.focus_window());
    Ok(())
}
pub fn show_error(message: &str) {
    unsafe {
        MessageBoxW(
            ptr::null_mut(),
            wide(message).as_ptr(),
            wide("Chill Hub").as_ptr(),
            MB_OK | MB_ICONERROR,
        );
    }
}

#[derive(Clone, Default)]
struct MenuSelection {
    title: String,
    game_id: String,
    action: String,
    enabled: bool,
}
impl MenuSelection {
    fn can_play(&self) -> bool {
        self.enabled && !self.game_id.is_empty()
    }

    fn command(self, command: u32) -> Option<TrayEvent> {
        match command {
            1 => Some(TrayEvent::Show),
            2 if self.can_play() => Some(TrayEvent::Play(self.game_id, self.action)),
            3 => Some(TrayEvent::Exit),
            _ => None,
        }
    }
}
struct Balloon {
    title: String,
    text: String,
    error: bool,
}
struct PendingShell {
    menu: MenuSelection,
    tip: String,
    notifications: VecDeque<Balloon>,
}
struct ShellBridge {
    hwnd: AtomicUsize,
    pending: Mutex<PendingShell>,
}
impl ShellBridge {
    fn new() -> Self {
        Self {
            hwnd: AtomicUsize::new(0),
            pending: Mutex::new(PendingShell {
                menu: MenuSelection::default(),
                tip: "Chill Hub".into(),
                notifications: VecDeque::new(),
            }),
        }
    }

    fn set_status(&self, status: &str, post_update: impl FnOnce(usize) -> bool) {
        let tip = if status.trim().is_empty() {
            "Chill Hub".into()
        } else {
            format!("Chill Hub — {}", status.trim())
        };
        let Ok(mut pending) = self.pending.lock() else {
            return;
        };
        if pending.tip == tip {
            return;
        }
        pending.tip = tip;
        drop(pending);
        let hwnd = self.hwnd.load(Ordering::Acquire);
        if hwnd != 0 {
            post_update(hwnd);
        }
    }

    fn notify(
        &self,
        title: &str,
        text: &str,
        error: bool,
        post_update: impl FnOnce(usize) -> bool,
    ) -> bool {
        let hwnd = self.hwnd.load(Ordering::Acquire);
        if hwnd == 0 {
            return false;
        }
        let Ok(mut pending) = self.pending.lock() else {
            return false;
        };
        // Hold the queue while posting so the receiving window reads this
        // notification only after Windows has accepted the wakeup.
        if !post_update(hwnd) {
            return false;
        }
        if pending.notifications.len() == 8 {
            pending.notifications.pop_front();
        }
        pending.notifications.push_back(Balloon {
            title: title.chars().take(120).collect(),
            text: text.chars().take(512).collect(),
            error,
        });
        true
    }

    fn take_update(&self) -> Option<(String, Vec<Balloon>)> {
        let mut pending = self.pending.lock().ok()?;
        Some((
            pending.tip.clone(),
            pending.notifications.drain(..).collect(),
        ))
    }
}
static SHELL: OnceLock<Arc<ShellBridge>> = OnceLock::new();
enum TrayEvent {
    Show,
    Menu(i32, i32),
    Play(String, String),
    Exit,
}
static EVENTS: OnceLock<mpsc::Sender<TrayEvent>> = OnceLock::new();
const TRAY_MESSAGE: u32 = WM_APP + 17;
const TRAY_MENU: u32 = WM_APP + 18;
const TRAY_UPDATE: u32 = WM_APP + 19;
// Windows SDK function-like macro, not exported by windows-sys.
const NIN_KEYSELECT: u32 = NIN_SELECT | NINF_KEY;
fn fill_wide<const N: usize>(target: &mut [u16; N], text: &str) {
    target.fill(0);
    let mut offset = 0;
    for ch in text.chars() {
        let mut encoded = [0u16; 2];
        let units = ch.encode_utf16(&mut encoded);
        if offset + units.len() >= N {
            break;
        }
        target[offset..offset + units.len()].copy_from_slice(units);
        offset += units.len();
    }
}
fn play_menu_label(title: &str, action: &str) -> String {
    let title: String = title.chars().filter(|c| !c.is_control()).collect();
    let title = title.trim();
    if title.is_empty() {
        return "Игра не выбрана".into();
    }
    let short: String = title.chars().take(90).collect();
    format!(
        "{action}: {}{}",
        short.replace('&', "&&"),
        if title.chars().count() > 90 {
            "…"
        } else {
            ""
        }
    )
}
/// Event-driven rounded progress; equal text never calls the Windows shell.
pub(super) fn set_status(status: &str) {
    if let Some(shell) = SHELL.get() {
        shell.set_status(status, post_tray_update);
    }
}
/// Caller decides whether the window is hidden. No window is activated here.
pub(super) fn notify(title: &str, text: &str, error: bool) -> bool {
    SHELL
        .get()
        .is_some_and(|shell| shell.notify(title, text, error, post_tray_update))
}
fn post_tray_update(hwnd: usize) -> bool {
    unsafe { PostMessageW(hwnd as HWND, TRAY_UPDATE, 0, 0) != 0 }
}
struct NativeTray {
    data: NOTIFYICONDATAW,
    shell: Arc<ShellBridge>,
    taskbar_created: u32,
}
unsafe fn add_icon(data: &NOTIFYICONDATAW) -> bool {
    unsafe {
        if Shell_NotifyIconW(NIM_ADD, data) == 0 {
            return false;
        }
        let mut version = *data;
        version.Anonymous.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &version);
        true
    }
}
fn emit(event: TrayEvent) {
    if let Some(tx) = EVENTS.get() {
        let _ = tx.send(event);
    }
}
unsafe extern "system" fn window_proc(
    hwnd: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    unsafe {
        if let Some(context) = (GetWindowLongPtrW(hwnd, GWLP_USERDATA) as *mut NativeTray).as_mut()
        {
            // Explorer broadcasts to a hidden top-level HWND, not a message-only HWND.
            if context.taskbar_created != 0 && message == context.taskbar_created {
                let data = context.data;
                add_icon(&data);
                return 0;
            }
            if message == TRAY_UPDATE {
                if let Some((tip, notifications)) = context.shell.take_update() {
                    fill_wide(&mut context.data.szTip, &tip);
                    let mut data = context.data;
                    data.uFlags = NIF_TIP | NIF_SHOWTIP;
                    Shell_NotifyIconW(NIM_MODIFY, &data);
                    for balloon in notifications {
                        data.uFlags = NIF_INFO;
                        fill_wide(&mut data.szInfoTitle, &balloon.title);
                        fill_wide(&mut data.szInfo, &balloon.text);
                        data.dwInfoFlags = if balloon.error { NIIF_ERROR } else { NIIF_INFO };
                        data.Anonymous.uTimeout = 5000;
                        Shell_NotifyIconW(NIM_MODIFY, &data);
                    }
                }
                return 0;
            }
            if message == TRAY_MENU {
                let selected = context
                    .shell
                    .pending
                    .lock()
                    .ok()
                    .map(|p| p.menu.clone())
                    .unwrap_or_default();
                let menu = CreatePopupMenu();
                if menu.is_null() {
                    return 0;
                }
                AppendMenuW(menu, MF_STRING, 1, wide("Открыть Chill Hub").as_ptr());
                SetMenuDefaultItem(menu, 1, 0);
                AppendMenuW(
                    menu,
                    MF_STRING | if selected.can_play() { 0 } else { MF_GRAYED },
                    2,
                    wide(&play_menu_label(&selected.title, &selected.action)).as_ptr(),
                );
                AppendMenuW(menu, MF_SEPARATOR, 0, ptr::null());
                AppendMenuW(menu, MF_STRING, 3, wide("Выйти").as_ptr());
                SetForegroundWindow(hwnd);
                let command = TrackPopupMenu(
                    menu,
                    TPM_RETURNCMD | TPM_RIGHTBUTTON,
                    wparam as i32,
                    lparam as i32,
                    0,
                    hwnd,
                    ptr::null(),
                );
                DestroyMenu(menu);
                if let Some(event) = selected.command(command as u32) {
                    emit(event);
                }
                PostMessageW(hwnd, WM_NULL, 0, 0);
                return 0;
            }
        }
        match message {
            TRAY_MESSAGE => {
                let event = lparam as u32 & 0xffff;
                if matches!(
                    event,
                    WM_LBUTTONUP
                        | WM_LBUTTONDBLCLK
                        | NIN_SELECT
                        | NIN_KEYSELECT
                        | NIN_BALLOONUSERCLICK
                ) {
                    emit(TrayEvent::Show);
                }
                if matches!(event, WM_RBUTTONUP | WM_CONTEXTMENU) {
                    let mut point: POINT = std::mem::zeroed();
                    GetCursorPos(&mut point);
                    emit(TrayEvent::Menu(point.x, point.y));
                }
                0
            }
            WM_CLOSE => {
                DestroyWindow(hwnd);
                0
            }
            WM_DESTROY => {
                PostQuitMessage(0);
                0
            }
            WM_NCDESTROY => {
                SetWindowLongPtrW(hwnd, GWLP_USERDATA, 0);
                DefWindowProcW(hwnd, message, wparam, lparam)
            }
            _ => DefWindowProcW(hwnd, message, wparam, lparam),
        }
    }
}
fn can_launch_from_tray(
    selected: bool,
    can_play: bool,
    busy: bool,
    modal_open: bool,
    action: Option<&chillhub_native::launch::LaunchAction>,
) -> bool {
    selected
        && can_play
        && !busy
        && !modal_open
        && action.is_some_and(|a| *a != chillhub_native::launch::LaunchAction::Unavailable)
}
fn remember_tray_hint(store: &chillhub_native::config::ConfigStore) -> Result<(), String> {
    store.update(|c| c.tray_hint_shown = true).map(|_| ())
}
pub(super) struct Tray {
    hwnd: usize,
}
impl Drop for Tray {
    fn drop(&mut self) {
        unsafe {
            PostMessageW(self.hwnd as HWND, WM_CLOSE, 0, 0);
        }
    }
}
pub(super) fn create_tray(ui: &AppWindow, state: &State) -> Result<Tray, String> {
    let (tx, rx) = mpsc::channel();
    EVENTS.set(tx).map_err(|_| "Значок уже создан")?;
    let shell = Arc::new(ShellBridge::new());
    SHELL.set(shell.clone()).map_err(|_| "Значок уже создан")?;
    let (ready_tx, ready_rx) = mpsc::channel();
    let native_shell = shell.clone();
    thread::spawn(move || unsafe {
        let class_name = wide("ChillHub.Tray.Native");
        let instance = GetModuleHandleW(ptr::null());
        let class = WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance,
            lpszClassName: class_name.as_ptr(),
            ..std::mem::zeroed()
        };
        RegisterClassW(&class);
        let hwnd = CreateWindowExW(
            0,
            class_name.as_ptr(),
            wide("Chill Hub").as_ptr(),
            0,
            0,
            0,
            0,
            0,
            ptr::null_mut(),
            ptr::null_mut(),
            instance,
            ptr::null(),
        );
        if hwnd.is_null() {
            let _ = ready_tx.send(Err(
                "Не удалось создать значок в области уведомлений".to_string()
            ));
            UnregisterClassW(class_name.as_ptr(), instance);
            return;
        }
        let exe = std::env::current_exe().unwrap_or_default();
        let extracted = ExtractIconW(instance, wide(&exe.to_string_lossy()).as_ptr(), 0);
        let owned_icon = !extracted.is_null() && extracted as usize != 1;
        let icon = if owned_icon {
            extracted
        } else {
            LoadIconW(ptr::null_mut(), IDI_APPLICATION)
        };
        let mut data: NOTIFYICONDATAW = std::mem::zeroed();
        data.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
        data.hWnd = hwnd;
        data.uID = 1;
        data.uFlags = NIF_ICON | NIF_MESSAGE | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = TRAY_MESSAGE;
        data.hIcon = icon;
        fill_wide(&mut data.szTip, "Chill Hub");
        let mut context = Box::new(NativeTray {
            data,
            shell: native_shell.clone(),
            taskbar_created: RegisterWindowMessageW(wide("TaskbarCreated").as_ptr()),
        });
        SetWindowLongPtrW(
            hwnd,
            GWLP_USERDATA,
            (&mut *context as *mut NativeTray) as isize,
        );
        if !add_icon(&context.data) {
            DestroyWindow(hwnd);
            if owned_icon {
                DestroyIcon(icon);
            }
            UnregisterClassW(class_name.as_ptr(), instance);
            let _ = ready_tx.send(Err("Windows не разрешила создать значок лаунчера".into()));
            return;
        }
        native_shell.hwnd.store(hwnd as usize, Ordering::Release);
        if ready_tx.send(Ok(hwnd as usize)).is_err() {
            DestroyWindow(hwnd);
        }
        let mut msg: MSG = std::mem::zeroed();
        while GetMessageW(&mut msg, ptr::null_mut(), 0, 0) > 0 {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        native_shell.hwnd.store(0, Ordering::Release);
        Shell_NotifyIconW(NIM_DELETE, &context.data);
        if owned_icon {
            DestroyIcon(icon);
        }
        UnregisterClassW(class_name.as_ptr(), instance);
    });
    let hwnd = ready_rx
        .recv_timeout(Duration::from_secs(4))
        .map_err(|_| "Windows не ответила при создании значка")??;
    let weak = ui.as_weak();
    thread::spawn(move || {
        while let Ok(event) = rx.recv() {
            let weak = weak.clone();
            let shell = shell.clone();
            if slint::invoke_from_event_loop(move || {
                let Some(ui) = weak.upgrade() else {
                    return;
                };
                ACTIVE_STATE.with(|slot| {
                    let state = slot.borrow().as_ref().cloned();
                    match event {
                        TrayEvent::Show => {
                            let _ = show(&ui);
                        }
                        TrayEvent::Menu(x, y) => {
                            let selection = state
                                .as_ref()
                                .and_then(|s| {
                                    let s = s.borrow();
                                    s.records.get(s.selected).map(|g| MenuSelection {
                                        title: ui.get_game_title().to_string(),
                                        action: ui.get_play_label().to_string(),
                                        enabled: can_launch_from_tray(
                                            true,
                                            ui.get_can_play(),
                                            ui.get_busy()
                                                || ui.get_operation_busy()
                                                || ui.get_loading(),
                                            !ui.get_panel_kind().is_empty(),
                                            s.option.as_ref().map(|o| &o.action),
                                        ),
                                        game_id: if ui.get_game_title().is_empty() {
                                            String::new()
                                        } else {
                                            g.game_id.clone()
                                        },
                                    })
                                })
                                .unwrap_or_default();
                            if let Ok(mut p) = shell.pending.lock() {
                                p.menu = selection;
                            }
                            unsafe {
                                PostMessageW(hwnd as HWND, TRAY_MENU, x as WPARAM, y as LPARAM);
                            }
                        }
                        TrayEvent::Play(id, shown_action) => {
                            let Some(state) = state else {
                                let _ = show(&ui);
                                return;
                            };
                            let ready = {
                                let s = state.borrow();
                                can_launch_from_tray(
                                    s.records.get(s.selected).is_some_and(|g| g.game_id == id),
                                    ui.get_can_play() && ui.get_play_label() == shown_action,
                                    ui.get_busy() || ui.get_operation_busy() || ui.get_loading(),
                                    !ui.get_panel_kind().is_empty(),
                                    s.option.as_ref().map(|o| &o.action),
                                )
                            };
                            let _ = show(&ui);
                            if ready {
                                controller::play(&ui, &state);
                            }
                        }
                        TrayEvent::Exit => {
                            if ui.get_operation_busy() {
                                let _ = show(&ui);
                                return;
                            }
                            if let Some(state) = state {
                                let _ = show(&ui);
                                controller::request_exit(&ui, &state);
                            } else {
                                let _ = slint::quit_event_loop();
                            }
                        }
                    }
                });
            })
            .is_err()
            {
                break;
            }
        }
    });
    let weak = ui.as_weak();
    let weak_state = Rc::downgrade(state);
    ui.window().on_close_requested(move||{
        if let(Some(ui),Some(state))=(weak.upgrade(),weak_state.upgrade()){
            if ui.get_operation_busy(){let _=show(&ui);return slint::CloseRequestResponse::KeepWindowShown;}controller::save_window(&ui,&state);
            if state.borrow().prefs.minimize_to_tray{
                // Only media is closed here. Settings keep their unsaved draft
                // while hidden, and the same close path releases both players.
                if ui.get_panel_kind() == "gallery" { close_panel(&ui,&state); }
                let _=ui.hide();
                if !state.borrow().prefs.tray_hint_shown&&notify("Лаунчер продолжает работать в трее","Загрузки идут дальше. Открыть — клик по значку, выйти — «Выйти» в его меню.",false){state.borrow_mut().prefs.tray_hint_shown=true;let store=state.borrow().services.config.clone();thread::spawn(move||{let _=remember_tray_hint(&store);});}
                return slint::CloseRequestResponse::KeepWindowShown;
            }
            controller::request_exit(&ui,&state);
            return slint::CloseRequestResponse::KeepWindowShown;
        }
        // The event loop survives hidden windows for the raw Win32 tray.
        let _=slint::quit_event_loop();slint::CloseRequestResponse::HideWindow
    });
    Ok(Tray { hwnd })
}

#[cfg(test)]
#[path = "desktop_tests.rs"]
mod tests;
