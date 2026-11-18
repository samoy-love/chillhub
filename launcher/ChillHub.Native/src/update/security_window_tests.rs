use super::*;
use std::{
    cell::Cell,
    fs,
    os::windows::process::CommandExt,
    path::PathBuf,
    process::{Child, Command, ExitStatus, Stdio},
    ptr::null_mut,
    sync::atomic::{AtomicBool, Ordering},
    thread::sleep,
    time::Instant,
};
use windows_sys::Win32::{
    Foundation::{LRESULT, WPARAM},
    System::{LibraryLoader::GetModuleHandleW, Threading::CREATE_NO_WINDOW},
    UI::WindowsAndMessaging::{
        CreateWindowExW, DefWindowProcW, DestroyWindow, DispatchMessageW, MSG, PM_REMOVE,
        PeekMessageW, RegisterClassW, WNDCLASSW,
    },
};

const SIGNALS_ENV: &str = "CHILLHUB_WINDOW_CLOSE_FIXTURE_DIR";
const MODE_ENV: &str = "CHILLHUB_WINDOW_CLOSE_FIXTURE_MODE";
const FIXTURE_TIMEOUT: Duration = Duration::from_secs(20);
const POLL: Duration = Duration::from_millis(10);

#[test]
fn close_preflight_covers_races_without_bypassing_the_install_lock() {
    prepare_security_window(|| Ok(()), |_| panic!("the installation is available")).unwrap();
    for outcome in [CloseResult::Closed, CloseResult::NotFound] {
        let probes = Cell::new(0);
        prepare_security_window(
            || {
                probes.set(probes.get() + 1);
                if probes.get() == 1 {
                    Err("closing".into())
                } else {
                    Ok(())
                }
            },
            |timeout| {
                assert_eq!(timeout, Duration::from_secs(3));
                outcome
            },
        )
        .unwrap();
        assert_eq!(probes.get(), 2);
    }
    assert_eq!(
        prepare_security_window(|| Err("new holder".into()), |_| CloseResult::Closed),
        Err("new holder".into())
    );
    let attempts = Cell::new(0);
    let error = prepare_security_window(
        || {
            attempts.set(attempts.get() + 1);
            Err("busy".into())
        },
        |timeout| {
            assert_eq!(timeout, CLOSE_TIMEOUT);
            CloseResult::Busy
        },
    )
    .unwrap_err();
    assert!(error.contains("Завершите настройку защиты"));
    assert_eq!(attempts.get(), 1);
}

#[test]
fn own_idle_window_closes_but_busy_or_mismatched_windows_remain_alive() {
    let temp = tempfile::tempdir().unwrap();
    let install = temp.path().join("own");
    let foreign = temp.path().join("foreign");
    fs::create_dir(&install).unwrap();
    fs::create_dir(&foreign).unwrap();
    let fixture_exe = install.join(HELPER_NAME);
    fs::copy(std::env::current_exe().unwrap(), &fixture_exe).unwrap();
    fs::write(foreign.join(HELPER_NAME), b"same name, different location").unwrap();

    // A real HWND in the right executable is insufficient without its PID marker.
    let mut wrong_marker = FixtureWindow::start(&fixture_exe, temp.path(), "wrong-marker");
    assert_eq!(
        close_idle_security_window(&install, CLOSE_TIMEOUT),
        CloseResult::NotFound
    );
    assert!(!wrong_marker.received_close());
    assert!(wrong_marker.child.try_wait().unwrap().is_none());
    wrong_marker.stop();

    let mut busy = FixtureWindow::start(&fixture_exe, temp.path(), "busy");
    assert_eq!(
        close_idle_security_window(&foreign, CLOSE_TIMEOUT),
        CloseResult::NotFound
    );
    assert!(!busy.received_close());
    let payload = temp.path().join("staged.zip");
    fs::write(&payload, b"staged-update").unwrap();
    assert_eq!(
        close_idle_security_window(&install, Duration::from_millis(200)),
        CloseResult::Busy
    );
    let deadline = Instant::now() + FIXTURE_TIMEOUT;
    while !busy.received_close() && Instant::now() < deadline {
        sleep(POLL);
    }
    assert!(busy.received_close(), "the native close request arrived");
    assert!(
        busy.child.try_wait().unwrap().is_none(),
        "busy fixture remains alive"
    );
    assert_eq!(fs::read(payload).unwrap(), b"staged-update");
    busy.stop();

    let mut idle = FixtureWindow::start(&fixture_exe, temp.path(), "idle");
    prepare_for_update(&install).unwrap();
    assert!(idle.received_close());
    assert!(
        idle.child
            .try_wait()
            .unwrap()
            .is_some_and(|status| status.success())
    );
    prepare_for_update(&install).unwrap();
}

struct FixtureWindow {
    child: Child,
    signals: PathBuf,
}

