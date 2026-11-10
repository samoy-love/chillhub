//! Slint presentation for the existing NSIS engine. No shell command strings,
//! elevated process or second implementation of file/registry installation.
use crate::{
    helper_runtime::{self, HelperKind, HelperLog},
    ui::SetupWindow,
};
use slint::ComponentHandle;
use std::{
    path::{Component, Path, PathBuf},
    process::Command,
    sync::Arc,
};

#[derive(Clone, Debug)]
struct Request {
    removing: bool,
    preview: bool,
    engine: PathBuf,
    install: PathBuf,
    games: PathBuf,
    version: String,
    needs_webview: bool,
    allow_downgrade: bool,
}

impl Request {
    fn parse(args: &[String]) -> Result<Self, String> {
        if args.len() != 6
            || !matches!(
                args[0].as_str(),
                "install" | "uninstall" | "preview-install" | "preview-uninstall"
            )
        {
            return Err("Откройте установщик Chill Hub, чтобы начать установку.".into());
        }
        let request = Self {
            removing: args[0].ends_with("uninstall"),
            preview: args[0].starts_with("preview-"),
            engine: args[1].clone().into(),
            install: args[2].clone().into(),
            games: args[3].clone().into(),
            version: args[4].clone(),
            needs_webview: args[5] == "1",
            allow_downgrade: false,
        };
        if !request.preview && (!request.engine.is_absolute() || !request.engine.is_file()) {
            return Err("Не найден установщик. Скачайте его заново.".into());
        }
        Ok(request)
    }
}

fn directory(raw: &str) -> Result<PathBuf, String> {
    let path = PathBuf::from(raw.trim().replace('/', "\\"));
    if raw.contains(['\0', '\r', '\n', '"'])
        || !path.is_absolute()
        || path.parent().is_none()
        || path
            .components()
            .any(|c| matches!(c, Component::ParentDir | Component::CurDir))
    {
        return Err(
            "Выберите отдельную папку с полным путём, например D:\\Games\\ChillHub.".into(),
        );
    }
    for name in [
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "WINDIR",
        "ProgramFiles",
        "ProgramFiles(x86)",
    ] {
        if let Some(value) = std::env::var_os(name) {
            let protected = PathBuf::from(value);
            let identity = PathBuf::from(
                crate::paths::canonical_path_with_missing(&path)
                    .map_err(|e| e.to_string())?
                    .to_string_lossy()
                    .to_lowercase(),
            );
            let protected = PathBuf::from(
                crate::paths::canonical_path_with_missing(&protected)
                    .map_err(|e| e.to_string())?
                    .to_string_lossy()
                    .to_lowercase(),
            );
            if identity == protected || (name == "WINDIR" && identity.starts_with(&protected)) {
                return Err(
                    "Эту системную папку использовать нельзя. Выберите отдельную папку Chill Hub."
                        .into(),
                );
            }
        }
    }
    Ok(path)
}

fn validate_paths(install: &Path, games: &Path) -> Result<(), String> {
    let install = crate::paths::canonical_path_with_missing(install).map_err(|e| e.to_string())?;
    let games = crate::paths::canonical_path_with_missing(games).map_err(|e| e.to_string())?;
    let identity = |p: &Path| p.to_string_lossy().to_lowercase();
    let install = PathBuf::from(identity(&install));
    let games = PathBuf::from(identity(&games));
    if install.starts_with(&games) || games.starts_with(&install) {
        return Err(
            "Папки лаунчера и игр должны быть раздельными и не находиться друг внутри друга."
                .into(),
        );
    }
    if install.is_dir()
        && !install.join("ChillHub.exe").is_file()
        && std::fs::read_dir(&install)
            .map_err(|e| e.to_string())?
            .next()
            .is_some()
    {
        return Err("В выбранной папке уже есть другие файлы. Выберите пустую папку или прежнюю папку Chill Hub.".into());
    }
    Ok(())
}

