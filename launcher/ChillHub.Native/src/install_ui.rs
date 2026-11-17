//! Non-blocking space hints and a user-selected fallback for unwritable libraries.
use super::*;
use chillhub_native::launch::LaunchAction;
use chillhub_native::service::DownloadEstimate;

pub(super) enum PlayFailure {
    General(String),
    Folder(PathBuf, String),
}
fn set_hint(ui: &AppWindow, text: impl Into<SharedString>, space_low: bool) {
    ui.set_install_hint(text.into());
    ui.set_install_space_status(if space_low { "Мало места" } else { "" }.into());
    ui.set_install_space_low(space_low);
}

pub(super) fn clear_hint(ui: &AppWindow) {
    set_hint(ui, "", false);
}

fn apply_estimate(ui: &AppWindow, estimate: &DownloadEstimate) {
    controller::set_disk_label(ui, estimate.available_bytes);
    let (hint, space_low) = describe(estimate);
    set_hint(ui, hint, space_low);
}
impl From<String> for PlayFailure {
    fn from(value: String) -> Self {
        Self::General(value)
    }
}
pub(super) fn can_choose_folder_after(error: &std::io::Error) -> bool {
    error.kind() == std::io::ErrorKind::PermissionDenied
        || matches!(error.raw_os_error(), Some(5 | 19 | 32 | 33 | 1314))
}

pub(super) fn preflight(root: &std::path::Path) -> Result<(), std::io::Error> {
    use std::io::Write;
    chillhub_native::manifest::check_no_links(root)?;
    let mut existing = root;
    while !existing.try_exists()? {
        existing = existing.parent().ok_or_else(|| {
            std::io::Error::new(std::io::ErrorKind::NotFound, "Диск или папка недоступны")
        })?;
    }
    if !existing.is_dir() {
        return Err(std::io::Error::new(
            std::io::ErrorKind::NotADirectory,
            "Путь занят файлом",
        ));
    }
    let _guard = chillhub_native::manifest::DirectoryGuard::acquire(existing, false)?;
    // create_new and an unpredictable name cannot overwrite another application's files.
    let path = existing.join(format!(".chillhub-write-probe-{}", uuid::Uuid::new_v4()));
    let mut options = std::fs::OpenOptions::new();
    options.write(true).create_new(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        options.share_mode(0).custom_flags(
            windows_sys::Win32::Storage::FileSystem::FILE_FLAG_DELETE_ON_CLOSE
                | windows_sys::Win32::Storage::FileSystem::FILE_FLAG_OPEN_REPARSE_POINT,
        );
    }
    let mut file = options.open(&path)?;
    let result = file.write_all(b"ChillHub").and_then(|_| file.flush());
    drop(file);
    #[cfg(not(windows))]
    {
        let _ = std::fs::remove_file(path);
    }
    result
}

pub(super) fn hint(ui: &AppWindow, state: &State) {
    if state.borrow().estimate_inflight {
        return;
    }
    let Some((game, target)) = controller::selected(state) else {
        return;
    };
    let s = state.borrow();
    if s.option
        .as_ref()
        .is_none_or(|o| matches!(o.action, LaunchAction::Play | LaunchAction::Unavailable))
        || !ui.get_show_install_info()
    {
        return;
    }
    let services = s.services.clone();
    let generation = s.choice_generation;
    drop(s);
    state.borrow_mut().estimate_inflight = true;
    set_hint(ui, "Уточняем объём файлов и свободное место…", false);
    controller::background(
        ui,
        move || {
            std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                services.estimate_download(&game, target)
            }))
            .unwrap_or_else(|_| Err("Не удалось рассчитать размер файлов".into()))
        },
        move |ui, state, result| {
            state.borrow_mut().estimate_inflight = false;
            let current_generation = state.borrow().choice_generation;
            if !apply_current_result(ui, generation, current_generation, &result) {
                hint(ui, state);
                return;
            }
            if ui.get_show_install_info()
                && let Err(error) = result
            {
                chillhub_native::diagnostics::log(
                    &state.borrow().services.paths.data_dir,
                    "install-estimate",
                    &error,
                );
            }
        },
    );
}

fn apply_current_result(
    ui: &AppWindow,
    generation: u64,
    current_generation: u64,
    result: &Result<DownloadEstimate, String>,
) -> bool {
    if generation != current_generation {
        return false;
    }
    if !ui.get_show_install_info() {
        clear_hint(ui);
    } else {
        match result {
            Ok(estimate) => apply_estimate(ui, estimate),
            Err(_) => set_hint(ui, "Размер уточнится перед загрузкой", false),
        }
    }
    true
}

fn describe(e: &DownloadEstimate) -> (String, bool) {
    let size = queue_ui::size;
    let available = e
        .available_bytes
        .map(|v| format!(" · свободно {}", size(v)))
        .unwrap_or_default();
    let space_low = e.available_bytes.is_some_and(|v| v < e.required_free_bytes);
    let warning = if space_low {
        "Недостаточно места. "
    } else {
        ""
    };
    (
        format!(
            "{warning}Подготовить файлы: {} · нужно места: {}{available}",
            size(e.bytes_to_prepare),
            size(e.required_free_bytes)
        ),
        space_low,
    )
}

#[cfg(test)]
#[path = "install_ui_tests.rs"]
mod tests;