impl FixtureWindow {
    fn start(exe: &Path, parent: &Path, mode: &str) -> Self {
        let signals = parent.join(mode);
        fs::create_dir(&signals).unwrap();
        let module = module_path!().split_once("::").unwrap().1;
        let name = format!("{module}::security_window_fixture");
        let log = fs::File::create(signals.join("fixture.log")).unwrap();
        let child = Command::new(exe)
            .args(["--exact", &name, "--test-threads=1", "--nocapture"])
            .env(SIGNALS_ENV, &signals)
            .env(MODE_ENV, mode)
            .stdin(Stdio::null())
            .stdout(Stdio::from(log.try_clone().unwrap()))
            .stderr(Stdio::from(log))
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .unwrap();
        let mut fixture = Self { child, signals };
        let deadline = Instant::now() + FIXTURE_TIMEOUT;
        while !fixture.signals.join("ready").is_file() {
            if let Some(status) = fixture.child.try_wait().unwrap() {
                let log =
                    fs::read_to_string(fixture.signals.join("fixture.log")).unwrap_or_default();
                panic!("fixture exited before registering a window ({status})\n{log}");
            }
            assert!(Instant::now() < deadline, "fixture startup timed out");
            sleep(POLL);
        }
        fixture
    }

    fn received_close(&self) -> bool {
        self.signals.join("close").is_file()
    }

    fn stop(&mut self) {
        fs::write(self.signals.join("stop"), []).unwrap();
        assert!(self.wait().is_some_and(|status| status.success()));
    }

    fn wait(&mut self) -> Option<ExitStatus> {
        let deadline = Instant::now() + FIXTURE_TIMEOUT;
        while Instant::now() < deadline {
            if let Some(status) = self.child.try_wait().ok()? {
                return Some(status);
            }
            sleep(POLL);
        }
        None
    }
}

impl Drop for FixtureWindow {
    fn drop(&mut self) {
        let _ = fs::write(self.signals.join("stop"), []);
        let _ = self.wait();
    }
}

static CLOSE_RECEIVED: AtomicBool = AtomicBool::new(false);

unsafe extern "system" fn fixture_window_proc(
    window: HWND,
    message: u32,
    wparam: WPARAM,
    lparam: LPARAM,
) -> LRESULT {
    if message == WM_CLOSE {
        CLOSE_RECEIVED.store(true, Ordering::Relaxed);
        return 0;
    }
    unsafe { DefWindowProcW(window, message, wparam, lparam) }
}

#[test]
fn security_window_fixture() {
    let Some(signals) = std::env::var_os(SIGNALS_ENV).map(PathBuf::from) else {
        return;
    };
    let mode = std::env::var(MODE_ENV).unwrap();
    let exe = std::env::current_exe().unwrap();
    let mutex = update_mutex_name(exe.parent().unwrap()).unwrap();
    let _guard = ProcessLock::acquire(&mutex).unwrap();
    let class_name = windows_sys::w!("ChillHub.Update.Tests.HiddenWindow");
    let instance = unsafe { GetModuleHandleW(std::ptr::null()) };
    let class = WNDCLASSW {
        hInstance: instance,
        lpszClassName: class_name,
        lpfnWndProc: Some(fixture_window_proc),
        ..Default::default()
    };
    assert_ne!(unsafe { RegisterClassW(&class) }, 0);
    let window = unsafe {
        CreateWindowExW(
            0,
            class_name,
            class_name,
            0,
            0,
            0,
            32,
            32,
            null_mut(),
            null_mut(),
            instance,
            std::ptr::null(),
        )
    };
    assert!(!window.is_null());
    assert!(!mark_security_update_window(0));
    if mode == "wrong-marker" {
        let wrong = std::process::id().wrapping_add(1).max(1);
        assert_ne!(
            unsafe { SetPropW(window, WINDOW_PROPERTY, wrong as usize as HANDLE) },
            0
        );
        assert_eq!(window_pid(window), None);
    } else {
        assert!(mark_security_update_window(window as isize));
        assert_eq!(window_pid(window), Some(std::process::id()));
    }
    fs::write(signals.join("ready"), []).unwrap();

    let deadline = Instant::now() + FIXTURE_TIMEOUT;
    let mut message = MSG::default();
    while Instant::now() < deadline && !signals.join("stop").is_file() {
        while unsafe { PeekMessageW(&mut message, window, 0, 0, PM_REMOVE) } != 0 {
            unsafe { DispatchMessageW(&message) };
        }
        if CLOSE_RECEIVED.swap(false, Ordering::Relaxed) {
            fs::write(signals.join("close"), []).unwrap();
            if mode == "idle" {
                break;
            }
        }
        sleep(POLL);
    }
    assert_ne!(unsafe { DestroyWindow(window) }, 0);
}
