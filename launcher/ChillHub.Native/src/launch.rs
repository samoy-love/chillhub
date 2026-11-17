mod doorstop;
mod loader_session;
mod steam_handoff;
pub use doorstop::{doorstop_config_valid, set_doorstop_enabled};

use crate::{
    config::{ConfigStore, LaunchTarget},
    diagnostics,
    model::{GameRecord, ModsRecord},
    paths::{AppPaths, atomic_write, contained_path, relative_path, validate_game_id},
    playtime::{PlaytimeStore, ProcessIdentity, SessionHandle, processes_in},
};
use loader_session::LaunchLocks;
use serde::{Deserialize, Serialize};
use std::{
    collections::HashSet,
    fs,
    path::{Path, PathBuf},
    process::Command,
    sync::mpsc::{self, Receiver},
    time::{Duration, Instant},
};
use steam_handoff::{SteamWait, find_game_process, wait_for_game};

#[derive(Clone, Debug, Eq, PartialEq)]
pub enum SteamLookup {
    Found,
    SteamNotInstalled,
    NoLibraries,
    GameNotInstalled,
    FolderMissing,
    NoAppId,
}
#[derive(Clone, Debug)]
pub struct SteamGame {
    pub outcome: SteamLookup,
    pub game_dir: PathBuf,
    pub steam_exe: PathBuf,
    pub trace: Vec<String>,
}
impl SteamGame {
    pub fn available(&self) -> bool {
        self.outcome == SteamLookup::Found && self.steam_exe.is_file()
    }
    pub fn reason(&self) -> &'static str {
        match self.outcome {
            SteamLookup::Found => {
                if self.steam_exe.is_file() {
                    ""
                } else {
                    "Steam не найден"
                }
            }
            SteamLookup::SteamNotInstalled => "Steam не установлен",
            SteamLookup::NoLibraries => "Не найдены библиотеки Steam",
            SteamLookup::GameNotInstalled => "Игра не установлена в Steam",
            SteamLookup::FolderMissing => "Папка игры Steam не найдена",
            SteamLookup::NoAppId => "Для игры не задан Steam AppID",
        }
    }
}

pub fn locate_steam(app_id: &str, steam_folder: &str) -> SteamGame {
    if app_id.is_empty() || !app_id.bytes().all(|byte| byte.is_ascii_digit()) {
        return locate_steam_at(None, app_id, steam_folder);
    }
    let root = steam_install_dir();
    locate_steam_at(root.as_deref(), app_id, steam_folder)
}

