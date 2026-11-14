//! Non-blocking space hints and a user-selected fallback for unwritable libraries.
use super::*;
use chillhub_native::launch::LaunchAction;

pub(super) enum PlayFailure {
    General(String),
    Folder(PathBuf, String),
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
        || ui.get_operation_busy()
        || !ui.get_can_play()
    {
        return;
    }
    let services = s.services.clone();
    let generation = s.choice_generation;
    drop(s);
    state.borrow_mut().estimate_inflight = true;
    ui.set_install_hint("Уточняем объём файлов и свободное место…".into());
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
            if state.borrow().choice_generation != generation {
                hint(ui, state);
                return;
            }
            if ui.get_operation_busy() {
                ui.set_install_hint("".into());
                return;
            }
            match result {
                Ok(e) => ui.set_install_hint(describe(&e).into()),
                Err(error) => {
                    ui.set_install_hint("Размер уточнится перед загрузкой".into());
                    chillhub_native::diagnostics::log(
                        &state.borrow().services.paths.data_dir,
                        "install-estimate",
                        &error,
                    );
                }
            }
        },
    );
}

fn describe(e: &chillhub_native::service::DownloadEstimate) -> String {
    let size = queue_ui::size;
    let available = e
        .available_bytes
        .map(|v| format!(" · свободно {}", size(v)))
        .unwrap_or_default();
    let warning = if e.available_bytes.is_some_and(|v| v < e.required_free_bytes) {
        "Недостаточно места. "
    } else {
        ""
    };
    format!(
        "{warning}Подготовить файлы: {} · нужно места: {}{available}",
        size(e.bytes_to_prepare),
        size(e.required_free_bytes)
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn folder_recovery_uses_native_codes_not_localized_messages() {
        for code in [5, 19, 32, 33, 1314] {
            assert!(can_choose_folder_after(&std::io::Error::from_raw_os_error(
                code
            )));
        }
        assert!(!can_choose_folder_after(
            &std::io::Error::from_raw_os_error(112)
        ));
    }
    #[test]
    fn write_probe_leaves_no_files_or_game_directory() {
        let t = tempfile::tempdir().unwrap();
        preflight(&t.path().join("missing/game")).unwrap();
        assert_eq!(std::fs::read_dir(t.path()).unwrap().count(), 0);
    }
    #[test]
    fn estimate_never_calls_planned_work_network_traffic() {
        let s = describe(&chillhub_native::service::DownloadEstimate {
            bytes_to_prepare: 1024,
            required_free_bytes: 2048,
            available_bytes: Some(1024),
        });
        assert!(s.contains("Недостаточно места"));
        assert!(!s.contains("скачать"));
    }
}