fn engine_command(request: &Request, shortcut: bool, delete_games: bool, webview: bool) -> Command {
    let mut cmd = Command::new(&request.engine);
    cmd.arg("/S");
    if request.removing {
        if delete_games {
            cmd.arg("/DELETEGAMES");
        }
    } else {
        if request.allow_downgrade {
            cmd.arg("/ALLOWDOWNGRADE");
        }
        if !shortcut {
            cmd.arg("/NODESKTOP");
        }
        if webview {
            cmd.arg("/WEBVIEW2");
        }
    }
    // NSIS consumes the remainder of the Windows command line for /D and _?.
    // It MUST remain unquoted and last, including when the path has spaces.
    #[cfg(windows)]
    {
        use std::os::windows::process::CommandExt;
        // GetOptions skips an option quoted as a whole. Quote the value only;
        // directory() rejects quotes/control characters before this boundary.
        if !request.removing {
            cmd.raw_arg(format!("/GAMESDIR=\"{}\"", request.games.display()));
        }
        cmd.raw_arg(format!(
            "{}{}",
            if request.removing { "_?=" } else { "/D=" },
            request.install.display()
        ));
        cmd.creation_flags(0x08000000);
    }
    cmd
}

fn execute(
    request: &Request,
    shortcut: bool,
    delete_games: bool,
    webview: bool,
) -> Result<String, String> {
    if request.preview {
        return Err("Это предпросмотр: файлы и настройки не изменяются.".into());
    }
    let status = engine_command(request, shortcut, delete_games, webview)
        .status()
        .map_err(|e| format!("Не удалось начать операцию: {e}"))?;
    let missing_webview = !request.removing && webview && status.code() == Some(20);
    if !status.success() && !missing_webview {
        return Err(format!(
            "Операция не завершена (код {}). Закройте Chill Hub и игры, проверьте свободное место и доступ к папке. Для более старой версии нужно отдельное подтверждение.",
            status.code().unwrap_or(-1)
        ));
    }
    if request.removing {
        if request.install.join("ChillHub.exe").exists() {
            return Err("Некоторые файлы заняты. Закройте Chill Hub и повторите удаление.".into());
        }
        if delete_games && request.games.exists() {
            return Err("Лаунчер удалён, но папка игр сохранилась. Проверьте занятые файлы и доступ к папке.".into());
        }
        Ok(if delete_games {
            "Лаунчер, настройки и игры удалены."
        } else {
            "Лаунчер и настройки удалены. Игры остались в выбранной папке."
        }
        .into())
    } else {
        let version = std::fs::read_to_string(request.install.join("launcher.version"))
            .map_err(|_| "Не удалось проверить установленную версию. Повторите установку.")?;
        if version.trim() != request.version || !request.install.join("ChillHub.exe").is_file() {
            return Err("Проверка установленной версии не прошла. Повторите установку.".into());
        }
        if missing_webview {
            return Ok("Лаунчер установлен, но компонент для YouTube установить не удалось. Игры и обычные трейлеры работают. Подключитесь к интернету и повторите установку, чтобы добавить компонент.".into());
        }
        Ok(
            "Лаунчер установлен. Настройка антивируса доступна в разделе «Помощь» после запуска."
                .into(),
        )
    }
}