#[cfg(windows)]
fn steam_install_dir() -> Option<PathBuf> {
    use winreg::{
        RegKey,
        enums::{HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE},
    };
    let mut library_root = None;
    for (hive, key, value) in [
        (
            HKEY_LOCAL_MACHINE,
            r"SOFTWARE\WOW6432Node\Valve\Steam",
            "InstallPath",
        ),
        (HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam", "InstallPath"),
        (HKEY_CURRENT_USER, r"SOFTWARE\Valve\Steam", "SteamPath"),
    ] {
        if let Ok(key) = RegKey::predef(hive).open_subkey(key)
            && let Ok(path) = key.get_value::<String, _>(value)
        {
            let p = PathBuf::from(path.trim().replace('/', "\\"));
            if p.join("steam.exe").is_file() {
                return Some(p);
            }
            if p.join("steamapps").is_dir() {
                library_root.get_or_insert(p);
            }
        }
    }
    library_root
}
#[cfg(not(windows))]
fn steam_install_dir() -> Option<PathBuf> {
    None
}

pub fn locate_steam_at(root: Option<&Path>, app_id: &str, steam_folder: &str) -> SteamGame {
    let mut result = SteamGame {
        outcome: SteamLookup::SteamNotInstalled,
        game_dir: PathBuf::new(),
        steam_exe: PathBuf::new(),
        trace: Vec::new(),
    };
    if app_id.is_empty() || !app_id.bytes().all(|b| b.is_ascii_digit()) {
        result.outcome = SteamLookup::NoAppId;
        return result;
    }
    let Some(root) = root else { return result };
    result.steam_exe = root.join("steam.exe");
    let own = root.join("steamapps");
    let mut libraries = Vec::new();
    if own.is_dir() {
        libraries.push(own.clone());
    }
    if let Ok(vdf) = read_vdf(&own.join("libraryfolders.vdf")) {
        let folders = child(&vdf, "libraryfolders").unwrap_or(&vdf);
        for (key, value) in folders {
            if key.parse::<u32>().is_err() {
                continue;
            }
            let path = match value {
                VdfValue::Text(s) => Some(s.as_str()),
                VdfValue::Object(items) => text_value(items, "path"),
            };
            if let Some(path) = path {
                let apps = PathBuf::from(path).join("steamapps");
                if apps.is_dir() && !libraries.iter().any(|p: &PathBuf| path_equal(p, &apps)) {
                    libraries.push(apps);
                }
            }
        }
    }
    if libraries.is_empty() {
        result.outcome = SteamLookup::NoLibraries;
        return result;
    }
    result.outcome = SteamLookup::GameNotInstalled;
    for apps in libraries {
        let manifest = apps.join(format!("appmanifest_{app_id}.acf"));
        if !manifest.is_file() {
            continue;
        }
        result
            .trace
            .push(format!("Манифест: {}", manifest.display()));
        let Ok(data) = read_vdf(&manifest) else {
            continue;
        };
        let Some(install_dir) = child(&data, "AppState").and_then(|o| text_value(o, "installdir"))
        else {
            continue;
        };
        let Ok(relative) = relative_path(install_dir) else {
            continue;
        };
        let common = apps.join("common");
        let Ok(direct) = contained_path(&common, &relative) else {
            continue;
        };
        let folder = steam_folder.replace('\\', "/");
        let install_normal = install_dir.replace('\\', "/");
        let prefix = format!("{install_normal}/").to_lowercase();
        let nested = if folder.to_lowercase().starts_with(&prefix) {
            relative_path(&folder)
                .ok()
                .and_then(|r| contained_path(&common, &r).ok())
                .filter(|p| p.is_dir())
        } else {
            None
        };
        if let Some(dir) = nested.or_else(|| direct.is_dir().then_some(direct)) {
            result.outcome = SteamLookup::Found;
            result.game_dir = dir;
            return result;
        }
        result.outcome = SteamLookup::FolderMissing;
    }
    result
}

#[derive(Clone, Debug, Eq, PartialEq, Serialize, Deserialize)]
pub enum LaunchAction {
    Play,
    InstallGame,
    ResumeInstall,
    UpdateGame,
    RepairGame,
    InstallMods,
    UpdateMods,
    RepairMods,
    Unavailable,
}
#[derive(Clone, Debug)]
pub struct LaunchOption {
    pub target: LaunchTarget,
    pub action: LaunchAction,
    pub game_dir: PathBuf,
    pub note: String,
}
impl LaunchOption {
    pub fn available(&self) -> bool {
        self.action != LaunchAction::Unavailable
    }
    pub fn ready(&self) -> bool {
        self.action == LaunchAction::Play
    }
}

pub fn options(game: &GameRecord, paths: &AppPaths) -> Result<Vec<LaunchOption>, String> {
    let steam = game
        .mods
        .as_ref()
        .map(|m| locate_steam(&m.steam_app_id, &m.steam_folder));
    options_with_steam(game, paths, steam.as_ref())
}

pub fn options_with_steam(
    game: &GameRecord,
    paths: &AppPaths,
    steam: Option<&SteamGame>,
) -> Result<Vec<LaunchOption>, String> {
    let local = paths.game_root(&game.game_id)?;
    let mut result = Vec::new();
    let preferences = ConfigStore::new(paths.clone()).load_readonly()?;
    let local_version = preferences
        .selected_version(&game.game_id)
        .unwrap_or(&game.latest_version);
    let has_build = !local_version.trim().is_empty();
    let add = |result: &mut Vec<LaunchOption>, target, root: &Path, action, note: &str| {
        result.push(LaunchOption {
            target,
            action,
            game_dir: root.into(),
            note: note.into(),
        })
    };
    if let Some(mods) = &game.mods {
        let missing = SteamGame {
            outcome: SteamLookup::NoAppId,
            game_dir: PathBuf::new(),
            steam_exe: PathBuf::new(),
            trace: Vec::new(),
        };
        let steam = steam.unwrap_or(&missing);
        for target in [LaunchTarget::SteamModded, LaunchTarget::SteamVanilla] {
            let (action, note) = if !steam.available() {
                (LaunchAction::Unavailable, steam.reason())
            } else if steam.game_dir.join(".updating").exists() {
                if target.modded() && mods.has_latest {
                    (
                        if read_marker(&steam.game_dir, ".mods.version").is_empty()
                            && !steam.game_dir.join(".mods.manifest.json").is_file()
                        {
                            LaunchAction::InstallMods
                        } else {
                            LaunchAction::RepairMods
                        },
                        "Установка модов не завершена. Восстановить моды",
                    )
                } else {
                    (
                        LaunchAction::Unavailable,
                        "Установка не завершена. Восстановите моды в режиме «С модами»",
                    )
                }
            } else if resolve_game_exe(&steam.game_dir, &game.exe_relative_path, &mods.exe_names)
                .is_err()
            {
                (
                    LaunchAction::Unavailable,
                    "Файл запуска игры не найден. Проверьте игру в Steam",
                )
            } else if target.modded() {
                mod_action(&steam.game_dir, mods)
            } else {
                (LaunchAction::Play, "")
            };
            add(&mut result, target, &steam.game_dir, action, note);
        }
    }
    if has_build {
        let targets: &[LaunchTarget] = if game.mods.is_some() {
            &[LaunchTarget::LocalModded, LaunchTarget::LocalVanilla]
        } else {
            &[LaunchTarget::LocalVanilla]
        };
        let exe_exists = resolve_game_exe(&local, &game.exe_relative_path, &[]).is_ok();
        let version_marker = read_marker(&local, ".version");
        let installed = !version_marker.is_empty();
        let outdated = !version_marker.eq_ignore_ascii_case(local_version.trim());
        let interrupted = local.join(".updating").exists();
        for target in targets {
            let (action, note) = if interrupted {
                if !installed {
                    (LaunchAction::ResumeInstall, "Установка прервана")
                } else if outdated {
                    (LaunchAction::UpdateGame, "Обновление не завершено")
                } else {
                    (LaunchAction::RepairGame, "Восстановить файлы игры")
                }
            } else if !installed && !exe_exists {
                (LaunchAction::InstallGame, "Установить игру")
            } else if outdated {
                (LaunchAction::UpdateGame, "Обновить игру")
            } else if !exe_exists || crate::integrity::needs_repair(&local, local_version) {
                (LaunchAction::RepairGame, "Восстановить файлы игры")
            } else if target.modded() {
                let (action, note) = mod_action(&local, game.mods.as_ref().unwrap());
                match action {
                    LaunchAction::InstallMods
                    | LaunchAction::UpdateMods
                    | LaunchAction::RepairMods => (LaunchAction::UpdateGame, note),
                    other => (other, note),
                }
            } else {
                (LaunchAction::Play, "")
            };
            add(&mut result, *target, &local, action, note);
        }
    }
    Ok(result)
}

fn mod_action(root: &Path, mods: &ModsRecord) -> (LaunchAction, &'static str) {
    if !mods.has_latest {
        return (LaunchAction::Unavailable, "Модпак ещё не опубликован");
    }
    let version = read_marker(root, ".mods.version");
    if version.is_empty() {
        return (LaunchAction::InstallMods, "Установить моды");
    }
    if !version.eq_ignore_ascii_case(mods.version.trim())
        || (!mods.revision.trim().is_empty()
            && !read_marker(root, ".mods.revision").eq_ignore_ascii_case(mods.revision.trim()))
    {
        return (LaunchAction::UpdateMods, "Обновить моды");
    }
    if !root.join("winhttp.dll").is_file() || !doorstop_config_valid(root) || modpack_broken(root) {
        return (LaunchAction::RepairMods, "Восстановить моды");
    }
    (LaunchAction::Play, "")
}

pub fn read_marker(root: &Path, name: &str) -> String {
    let path = root.join(name);
    if fs::metadata(&path).is_ok_and(|m| m.len() <= 16 * 1024) {
        fs::read_to_string(path)
            .unwrap_or_default()
            .trim_start_matches('\u{feff}')
            .trim()
            .into()
    } else {
        String::new()
    }
}

pub fn installed_modpack_paths(root: &Path) -> Result<Vec<PathBuf>, String> {
    let path = root.join(".mods.manifest.json");
    if !path.exists() {
        return Ok(Vec::new());
    }
    let data = read_limited(&path, 16 * 1024 * 1024)?;
    let manifest: serde_json::Value = serde_json::from_slice(&data).map_err(|e| e.to_string())?;
    manifest_paths(&manifest)
}

fn manifest_paths(manifest: &serde_json::Value) -> Result<Vec<PathBuf>, String> {
    let files = manifest
        .get("files")
        .or_else(|| manifest.get("Files"))
        .and_then(|v| v.as_array())
        .ok_or("В манифесте модов нет файлов")?;
    files
        .iter()
        .map(|f| {
            let p = f
                .get("path")
                .or_else(|| f.get("Path"))
                .and_then(|v| v.as_str())
                .ok_or("В манифесте модов нет пути")?;
            relative_path(p)
        })
        .collect()
}

fn modpack_broken(root: &Path) -> bool {
    let Ok(paths) = installed_modpack_paths(root) else {
        return true;
    };
    let mut paths = paths
        .into_iter()
        .filter(|path| {
            !path
                .as_os_str()
                .eq_ignore_ascii_case(std::ffi::OsStr::new("doorstop_config.ini"))
        })
        .peekable();
    if paths.peek().is_none() {
        return false;
    }
    let Ok(canonical_root) = fs::canonicalize(root) else {
        return true;
    };
    let mut checked_dirs = HashSet::new();
    for relative in paths {
        let mut current = root.to_path_buf();
        if let Some(parent) = relative.parent() {
            for component in parent.components() {
                current.push(component.as_os_str());
                if !modpack_parent_is_contained(&current, &canonical_root, &mut checked_dirs) {
                    return true;
                }
            }
        }
        // Check the leaf on every probe, including links and files removed after install.
        current.push(relative.file_name().expect("validated relative path"));
        if !fs::canonicalize(current)
            .is_ok_and(|file| file.starts_with(&canonical_root) && file.is_file())
        {
            return true;
        }
    }
    false
}

fn modpack_parent_is_contained(
    path: &Path,
    canonical_root: &Path,
    checked: &mut HashSet<PathBuf>,
) -> bool {
    let Ok(metadata) = fs::symlink_metadata(path) else {
        return false;
    };
    let ordinary = metadata.is_dir() && !crate::manifest::is_reparse(&metadata);
    if ordinary && checked.contains(path) {
        return true;
    }
    let contained = fs::canonicalize(path)
        .is_ok_and(|parent| parent.starts_with(canonical_root) && parent.is_dir());
    // Recheck metadata before reusing a directory. Reparse points always need
    // to be resolved, and this set never survives the current readiness probe.
    if contained && ordinary {
        checked.insert(path.to_path_buf());
    }
    contained
}

/// Call only after the entire modpack has passed verification and been committed by transfer.
pub fn record_modpack_install(
    root: &Path,
    mods: &ModsRecord,
    manifest: &serde_json::Value,
) -> Result<(), String> {
    for relative in manifest_paths(manifest)? {
        if !contained_path(root, &relative)?.is_file() {
            return Err(format!("Не установлен файл {}", relative.display()));
        }
    }
    if !processes_in(root)?.is_empty() {
        return Err("Закройте игру перед изменением модов".into());
    }
    set_doorstop_enabled(root, false)?;
    atomic_write(
        &root.join(".mods.manifest.json"),
        &serde_json::to_vec_pretty(manifest).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;
    atomic_write(
        &root.join(".mods.revision"),
        mods.revision.trim().as_bytes(),
    )
    .map_err(|e| e.to_string())?;
    atomic_write(&root.join(".mods.version"), mods.version.trim().as_bytes())
        .map_err(|e| e.to_string())
}

pub fn resolve_game_exe(
    root: &Path,
    relative: &str,
    preferred_names: &[String],
) -> Result<PathBuf, String> {
    if !relative.trim().is_empty() {
        let rel = relative_path(relative)?;
        let path = contained_path(root, &rel)?;
        if is_exe(&path) && path.is_file() {
            return Ok(path);
        }
    }
    for name in preferred_names {
        let path = contained_path(root, &relative_path(name)?)?;
        if is_exe(&path) && path.is_file() {
            return Ok(path);
        }
    }
    let candidates: Vec<PathBuf> = fs::read_dir(root)
        .map_err(|_| "Папка игры не найдена".to_string())?
        .filter_map(Result::ok)
        .map(|e| e.path())
        .filter(|p| {
            p.is_file()
                && is_exe(p)
                && !p
                    .file_name()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .to_ascii_lowercase()
                    .starts_with("unitycrashhandler")
        })
        .collect();
    if candidates.len() == 1 {
        let rel = candidates[0]
            .strip_prefix(root)
            .map_err(|e| e.to_string())?;
        return contained_path(root, rel);
    }
    Err("Не найден однозначный файл запуска игры".into())
}
fn is_exe(path: &Path) -> bool {
    path.extension()
        .is_some_and(|e| e.eq_ignore_ascii_case("exe"))
}

#[derive(Clone, Debug)]
pub struct LaunchPlan {
    pub game_id: String,
    pub target: LaunchTarget,
    pub executable: PathBuf,
    pub arguments: Vec<String>,
    pub working_directory: PathBuf,
    pub game_directory: PathBuf,
    pub game_executable: PathBuf,
}

#[derive(Debug)]
pub enum LaunchOutcome {
    Started(SessionHandle),
    SteamPending {
        warning: &'static str,
        late_session: Receiver<SessionHandle>,
    },
}

#[derive(Clone)]
pub struct LaunchService {
    pub paths: AppPaths,
    pub config: ConfigStore,
    pub playtime: PlaytimeStore,
}
impl LaunchService {
    pub fn new(paths: AppPaths) -> Self {
        Self {
            config: ConfigStore::new(paths.clone()),
            playtime: PlaytimeStore::new(&paths),
            paths,
        }
    }
    pub fn plan(&self, game: &GameRecord, target: LaunchTarget) -> Result<LaunchPlan, String> {
        let steam = game
            .mods
            .as_ref()
            .map(|m| locate_steam(&m.steam_app_id, &m.steam_folder));
        self.plan_with_steam(game, target, steam.as_ref())
    }
    pub fn plan_with_steam(
        &self,
        game: &GameRecord,
        target: LaunchTarget,
        steam: Option<&SteamGame>,
    ) -> Result<LaunchPlan, String> {
        let option = options_with_steam(game, &self.paths, steam)?
            .into_iter()
            .find(|o| o.target == target)
            .ok_or("Этот вариант запуска недоступен")?;
        if !option.ready() {
            return Err(option.note);
        }
        if option.game_dir.join(".updating").exists() {
            return Err("Обновление не завершено. Восстановите игру".into());
        }
        let names = game
            .mods
            .as_ref()
            .map(|m| m.exe_names.as_slice())
            .unwrap_or(&[]);
        let game_executable = resolve_game_exe(&option.game_dir, &game.exe_relative_path, names)?;
        let (executable, arguments) = if target.via_steam() {
            let steam = steam.ok_or("Steam не найден")?;
            let id = &game
                .mods
                .as_ref()
                .ok_or("Для игры не настроен Steam")?
                .steam_app_id;
            if !steam.available() || id.is_empty() || !id.bytes().all(|c| c.is_ascii_digit()) {
                return Err("Steam-копия недоступна".into());
            }
            (
                steam.steam_exe.clone(),
                vec!["-applaunch".into(), id.clone()],
            )
        } else {
            (game_executable.clone(), Vec::new())
        };
        let working_directory = executable
            .parent()
            .ok_or("Не найдена папка запуска")?
            .to_path_buf();
        Ok(LaunchPlan {
            game_id: game.game_id.clone(),
            target,
            executable,
            arguments,
            working_directory,
            game_directory: option.game_dir,
            game_executable,
        })
    }

    pub fn recover_loader_sessions(&self) -> Result<(), String> {
        loader_session::recover_loader_sessions(&self.paths.data_dir)
    }

    /// Blocking: invoke on a worker. A slow Steam handoff continues in the background.
    pub fn launch(&self, game: &GameRecord, target: LaunchTarget) -> Result<LaunchOutcome, String> {
        let plan = self.plan(game, target)?;
        self.execute_plan(plan, SteamWait::default())
    }
    pub fn launch_shortcut(&self, request: &ShortcutRequest) -> Result<SessionHandle, String> {
        let executable = request.allowed_fallback(&self.paths)?;
        let directory = self.paths.game_root(&request.game_id)?;
        let target = request.target.unwrap_or(LaunchTarget::LocalVanilla);
        if target.via_steam() {
            return Err("Для запуска через Steam нужен сохранённый каталог игры".into());
        }
        if target.modded()
            && (read_marker(&directory, ".mods.version").is_empty() || modpack_broken(&directory))
        {
            return Err(
                "Модпак не установлен или повреждён. Обновите библиотеку перед запуском".into(),
            );
        }
        let working_directory = executable
            .parent()
            .ok_or("Не найдена папка запуска игры")?
            .to_path_buf();
        let plan = LaunchPlan {
            game_id: request.game_id.clone(),
            target,
            executable: executable.clone(),
            arguments: vec![],
            working_directory,
            game_directory: directory,
            game_executable: executable,
        };
        match self.execute_plan(plan, SteamWait::default())? {
            LaunchOutcome::Started(session) => Ok(session),
            LaunchOutcome::SteamPending { .. } => unreachable!("Steam shortcuts are checked above"),
        }
    }

    fn execute_plan(
        &self,
        plan: LaunchPlan,
        steam_wait: SteamWait,
    ) -> Result<LaunchOutcome, String> {
        let target = plan.target;
        let locks =
            LaunchLocks::acquire(&self.paths.data_dir, &plan.game_directory, !target.modded())?;
        if plan.game_directory.join(".updating").exists() {
            return Err("Обновление не завершено. Восстановите игру".into());
        }
        if !processes_in(&plan.game_directory)?.is_empty() {
            return Err("Игра уже запущена. Закройте её перед сменой режима".into());
        }
        let accounting_error = self.playtime.reconcile_for_launch(target)?;
        let steam_start = Instant::now();
        let wait_until = target
            .via_steam()
            .then(|| chrono::Utc::now() + (steam_wait.report_after + steam_wait.watch_after));
        locks.prepare_loader(target.modded(), wait_until)?;
        let mut child = match Command::new(&plan.executable)
            .args(&plan.arguments)
            .current_dir(&plan.working_directory)
            .spawn()
        {
            Ok(child) => child,
            Err(error) => {
                self.restore_session_loader(&locks, target);
                return Err(format!("Не удалось запустить игру: {error}"));
            }
        };
        if !target.via_steam() {
            let process = match ProcessIdentity::capture(child.id()) {
                Ok(process) => process,
                Err(error) => {
                    let service = self.clone();
                    std::thread::spawn(move || {
                        if child.wait().is_ok() {
                            service.restore_session_loader(&locks, target);
                        }
                    });
                    return Err(format!("Не удалось определить процесс игры: {error}"));
                }
            };
            let session = self.track_session(&plan, locks, process, accounting_error);
            self.remember_choice(&plan);
            return Ok(LaunchOutcome::Started(session));
        }

        drop(child);
        let report_at = steam_start + steam_wait.report_after;
        let watch_until = report_at + steam_wait.watch_after;
        if let Some(process) = self.wait_for_steam(&plan, report_at, steam_wait.interval) {
            let session = self.track_session(&plan, locks, process, accounting_error);
            self.remember_choice(&plan);
            return Ok(LaunchOutcome::Started(session));
        }

        let service = self.clone();
        let pending_plan = plan.clone();
        let warning = "Команда отправлена в Steam. Проверьте, что происходит в окне Steam";
        diagnostics::log(&self.paths.data_dir, "steam-launch-pending", warning);
        let (late_started, late_session) = mpsc::channel();
        std::thread::spawn(move || {
            if let Some(process) =
                service.wait_for_steam(&pending_plan, watch_until, steam_wait.interval)
            {
                let session =
                    service.track_session(&pending_plan, locks, process, accounting_error);
                if let Some(warning) = &session.warning {
                    diagnostics::log(&service.paths.data_dir, "playtime", warning);
                }
                let _ = late_started.send(session);
            }
            // Without a game process we cannot tell when Steam will start it.
            // Retain the selected loader state and its recovery record.
        });
        self.remember_choice(&plan);
        Ok(LaunchOutcome::SteamPending {
            warning,
            late_session,
        })
    }

    fn wait_for_steam(
        &self,
        plan: &LaunchPlan,
        deadline: Instant,
        interval: Duration,
    ) -> Option<ProcessIdentity> {
        wait_for_game(deadline, interval, || {
            match find_game_process(&plan.game_directory, &plan.game_executable) {
                Ok(process) => process,
                Err(error) => {
                    diagnostics::log(&self.paths.data_dir, "steam-process-lookup", &error);
                    None
                }
            }
        })
    }

    fn remember_choice(&self, plan: &LaunchPlan) {
        if let Err(error) = self.config.update(|config| {
            config.remember(&plan.game_id, plan.target);
            config.last_game_id.clone_from(&plan.game_id);
        }) {
            diagnostics::log(&self.paths.data_dir, "launch-choice", &error);
        }
    }

    fn track_session(
        &self,
        plan: &LaunchPlan,
        locks: LaunchLocks,
        process: ProcessIdentity,
        accounting_error: Option<String>,
    ) -> SessionHandle {
        // The loader journal is independent of playtime. Legacy sessions with
        // ModdedDir are still recovered by PlaytimeStore.
        let target = plan.target;
        let warning = |error| format!("Игра запущена, но время игры не удалось сохранить: {error}");
        let session = match self
            .playtime
            .begin(&plan.game_id, target, process.clone(), None)
        {
            Ok(mut session) => {
                session.warning = accounting_error.map(warning);
                session
            }
            Err(error) => SessionHandle {
                game_id: plan.game_id.clone(),
                target,
                process: process.clone(),
                warning: Some(warning(error)),
            },
        };
        let service = self.clone();
        std::thread::spawn(move || {
            while process.is_running() {
                std::thread::sleep(Duration::from_secs(1));
            }
            service.restore_session_loader(&locks, target);
        });
        session
    }

    fn restore_session_loader(&self, locks: &LaunchLocks, target: LaunchTarget) {
        if target.modded()
            && let Err(error) = locks.restore_loader()
        {
            diagnostics::log(&self.paths.data_dir, "loader-recovery", &error);
        }
    }
}

#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct ShortcutRequest {
    pub game_id: String,
    pub title: String,
    pub exe_path: Option<PathBuf>,
    pub target: Option<LaunchTarget>,
}
impl ShortcutRequest {
    pub fn parse_args(args: &[String]) -> Result<Option<Self>, String> {
        let mut result = Self::default();
        let mut index = 0;
        while index < args.len() {
            let arg = &args[index];
            if arg
                .get(..9)
                .is_some_and(|prefix| prefix.eq_ignore_ascii_case("chillhub:"))
            {
                return Self::parse_uri(arg).map(Some);
            }
            let (key, inline) = arg
                .split_once('=')
                .map_or((arg.as_str(), None), |(key, value)| (key, Some(value)));
            let key = key.to_ascii_lowercase();
            if !matches!(key.as_str(), "--game" | "--title" | "--exe" | "--target") {
                index += 1;
                continue;
            }
            let value = if let Some(value) = inline {
                Some(value)
            } else {
                let next = args.get(index + 1).filter(|value| !value.starts_with("--"));
                if next.is_some() {
                    index += 1;
                }
                next.map(String::as_str)
            };
            let Some(value) = value else {
                index += 1;
                continue;
            };
            let value = value.trim().trim_matches('"');
            match key.as_str() {
                "--game" => result.game_id = value.into(),
                "--title" => result.title = value.into(),
                "--exe" => result.exe_path = Some(value.into()),
                "--target" => {
                    result.target =
                        Some(LaunchTarget::parse(value).ok_or("Неизвестный вариант запуска")?)
                }
                _ => {}
            }
            index += 1;
        }
        if result.game_id.is_empty() {
            return Ok(None);
        }
        validate_game_id(&result.game_id)?;
        Ok(Some(result))
    }
    pub fn parse_uri(raw: &str) -> Result<Self, String> {
        let uri = url::Url::parse(raw).map_err(|e| e.to_string())?;
        if uri.scheme() != "chillhub"
            || uri.host_str() != Some("game")
            || !uri.username().is_empty()
            || uri.password().is_some()
            || uri.port().is_some()
            || uri.fragment().is_some()
        {
            return Err("Неизвестная ссылка Chill Hub".into());
        }
        let mut result = Self {
            game_id: uri.path().trim_matches('/').into(),
            ..Default::default()
        };
        validate_game_id(&result.game_id)?;
        for (key, value) in uri.query_pairs() {
            if key == "target" {
                result.target =
                    Some(LaunchTarget::parse(&value).ok_or("Неизвестный вариант запуска")?);
            } else {
                return Err("Неподдерживаемый параметр ссылки".into());
            }
        }
        // URI links deliberately never carry an arbitrary executable path.
        Ok(result)
    }
    pub fn allowed_fallback(&self, paths: &AppPaths) -> Result<PathBuf, String> {
        validate_game_id(&self.game_id)?;
        let path = self.exe_path.as_ref().ok_or("В ярлыке нет файла игры")?;
        let root = fs::canonicalize(paths.game_root(&self.game_id)?).map_err(|e| e.to_string())?;
        let exe = fs::canonicalize(path).map_err(|e| e.to_string())?;
        if !exe.starts_with(&root)
            || !is_exe(&exe)
            || !exe.is_file()
            || root.join(".updating").exists()
        {
            return Err("Файл ярлыка находится вне папки игры или обновление не завершено".into());
        }
        Ok(exe)
    }
    pub fn arguments(&self) -> Vec<String> {
        let mut args = vec!["--game".into(), self.game_id.clone()];
        if !self.title.is_empty() {
            args.extend(["--title".into(), self.title.clone()]);
        }
        if let Some(path) = &self.exe_path {
            args.extend(["--exe".into(), path.to_string_lossy().into_owned()]);
        }
        if let Some(target) = self.target {
            args.extend(["--target".into(), target.as_str().into()]);
        }
        args
    }
}

/// Windows .lnk arguments use the CommandLineToArgvW escaping convention.
pub fn quote_windows_argument(value: &str) -> String {
    let mut out = String::from("\"");
    let mut slashes = 0;
    for c in value.chars() {
        if c == '\\' {
            slashes += 1;
            continue;
        }
        if c == '"' {
            out.push_str(&"\\".repeat(slashes * 2 + 1));
            out.push('"');
        } else {
            out.push_str(&"\\".repeat(slashes));
            out.push(c);
        }
        slashes = 0;
    }
    out.push_str(&"\\".repeat(slashes * 2));
    out.push('"');
    out
}

pub fn create_game_shortcut(
    launcher: &Path,
    output: &Path,
    request: &ShortcutRequest,
) -> Result<(), String> {
    validate_game_id(&request.game_id)?;
    if !launcher.is_file()
        || !is_exe(launcher)
        || !output
            .extension()
            .is_some_and(|v| v.eq_ignore_ascii_case("lnk"))
    {
        return Err("Неверный путь ярлыка или лаунчера".into());
    }
    let args = request
        .arguments()
        .iter()
        .map(|v| quote_windows_argument(v))
        .collect::<Vec<_>>()
        .join(" ");
    #[cfg(windows)]
    {
        let working_directory = launcher.parent().ok_or("Не найдена папка лаунчера")?;
        crate::shortcuts::native::write(
            output,
            launcher,
            &args,
            working_directory,
            &request.title,
            launcher,
        )
    }
    #[cfg(not(windows))]
    {
        let _ = args;
        Err("Ярлыки поддерживаются в Windows".into())
    }
}

fn path_equal(a: &Path, b: &Path) -> bool {
    let a = fs::canonicalize(a).unwrap_or_else(|_| a.into());
    let b = fs::canonicalize(b).unwrap_or_else(|_| b.into());
    a.to_string_lossy()
        .eq_ignore_ascii_case(&b.to_string_lossy())
}
fn read_limited(path: &Path, max: u64) -> Result<Vec<u8>, String> {
    use std::io::Read;
    let file = fs::File::open(path).map_err(|e| e.to_string())?;
    let size = file.metadata().map_err(|e| e.to_string())?.len();
    if size > max {
        return Err("Файл слишком большой".into());
    }
    let mut data = Vec::new();
    file.take(max + 1)
        .read_to_end(&mut data)
        .map_err(|e| e.to_string())?;
    if data.len() as u64 > max {
        return Err("Файл слишком большой".into());
    }
    Ok(data)
}

#[derive(Clone, Debug)]
enum VdfValue {
    Text(String),
    Object(Vec<(String, VdfValue)>),
}
fn child<'a>(items: &'a [(String, VdfValue)], key: &str) -> Option<&'a Vec<(String, VdfValue)>> {
    items.iter().find_map(|(k, v)| {
        if k.eq_ignore_ascii_case(key) {
            if let VdfValue::Object(o) = v {
                Some(o)
            } else {
                None
            }
        } else {
            None
        }
    })
}
fn text_value<'a>(items: &'a [(String, VdfValue)], key: &str) -> Option<&'a str> {
    items.iter().find_map(|(k, v)| {
        if k.eq_ignore_ascii_case(key) {
            if let VdfValue::Text(s) = v {
                Some(s.as_str())
            } else {
                None
            }
        } else {
            None
        }
    })
}
fn read_vdf(path: &Path) -> Result<Vec<(String, VdfValue)>, String> {
    let bytes = read_limited(path, 2 * 1024 * 1024)?;
    parse_vdf(&String::from_utf8(bytes).map_err(|e| e.to_string())?)
}
fn parse_vdf(text: &str) -> Result<Vec<(String, VdfValue)>, String> {
    let mut tokens = Vec::new();
    let mut it = text.trim_start_matches('\u{feff}').chars().peekable();
    while let Some(c) = it.next() {
        if c.is_whitespace() {
            continue;
        }
        if c == '/' && it.peek() == Some(&'/') {
            for c in it.by_ref() {
                if c == '\n' {
                    break;
                }
            }
            continue;
        }
        if matches!(c, '{' | '}') {
            tokens.push(c.to_string());
            continue;
        }
        if c == '"' {
            let mut value = String::new();
            let mut closed = false;
            while let Some(c) = it.next() {
                if c == '"' {
                    closed = true;
                    break;
                }
                if c == '\\' && matches!(it.peek(), Some('\\' | '"')) {
                    value.push(it.next().unwrap());
                } else {
                    value.push(c);
                }
            }
            if !closed {
                return Err("Обрезанный файл Steam".into());
            }
            tokens.push(value);
        } else {
            let mut value = String::from(c);
            while it
                .peek()
                .is_some_and(|c| !c.is_whitespace() && !matches!(c, '{' | '}'))
            {
                value.push(it.next().unwrap());
            }
            tokens.push(value);
        }
    }
    fn object(
        tokens: &[String],
        cursor: &mut usize,
        depth: usize,
    ) -> Result<Vec<(String, VdfValue)>, String> {
        if depth > 32 {
            return Err("Слишком сложный файл Steam".into());
        }
        let mut result = Vec::new();
        while *cursor < tokens.len() {
            let key = &tokens[*cursor];
            *cursor += 1;
            if key == "}" {
                return if depth > 0 {
                    Ok(result)
                } else {
                    Err("Некорректный файл Steam".into())
                };
            }
            if key == "{" || *cursor >= tokens.len() {
                return Err("Обрезанный файл Steam".into());
            }
            let value = &tokens[*cursor];
            *cursor += 1;
            let value = if value == "{" {
                VdfValue::Object(object(tokens, cursor, depth + 1)?)
            } else if value == "}" {
                return Err("Некорректный файл Steam".into());
            } else {
                VdfValue::Text(value.clone())
            };
            result.push((key.clone(), value));
        }
        if depth != 0 {
            Err("Обрезанный файл Steam".into())
        } else {
            Ok(result)
        }
    }
    object(&tokens, &mut 0, 0)
}

#[cfg(test)]
#[path = "launch_tests.rs"]
mod tests;
