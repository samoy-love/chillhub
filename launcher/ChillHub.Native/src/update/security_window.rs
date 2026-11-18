//! Before update handoff, request a normal close from this installation's idle helper.

use super::{ParentProcess, ProcessLock, update_mutex_name};
use crate::{manifest::safe_join, security::HELPER_NAME};
use std::{path::Path, time::Duration};
#[cfg(any(feature = "helper-ui", test))]
use windows_sys::Win32::{Foundation::HANDLE, UI::WindowsAndMessaging::SetPropW};
use windows_sys::{
    Win32::{
        Foundation::{HWND, LPARAM},
        UI::WindowsAndMessaging::{
            EnumWindows, GetPropW, GetWindowThreadProcessId, PostMessageW, WM_CLOSE,
        },
    },
    core::BOOL,
};

const CLOSE_TIMEOUT: Duration = Duration::from_secs(3);
const WINDOW_PROPERTY: *const u16 = windows_sys::w!("ChillHub.SecurityHelper.UpdateClose");

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum CloseResult {
    NotFound,
    Closed,
    Busy,
}

pub(super) fn prepare_for_update(install: &Path) -> Result<(), String> {
    let mutex = update_mutex_name(install)?;
    prepare_security_window(
        || ProcessLock::acquire(&mutex).map(|_| ()),
        |timeout| close_idle_security_window(install, timeout),
    )
}

fn prepare_security_window(
    probe_install: impl Fn() -> Result<(), String>,
    close: impl FnOnce(Duration) -> CloseResult,
) -> Result<(), String> {
    if probe_install().is_ok() {
        return Ok(());
    }
    match close(CLOSE_TIMEOUT) {
        CloseResult::Closed | CloseResult::NotFound => probe_install(),
        CloseResult::Busy => Err(
            "Завершите настройку защиты и повторите обновление. Загруженные файлы сохранены."
                .into(),
        ),
    }
}

#[cfg(any(feature = "helper-ui", test))]
pub(crate) fn mark_security_update_window(hwnd: isize) -> bool {
    hwnd != 0
        && unsafe {
            SetPropW(
                hwnd as HWND,
                WINDOW_PROPERTY,
                std::process::id() as usize as HANDLE,
            ) != 0
        }
}

fn window_pid(window: HWND) -> Option<u32> {
    let mut pid = 0;
    let thread = unsafe { GetWindowThreadProcessId(window, &mut pid) };
    let marker = unsafe { GetPropW(window, WINDOW_PROPERTY) };
    (thread != 0 && pid != 0 && marker as usize == pid as usize).then_some(pid)
}

struct Search<'a> {
    executable: &'a Path,
    target: Option<(HWND, u32, ParentProcess)>,
}

unsafe extern "system" fn inspect_window(window: HWND, parameter: LPARAM) -> BOOL {
    // EnumWindows invokes this callback synchronously while Search is alive.
    let search = unsafe { &mut *(parameter as *mut Search<'_>) };
    let Some(pid) = window_pid(window) else {
        return 1;
    };
    let Ok(process) = ParentProcess::open(pid, search.executable) else {
        return 1;
    };
    search.target = Some((window, pid, process));
    0
}

fn close_idle_security_window(install: &Path, timeout: Duration) -> CloseResult {
    let Ok(executable) = safe_join(install, HELPER_NAME) else {
        return CloseResult::NotFound;
    };
    let mut search = Search {
        executable: &executable,
        target: None,
    };
    unsafe {
        EnumWindows(Some(inspect_window), (&raw mut search) as LPARAM);
    }
    let Some((window, pid, process)) = search.target else {
        return CloseResult::NotFound;
    };
    // A held process handle pins the PID. Recheck the window before posting;
    // the helper's own close handler refuses while an operation is running.
    if window_pid(window) != Some(pid) {
        return CloseResult::NotFound;
    }
    let posted = unsafe { PostMessageW(window, WM_CLOSE, 0, 0) } != 0;
    if process
        .wait(if posted { timeout } else { Duration::ZERO })
        .is_ok()
    {
        CloseResult::Closed
    } else {
        CloseResult::Busy
    }
}

#[cfg(test)]
#[path = "security_window_tests.rs"]
mod tests;