pub fn run(args: Vec<String>) -> Result<(), String> {
    let request = Arc::new(Request::parse(&args)?);
    helper_runtime::select_renderer()?;
    // Outside AppData: removal must be able to delete the settings directory.
    let log = HelperLog::in_directory(
        &std::env::temp_dir().join("ChillHub/setup"),
        HelperKind::Setup,
    )
    .ok();
    if let Some(log) = &log {
        let _ = log.record(
            "setup.start",
            if request.removing {
                "uninstall"
            } else {
                "install"
            },
        );
    }
    let ui = SetupWindow::new().map_err(|e| e.to_string())?;
    helper_runtime::style_window(ui.window());
    ui.set_removing(request.removing);
    ui.set_version(request.version.clone().into());
    ui.set_install_path(request.install.to_string_lossy().as_ref().into());
    ui.set_games_path(request.games.to_string_lossy().as_ref().into());
    ui.set_needs_webview(request.needs_webview);
    if !request.removing && !request.preview {
        use winreg::{RegKey, enums::HKEY_CURRENT_USER};
        let installed = RegKey::predef(HKEY_CURRENT_USER)
            .open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Uninstall\ChillHub")
            .and_then(|key| key.get_value::<String, _>("DisplayVersion"))
            .unwrap_or_default();
        if crate::update::version_parts(&installed).ok()
            > crate::update::version_parts(&request.version).ok()
        {
            ui.set_downgrade_from(installed.into());
        }
    }
    ui.on_cancel(|| {
        let _ = slint::quit_event_loop();
    });
    let weak = ui.as_weak();
    ui.window().on_close_requested(move || {
        if weak.upgrade().is_some_and(|u| u.get_busy()) {
            slint::CloseRequestResponse::KeepWindowShown
        } else {
            slint::CloseRequestResponse::HideWindow
        }
    });
    let weak = ui.as_weak();
    ui.on_browse(move |games| {
        let Some(ui) = weak.upgrade() else { return };
        // Resolve HWND at the click, after the event loop has created it.
        let owner = helper_runtime::style_window(ui.window());
        ui.set_busy(true);
        ui.set_picking(true);
        let initial = PathBuf::from(
            if games {
                ui.get_games_path()
            } else {
                ui.get_install_path()
            }
            .as_str(),
        );
        let weak = ui.as_weak();
        std::thread::spawn(move || {
            let result =
                helper_runtime::caught(|| crate::folder_picker::choose_folder(owner, &initial));
            let _ = weak.upgrade_in_event_loop(move |ui| {
                ui.set_busy(false);
                ui.set_picking(false);
                match result {
                    Ok(Some(path)) => {
                        if games {
                            ui.set_games_path(path.to_string_lossy().as_ref().into());
                        } else {
                            ui.set_install_path(path.to_string_lossy().as_ref().into());
                        }
                    }
                    Ok(None) => {}
                    Err(e) => {
                        ui.set_error(true);
                        ui.set_message(e.into());
                    }
                }
            });
        });
    });
    let weak = ui.as_weak();
    let security_request = request.clone();
    ui.on_security(move || {
        if security_request.preview {
            return;
        }
        let result = Command::new(security_request.install.join("ChillHub.SecurityHelper.exe"))
            .arg("--remove")
            .spawn();
        if let (Err(e), Some(ui)) = (result, weak.upgrade()) {
            ui.set_error(true);
            ui.set_message(format!("Не удалось открыть помощник: {e}").into());
        }
    });
    let weak = ui.as_weak();
    ui.on_submit(move || {
        let Some(ui) = weak.upgrade() else { return };
        if ui.get_done() {
            if !request.removing
                && let Err(e) =
                    Command::new(Path::new(ui.get_install_path().as_str()).join("ChillHub.exe"))
                        .spawn()
            {
                ui.set_error(true);
                ui.set_message(format!("Не удалось открыть лаунчер: {e}").into());
                return;
            }
            let _ = slint::quit_event_loop();
            return;
        }
        let paths = (|| {
            let install = directory(ui.get_install_path().as_str())?;
            let games = directory(ui.get_games_path().as_str())?;
            if !request.removing {
                validate_paths(&install, &games)?;
            }
            Ok::<_, String>((install, games))
        })();
        let (install, games) = match paths {
            Ok(paths) => paths,
            Err(e) => {
                ui.set_error(true);
                ui.set_message(e.into());
                return;
            }
        };
        let mut request = (*request).clone();
        request.allow_downgrade = ui.get_allow_downgrade();
        request.install = install;
        request.games = games;
        let shortcut = ui.get_desktop_shortcut();
        let delete_games = ui.get_delete_games();
        let webview = ui.get_install_webview();
        ui.set_busy(true);
        ui.set_error(false);
        ui.set_message("Подготавливаем файлы. Это может занять несколько минут.".into());
        let weak = ui.as_weak();
        let log = log.clone();
        std::thread::spawn(move || {
            let result =
                helper_runtime::caught(|| execute(&request, shortcut, delete_games, webview));
            if let Some(log) = log {
                let _ = log.record(
                    if result.is_ok() {
                        "setup.complete"
                    } else {
                        "setup.error"
                    },
                    result
                        .as_ref()
                        .map(String::as_str)
                        .unwrap_or_else(|e| e.as_str()),
                );
            }
            let _ = weak.upgrade_in_event_loop(move |ui| {
                ui.set_busy(false);
                match result {
                    Ok(message) => {
                        ui.set_done(true);
                        ui.set_message(message.into());
                    }
                    Err(message) => {
                        ui.set_error(true);
                        ui.set_message(message.into());
                    }
                }
            });
        });
    });
    ui.run().map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(windows)]
    #[test]
    fn nsis_arguments_quote_only_the_games_value_and_keep_destination_last() {
        let args = [
            "preview-install",
            "setup.exe",
            "C:\\Chill Hub",
            "D:\\Игры вместе",
            "1.8.2",
            "0",
        ]
        .map(str::to_owned);
        let request = Request::parse(&args).unwrap();
        let command = engine_command(&request, false, false, true);
        let args: Vec<_> = command
            .get_args()
            .map(|s| s.to_string_lossy().into_owned())
            .collect();
        assert_eq!(
            args,
            [
                "/S",
                "/NODESKTOP",
                "/WEBVIEW2",
                "/GAMESDIR=\"D:\\Игры вместе\"",
                "/D=C:\\Chill Hub"
            ]
        );
        let mut rollback = request;
        rollback.allow_downgrade = true;
        assert!(
            engine_command(&rollback, true, false, false)
                .get_args()
                .any(|arg| arg == "/ALLOWDOWNGRADE")
        );
    }
    #[test]
    fn setup_rejects_root_relative_and_command_injection_paths() {
        for path in [
            "",
            "relative",
            "C:\\",
            "C:\\Games\\..\\Windows",
            "C:\\Games\" /S",
            "C:\\Games\n/D=C:\\Windows",
        ] {
            assert!(directory(path).is_err(), "{path}");
        }
        assert!(directory("C:\\Games\\Chill Hub").is_ok());
    }
    #[test]
    fn setup_keeps_games_outside_installation_and_rejects_foreign_files() {
        let root = tempfile::tempdir().unwrap();
        let install = root.path().join("launcher");
        let games = root.path().join("games");
        assert!(validate_paths(&install, &install.join("games")).is_err());
        assert!(validate_paths(&install, root.path()).is_err());
        assert!(validate_paths(&install, &games).is_ok());
        std::fs::create_dir(&install).unwrap();
        std::fs::write(install.join("personal.txt"), "keep").unwrap();
        assert!(validate_paths(&install, &games).is_err());
        assert_eq!(
            std::fs::read_to_string(install.join("personal.txt")).unwrap(),
            "keep"
        );
    }
    #[test]
    fn preview_never_runs_the_engine() {
        let args = [
            "preview-uninstall",
            "missing.exe",
            "C:\\ChillHub",
            "C:\\Games\\ChillHub",
            "1.8.1",
            "0",
        ]
        .map(str::to_owned);
        let request = Request::parse(&args).unwrap();
        assert!(
            execute(&request, false, true, true)
                .unwrap_err()
                .contains("предпросмотр")
        );
        assert!(Request::parse(&args[..5]).is_err());
    }
}
