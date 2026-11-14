use super::*;
use std::{
    collections::VecDeque,
    io::Read,
    ptr,
    sync::{
        Arc, Mutex, OnceLock,
        atomic::{AtomicUsize, Ordering},
        mpsc,
    },
    thread,
    time::SystemTime,
};
use windows_sys::Win32::{
    Foundation::*,
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
    let directory = data.join("requests");
    let _guard = chillhub_native::manifest::DirectoryGuard::acquire(&directory, true)
        .map_err(|e| e.to_string())?;
    let bytes = serde_json::to_vec(args).map_err(|e| e.to_string())?;
    if bytes.len() as u64 > REQUEST_LIMIT {
        return Err("Запрос ярлыка слишком большой".into());
    }
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
/// Read only pinned regular files; never follow request-directory/file links.
fn consume_requests(data: &Path, now: SystemTime) -> Vec<Vec<String>> {
    let directory = data.join("requests");
    let Ok(_guard) = chillhub_native::manifest::DirectoryGuard::acquire(&directory, false) else {
        return Vec::new();
    };
    let Ok(entries) = std::fs::read_dir(&directory) else {
        return Vec::new();
    };
    let mut requests = Vec::new();
    for entry in entries.flatten().take(128) {
        let path = entry.path();
        if path.extension().is_none_or(|x| x != "json") {
            continue;
        }
        let Ok(mut file) = chillhub_native::manifest::open_regular(&path, false, false, false)
        else {
            continue;
        };
        let decoded = (|| {
            let meta = file.metadata().ok()?;
            if meta.len() > REQUEST_LIMIT || !fresh_request(meta.modified().ok()?, now) {
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
            Some(args)
        })();
        drop(file);
        let _ = std::fs::remove_file(path);
        if let Some(args) = decoded
            && requests.len() < 32
        {
            requests.push(args);
        }
    }
    requests
}
pub(super) struct Instance {
    pub primary: bool,
    mutex: HANDLE,
    event: usize,
    acknowledged: usize,
    data: PathBuf,
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
                event: event as usize,
                acknowledged: acknowledged as usize,
                data: data.into(),
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
                if instance.primary
                    && let Some(path) = request
                {
                    let _ = std::fs::remove_file(path);
                }
            }
            Ok(instance)
        }
    }
    pub fn listen(&self, ui: slint::Weak<AppWindow>) {
        if !self.primary {
            return;
        }
        let handle = self.event;
        let acknowledged = self.acknowledged;
        let data = self.data.clone();
        thread::spawn(move || {
            loop {
                if unsafe { WaitForSingleObject(handle as HANDLE, INFINITE) } != WAIT_OBJECT_0 {
                    return;
                }
                let requests = consume_requests(&data, SystemTime::now());
                let weak = ui.clone();
                if slint::invoke_from_event_loop(move || {
                    if let Some(ui) = weak.upgrade() {
                        show(&ui);
                        for args in requests {
                            controller::external_request(&ui, args);
                        }
                        unsafe {
                            SetEvent(acknowledged as HANDLE);
                        }
                    }
                })
                .is_err()
                {
                    return;
                }
            }
        });
    }
}
impl Drop for Instance {
    fn drop(&mut self) {
        unsafe {
            if self.primary {
                ReleaseMutex(self.mutex);
            } else {
                CloseHandle(self.event as HANDLE);
                CloseHandle(self.acknowledged as HANDLE);
            }
            CloseHandle(self.mutex);
        }
    }
}
fn show(ui: &AppWindow) {
    use slint::winit_030::WinitWindowAccessor;
    let _ = ui.show();
    ui.window().set_minimized(false);
    ui.window().with_winit_window(|w| w.focus_window());
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
    let title = title.trim();
    if title.is_empty() {
        return "Игра не выбрана".into();
    }
    let short: String = title.chars().filter(|c| !c.is_control()).take(90).collect();
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
    let Some(shell) = SHELL.get() else {
        return;
    };
    let tip = if status.trim().is_empty() {
        "Chill Hub".into()
    } else {
        format!("Chill Hub — {}", status.trim())
    };
    if let Ok(mut pending) = shell.pending.lock() {
        if pending.tip == tip {
            return;
        }
        pending.tip = tip;
    }
    let hwnd = shell.hwnd.load(Ordering::Acquire);
    if hwnd != 0 {
        unsafe {
            PostMessageW(hwnd as HWND, TRAY_UPDATE, 0, 0);
        }
    }
}
/// Caller decides whether the window is hidden. No window is activated here.
pub(super) fn notify(title: &str, text: &str, error: bool) -> bool {
    let Some(shell) = SHELL.get() else {
        return false;
    };
    let hwnd = shell.hwnd.load(Ordering::Acquire);
    if hwnd == 0 {
        return false;
    }
    let Ok(mut pending) = shell.pending.lock() else {
        return false;
    };
    if pending.notifications.len() == 8 {
        pending.notifications.pop_front();
    }
    pending.notifications.push_back(Balloon {
        title: title.chars().take(120).collect(),
        text: text.chars().take(512).collect(),
        error,
    });
    drop(pending);
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
                let update =
                    context.shell.pending.lock().ok().map(|mut p| {
                        (p.tip.clone(), p.notifications.drain(..).collect::<Vec<_>>())
                    });
                if let Some((tip, notifications)) = update {
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
                    MF_STRING
                        | if selected.game_id.is_empty() || !selected.enabled {
                            MF_GRAYED
                        } else {
                            0
                        },
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
                match command {
                    1 => emit(TrayEvent::Show),
                    2 if !selected.game_id.is_empty() => {
                        emit(TrayEvent::Play(selected.game_id, selected.action))
                    }
                    3 => emit(TrayEvent::Exit),
                    _ => {}
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
    let shell = Arc::new(ShellBridge {
        hwnd: AtomicUsize::new(0),
        pending: Mutex::new(PendingShell {
            menu: MenuSelection::default(),
            tip: "Chill Hub".into(),
            notifications: VecDeque::new(),
        }),
    });
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
                        TrayEvent::Show => show(&ui),
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
                                show(&ui);
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
                            show(&ui);
                            if ready {
                                controller::play(&ui, &state);
                            }
                        }
                        TrayEvent::Exit => {
                            if ui.get_operation_busy() {
                                show(&ui);
                                return;
                            }
                            if let Some(state) = state {
                                show(&ui);
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
            if ui.get_operation_busy(){show(&ui);return slint::CloseRequestResponse::KeepWindowShown;}controller::save_window(&ui,&state);
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
mod tests {
    use super::*;
    use chillhub_native::{config::ConfigStore, launch::LaunchAction, paths::AppPaths};
    #[test]
    fn tray_launch_never_changes_hidden_game_files() {
        assert!(can_launch_from_tray(
            true,
            true,
            false,
            false,
            Some(&LaunchAction::Play)
        ));
        for action in [
            LaunchAction::InstallGame,
            LaunchAction::UpdateGame,
            LaunchAction::InstallMods,
            LaunchAction::UpdateMods,
            LaunchAction::RepairMods,
        ] {
            assert!(can_launch_from_tray(
                true,
                true,
                false,
                false,
                Some(&action)
            ));
        }
        assert!(!can_launch_from_tray(
            true,
            true,
            false,
            false,
            Some(&LaunchAction::Unavailable)
        ));
        assert!(!can_launch_from_tray(
            false,
            true,
            false,
            false,
            Some(&LaunchAction::Play)
        ));
        assert!(!can_launch_from_tray(
            true,
            true,
            true,
            false,
            Some(&LaunchAction::Play)
        ));
        assert!(!can_launch_from_tray(
            true,
            true,
            false,
            true,
            Some(&LaunchAction::Play)
        ));
        assert!(!can_launch_from_tray(
            true,
            false,
            false,
            false,
            Some(&LaunchAction::Play)
        ));
    }
    #[test]
    fn tray_labels_preserve_unicode_and_escape_mnemonics() {
        assert_eq!(play_menu_label("  ", "Играть"), "Игра не выбрана");
        assert_eq!(play_menu_label("A&B", "Играть"), "Играть: A&&B");
        assert_eq!(play_menu_label("PEAK", "Установить"), "Установить: PEAK");
        let mut text = [9u16; 5];
        fill_wide(&mut text, "аб🎮в");
        assert_eq!(&text[..4], &"аб🎮".encode_utf16().collect::<Vec<_>>());
        assert_eq!(text[4], 0);
        let mut short = [9u16; 4];
        fill_wide(&mut short, "аб🎮");
        assert_eq!(&short, &[1072, 1073, 0, 0]);
    }
    #[test]
    fn tray_hint_persistence_preserves_preferences() {
        let temp = tempfile::tempdir().unwrap();
        let store = ConfigStore::new(AppPaths::isolated(temp.path()));
        store
            .update(|c| {
                c.speed_limit_mbps = 17;
                c.minimize_to_tray = true;
            })
            .unwrap();
        remember_tray_hint(&store).unwrap();
        let config = store.load().unwrap();
        assert!(config.tray_hint_shown);
        assert!(config.minimize_to_tray);
        assert_eq!(config.speed_limit_mbps, 17);
    }
    #[test]
    fn closing_instance_hands_mutex_to_new_process() {
        let name = wide(&format!(
            "Local\\ChillHub.Test.Handoff.{}",
            uuid::Uuid::new_v4()
        ));
        let (held, ready) = mpsc::channel();
        let (release, released) = mpsc::channel();
        let holder = thread::spawn(move || unsafe {
            let mutex = CreateMutexW(ptr::null(), 1, name.as_ptr());
            assert!(!mutex.is_null());
            held.send(mutex as usize).unwrap();
            released.recv().unwrap();
            ReleaseMutex(mutex);
        });
        let mutex = ready.recv().unwrap() as HANDLE;
        let acknowledged = unsafe { CreateEventW(ptr::null(), 0, 0, ptr::null()) };
        assert!(!acknowledged.is_null());
        unsafe {
            SetEvent(acknowledged);
        }
        assert!(!await_activation_or_ownership(mutex, acknowledged, 100).unwrap());
        release.send(()).unwrap();
        assert!(await_activation_or_ownership(mutex, acknowledged, 5000).unwrap());
        unsafe {
            ReleaseMutex(mutex);
            CloseHandle(mutex);
            CloseHandle(acknowledged);
        }
        holder.join().unwrap();
    }
    #[test]
    fn isolated_profile_aliases_share_instance_key_and_secondary_activation() {
        use std::os::windows::ffi::{OsStrExt, OsStringExt};
        let temp = tempfile::tempdir().unwrap();
        let directory = temp.path().join("Long isolated launcher profile");
        std::fs::create_dir_all(&directory).unwrap();
        let canonical = std::fs::canonicalize(&directory).unwrap();
        let input: Vec<u16> = directory.as_os_str().encode_wide().chain([0]).collect();
        let mut output = vec![0u16; 32768];
        let length = unsafe {
            windows_sys::Win32::Storage::FileSystem::GetShortPathNameW(
                input.as_ptr(),
                output.as_mut_ptr(),
                output.len() as u32,
            )
        } as usize;
        assert!(length > 0 && length < output.len());
        let short = PathBuf::from(std::ffi::OsString::from_wide(&output[..length]));
        assert_eq!(
            instance_suffix(&short, true).unwrap(),
            instance_suffix(&canonical, true).unwrap()
        );
        assert_eq!(
            instance_suffix(&short.join("missing/profile"), true).unwrap(),
            instance_suffix(&canonical.join("missing/profile"), true).unwrap()
        );
        assert_ne!(
            instance_suffix(&canonical.join("missing/other"), true).unwrap(),
            instance_suffix(&canonical.join("missing/profile"), true).unwrap()
        );
        assert!(!directory.join("missing").exists());
        assert!(instance_suffix(Path::new("relative-profile"), true).is_err());
        assert_eq!(instance_suffix(&directory, false).unwrap(), "");

        let (held, ready) = mpsc::channel();
        let (release, released) = mpsc::channel();
        let holder = thread::spawn(move || {
            let primary = Instance::acquire_for_profile(&short, &[], true).unwrap();
            assert!(primary.primary);
            held.send(()).unwrap();
            assert_eq!(
                unsafe { WaitForSingleObject(primary.event as HANDLE, 5000) },
                WAIT_OBJECT_0
            );
            unsafe { SetEvent(primary.acknowledged as HANDLE) };
            released.recv().unwrap();
            let event = primary.event as HANDLE;
            let acknowledged = primary.acknowledged as HANDLE;
            drop(primary);
            // No listener owns these handles in this GUI-free fixture.
            unsafe {
                CloseHandle(event);
                CloseHandle(acknowledged);
            }
        });
        ready.recv().unwrap();
        let secondary = Instance::acquire_for_profile(&canonical, &[], true).unwrap();
        assert!(!secondary.primary);
        drop(secondary);
        release.send(()).unwrap();
        holder.join().unwrap();
    }
    #[test]
    fn requests_expire_and_are_consumed_once() {
        let temp = tempfile::tempdir().unwrap();
        let args = vec!["--game".into(), "peak".into()];
        assert!(ShortcutRequest::parse_args(&args).unwrap().is_some());
        publish_request(temp.path(), &args).unwrap();
        let directory = temp.path().join("requests");
        let stale = directory.join("old.json");
        std::fs::write(&stale, serde_json::to_vec(&args).unwrap()).unwrap();
        let now = SystemTime::now();
        std::fs::File::options()
            .write(true)
            .open(&stale)
            .unwrap()
            .set_times(std::fs::FileTimes::new().set_modified(now - Duration::from_secs(121)))
            .unwrap();
        std::fs::write(
            directory.join("oversize.json"),
            vec![b' '; REQUEST_LIMIT as usize + 1],
        )
        .unwrap();
        std::fs::write(directory.join("bad.json"), b"not json").unwrap();
        assert_eq!(consume_requests(temp.path(), now), vec![args]);
        assert!(!stale.exists());
        assert!(consume_requests(temp.path(), now).is_empty());
        assert!(fresh_request(now + Duration::from_secs(120), now));
        assert!(!fresh_request(now + Duration::from_secs(121), now));
    }
}
