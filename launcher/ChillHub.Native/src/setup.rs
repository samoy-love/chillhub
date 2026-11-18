//! Slint presentation for the existing NSIS engine. No shell command strings,
//! elevated process or second implementation of file/registry installation.
use crate::{
    helper_runtime::{self, HelperKind, HelperLog},
    ui::SetupWindow,
};
use slint::ComponentHandle;
use std::{
    path::{Component, Path, PathBuf},
    process::{Command, ExitStatus},
    sync::Arc,
};

mod cleanup;
pub use cleanup::run_uninstall_cleanup;

const INSTALL_ENV: &str = "CHILLHUB_NATIVE_SETUP_INSTALL";
const GAMES_ENV: &str = "CHILLHUB_NATIVE_SETUP_GAMES";

fn engine_paths() -> Result<(PathBuf, PathBuf), String> {
    Ok((
        std::env::var_os(INSTALL_ENV)
            .ok_or("Не удалось определить папку установки")?
            .into(),
        std::env::var_os(GAMES_ENV).unwrap_or_default().into(),
    ))
}

fn installed_release() -> Option<String> {
    use winreg::{RegKey, enums::HKEY_CURRENT_USER};
    RegKey::predef(HKEY_CURRENT_USER)
        .open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Uninstall\ChillHub")
        .and_then(|key| key.get_value::<String, _>("DisplayVersion"))
        .ok()
}

fn downgrade_from(version: &str) -> Result<Option<String>, String> {
    crate::update::version_parts(version)?;
    Ok(installed_release().filter(|installed| {
        crate::update::compare_release_versions(installed, version).is_ok_and(|order| order.is_gt())
    }))
}

pub fn installed_release_is_newer(version: &str) -> Result<bool, String> {
    downgrade_from(version).map(|downgrade| downgrade.is_some())
}

#[derive(Clone, Debug)]
struct Request {
    removing: bool,
    preview: bool,
    engine: PathBuf,
    install: PathBuf,
    games: PathBuf,
    version: String,
    allow_downgrade: bool,
}

impl Request {
    fn parse(args: &[String]) -> Result<Self, String> {
        Self::parse_with_engine_paths(args, engine_paths)
    }

    fn parse_with_engine_paths(
        args: &[String],
        engine_paths: impl FnOnce() -> Result<(PathBuf, PathBuf), String>,
    ) -> Result<Self, String> {
        if args.len() != 5
            || !matches!(
                args[0].as_str(),
                "install" | "uninstall" | "preview-install" | "preview-uninstall"
            )
        {
            return Err("Откройте установщик Chill Hub, чтобы начать установку.".into());
        }
        let removing = args[0].ends_with("uninstall");
        let preview = args[0].starts_with("preview-");
        let (install, games) = if !preview && args[2].is_empty() && args[3].is_empty() {
            engine_paths()?
        } else {
            (PathBuf::from(&args[2]), PathBuf::from(&args[3]))
        };
        let request = Self {
            removing,
            preview,
            engine: args[1].clone().into(),
            install,
            games,
            version: args[4].clone(),
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
    if path.is_file() {
        return Err("На месте выбранной папки уже есть файл. Выберите другую папку.".into());
    }
    let identity = PathBuf::from(
        crate::paths::canonical_path_with_missing(&path)
            .map_err(|e| e.to_string())?
            .to_string_lossy()
            .to_lowercase(),
    );
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
    let install_identity = PathBuf::from(identity(&install));
    let games_identity = PathBuf::from(identity(&games));
    if install_identity.starts_with(&games_identity)
        || games_identity.starts_with(&install_identity)
    {
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

fn engine_command(request: &Request, shortcut: bool, delete_games: bool) -> Command {
    let mut cmd = Command::new(&request.engine);
    cmd.arg("/S");
    if request.removing {
        if delete_games {
            cmd.arg("/DELETEGAMES");
            cmd.env(cleanup::EXPECTED_GAMES_ENV, &request.games);
        } else {
            cmd.env_remove(cleanup::EXPECTED_GAMES_ENV);
        }
    } else {
        if request.allow_downgrade {
            cmd.arg("/ALLOWDOWNGRADE");
        }
        if !shortcut {
            cmd.arg("/NODESKTOP");
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
    run_engine: fn(&mut Command) -> std::io::Result<ExitStatus>,
) -> Result<String, String> {
    if request.preview {
        return Err("Это предпросмотр: файлы и настройки не изменяются.".into());
    }
    let status = run_engine(&mut engine_command(request, shortcut, delete_games))
        .map_err(|e| format!("Не удалось начать операцию: {e}"))?;
    if !status.success() {
        return Err(format!(
            "Операция не завершена (код {}). Закройте Chill Hub и игры, проверьте свободное место и доступ к папке. Для более старой версии нужно отдельное подтверждение.",
            status.code().unwrap_or(-1)
        ));
    }
    verify_setup_result(request, delete_games)
}

fn verify_setup_result(request: &Request, delete_games: bool) -> Result<String, String> {
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
    create_window(
        request,
        log,
        crate::folder_picker::choose_folder,
        Command::status,
    )?
    .run()
    .map_err(|e| e.to_string())
}

fn create_window(
    request: Arc<Request>,
    log: Option<HelperLog>,
    choose_folder: fn(isize, &Path) -> Result<Option<PathBuf>, String>,
    run_engine: fn(&mut Command) -> std::io::Result<ExitStatus>,
) -> Result<SetupWindow, String> {
    let ui = SetupWindow::new().map_err(|e| e.to_string())?;
    helper_runtime::style_window(ui.window());
    ui.set_removing(request.removing);
    ui.set_version(request.version.clone().into());
    ui.set_install_path(request.install.to_string_lossy().as_ref().into());
    ui.set_games_path(request.games.to_string_lossy().as_ref().into());
    if !request.removing
        && !request.preview
        && let Some(installed) = downgrade_from(&request.version)?
    {
        ui.set_downgrade_from(installed.into());
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
            let result = helper_runtime::caught(|| choose_folder(owner, &initial));
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
        if ui.get_busy() {
            return;
        }
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
        let delete_games = ui.get_delete_games();
        let paths = submit_paths(
            request.removing,
            delete_games,
            ui.get_install_path().as_str(),
            ui.get_games_path().as_str(),
        );
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
        ui.set_busy(true);
        ui.set_error(false);
        ui.set_message("Подготавливаем файлы. Это может занять несколько минут.".into());
        let weak = ui.as_weak();
        let log = log.clone();
        std::thread::spawn(move || {
            let result =
                helper_runtime::caught(|| execute(&request, shortcut, delete_games, run_engine));
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
    Ok(ui)
}

fn submit_paths(
    removing: bool,
    delete_games: bool,
    install: &str,
    games: &str,
) -> Result<(PathBuf, PathBuf), String> {
    let install = directory(install)?;
    if removing && !delete_games {
        return Ok((install, PathBuf::from(games)));
    }
    let games = directory(games)?;
    if !removing {
        validate_paths(&install, &games)?;
    }
    Ok((install, games))
}

#[cfg(test)]
#[path = "setup_tests.rs"]
mod tests;
