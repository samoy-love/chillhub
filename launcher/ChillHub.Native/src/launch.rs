use crate::{
    config::{ConfigStore, LaunchTarget},
    model::{GameRecord, ModsRecord},
    paths::{AppPaths, atomic_write, contained_path, relative_path, validate_game_id},
    playtime::{PlaytimeStore, ProcessIdentity, SessionHandle, processes_in},
};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
    process::Command,
    time::{Duration, Instant},
};

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
        }
    }
    None
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
    UpdateGame,
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
                (
                    LaunchAction::Unavailable,
                    "Обновление не завершено. Восстановите файлы",
                )
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
        let installed = !read_marker(&local, ".version").is_empty();
        let outdated = !read_marker(&local, ".version").eq_ignore_ascii_case(local_version.trim());
        for target in targets {
            let (action, note) = if local.join(".updating").exists() {
                (
                    LaunchAction::UpdateGame,
                    "Обновление не завершено. Восстановить игру",
                )
            } else if !installed && !exe_exists {
                (LaunchAction::InstallGame, "Установить игру")
            } else if !exe_exists {
                (LaunchAction::UpdateGame, "Восстановить игру")
            } else if outdated {
                (LaunchAction::UpdateGame, "Обновить игру")
            } else if crate::integrity::needs_repair(&local, local_version) {
                (LaunchAction::UpdateGame, "Восстановить файлы игры")
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
    match installed_modpack_paths(root) {
        Ok(paths) => paths
            .into_iter()
            .filter(|p| {
                p.to_string_lossy().replace('\\', "/").to_lowercase() != "doorstop_config.ini"
            })
            .any(|p| contained_path(root, &p).map_or(true, |p| !p.is_file())),
        Err(_) => true,
    }
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

/// Existing self-managed loader settings are preserved only while usable. The
/// enabled flag intentionally differs from the published hash between sessions.
pub fn doorstop_config_valid(root: &Path) -> bool {
    let Ok(path) = contained_path(root, Path::new("doorstop_config.ini")) else {
        return false;
    };
    let Ok(bytes) = read_limited(&path, 256 * 1024) else {
        return false;
    };
    let Ok(text) = String::from_utf8(bytes) else {
        return false;
    };
    text.lines()
        .find_map(|line| {
            let line = line.trim_start_matches('\u{feff}').trim_start();
            if line.starts_with([';', '#', '[']) {
                return None;
            }
            let (key, value) = line.split_once('=')?;
            if !key.trim().eq_ignore_ascii_case("enabled") {
                return None;
            }
            let value = value.split([';', '#']).next().unwrap_or_default().trim();
            Some(value.eq_ignore_ascii_case("true") || value.eq_ignore_ascii_case("false"))
        })
        .unwrap_or(false)
}

pub fn set_doorstop_enabled(root: &Path, enabled: bool) -> Result<(), String> {
    let path = contained_path(root, Path::new("doorstop_config.ini"))?;
    if !path.exists() {
        return if enabled {
            Err("Загрузчик модов не установлен".into())
        } else {
            Ok(())
        };
    }
    let bytes = read_limited(&path, 256 * 1024)?;
    let text =
        String::from_utf8(bytes).map_err(|_| "Не удалось прочитать настройки загрузчика модов")?;
    let newline = if text.contains("\r\n") { "\r\n" } else { "\n" };
    let mut found = false;
    let mut output = Vec::new();
    for line in text.lines() {
        let trimmed = line.trim_start_matches('\u{feff}').trim_start();
        if !found
            && !trimmed.starts_with([';', '#', '['])
            && let Some((key, value)) = trimmed.split_once('=')
            && key.trim().eq_ignore_ascii_case("enabled")
        {
            let eq = line.find('=').unwrap();
            let padding = value.len() - value.trim_start_matches([' ', '\t']).len();
            let comment = value
                .find([';', '#'])
                .map(|i| format!(" {}", value[i..].trim_end()))
                .unwrap_or_default();
            output.push(format!(
                "{}{}{}{comment}",
                &line[..=eq],
                &value[..padding],
                if enabled { "true" } else { "false" }
            ));
            found = true;
            continue;
        }
        output.push(line.to_owned());
    }
    if !found {
        return Err(
            "В настройках загрузчика модов нет параметра enabled. Восстановите моды".into(),
        );
    }
    let mut rewritten = output.join(newline);
    if text.ends_with('\n') {
        rewritten.push_str(newline);
    }
    if rewritten != text {
        atomic_write(&path, rewritten.as_bytes()).map_err(|e| e.to_string())?;
    }
    Ok(())
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

    /// Blocking: invoke on a worker. Steam startup can take up to three minutes.
    pub fn launch(&self, game: &GameRecord, target: LaunchTarget) -> Result<SessionHandle, String> {
        let plan = self.plan(game, target)?;
        self.execute_plan(game, target, plan)
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
        let game = GameRecord {
            game_id: request.game_id.clone(),
            title: request.title.clone(),
            ..Default::default()
        };
        self.execute_plan(&game, target, plan)
    }
    fn execute_plan(
        &self,
        game: &GameRecord,
        target: LaunchTarget,
        plan: LaunchPlan,
    ) -> Result<SessionHandle, String> {
        let lock_dir = self.paths.data_dir.join("launch-locks");
        fs::create_dir_all(&lock_dir).map_err(|e| e.to_string())?;
        let canonical = fs::canonicalize(&plan.game_directory).map_err(|e| e.to_string())?;
        let key = blake3::hash(canonical.to_string_lossy().to_lowercase().as_bytes()).to_hex();
        let lock = fs::OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(lock_dir.join(format!("{key}.lock")))
            .map_err(|e| e.to_string())?;
        fs2::FileExt::try_lock_exclusive(&lock).map_err(|_| "Игра уже запускается или запущена")?;
        let operation_lock_path =
            contained_path(&plan.game_directory, Path::new(".chillhub.lock"))?;
        let operation_lock = if operation_lock_path.exists() {
            Some(
                fs::OpenOptions::new()
                    .read(true)
                    .open(&operation_lock_path)
                    .map_err(|e| e.to_string())?,
            )
        } else {
            match fs::OpenOptions::new()
                .create_new(true)
                .read(true)
                .write(true)
                .open(&operation_lock_path)
            {
                Ok(file) => Some(file),
                Err(e)
                    if e.kind() == std::io::ErrorKind::PermissionDenied
                        && !target.modded()
                        && !plan.game_directory.join("doorstop_config.ini").exists() =>
                {
                    None
                }
                Err(e) => {
                    return Err(format!(
                        "Не удалось защитить файлы игры на время запуска: {e}"
                    ));
                }
            }
        };
        if let Some(file) = &operation_lock {
            fs2::FileExt::try_lock_exclusive(file)
                .map_err(|_| "Файлы игры сейчас обновляются или игра уже запущена")?;
        }
        if plan.game_directory.join(".updating").exists() {
            return Err("Обновление не завершено. Восстановите игру".into());
        }
        if !processes_in(&plan.game_directory)?.is_empty() {
            return Err("Игра уже запущена. Закройте её перед сменой режима".into());
        }
        self.playtime.recover_commit()?;
        self.playtime.reconcile()?;
        set_doorstop_enabled(&plan.game_directory, target.modded())?;
        let started = (|| {
            self.config.update(|c| {
                c.remember(&game.game_id, target);
                c.last_game_id = game.game_id.clone();
            })?;
            let child = Command::new(&plan.executable)
                .args(&plan.arguments)
                .current_dir(&plan.working_directory)
                .spawn()
                .map_err(|e| format!("Не удалось запустить игру: {e}"))?;
            let process = if target.via_steam() {
                drop(child);
                let deadline = Instant::now() + Duration::from_secs(180);
                loop {
                    if let Some(process) = processes_in(&plan.game_directory)?
                        .into_iter()
                        .find(|p| path_equal(&p.exe_path, &plan.game_executable))
                    {
                        break process;
                    }
                    if Instant::now() >= deadline {
                        return Err(
                            "Steam не запустил игру. Проверьте окно Steam и попробуйте снова"
                                .into(),
                        );
                    }
                    std::thread::sleep(Duration::from_secs(1));
                }
            } else {
                let pid = child.id();
                drop(child);
                ProcessIdentity::capture(pid)?
            };
            let session = self
                .playtime
                .begin(
                    &game.game_id,
                    target,
                    process.clone(),
                    target.modded().then_some(plan.game_directory.as_path()),
                )
                .unwrap_or_else(|error| SessionHandle {
                    game_id: game.game_id.clone(),
                    target,
                    process: process.clone(),
                    warning: Some(format!(
                        "Игра запущена, но время игры не удалось сохранить: {error}"
                    )),
                });
            let cleanup_dir = plan.game_directory.clone();
            // Keep the per-copy lock while the game is alive, including Steam's startup handoff.
            std::thread::spawn(move || {
                while process.is_running() {
                    std::thread::sleep(Duration::from_secs(1));
                }
                if target.modded() {
                    let _ = set_doorstop_enabled(&cleanup_dir, false);
                }
                drop(operation_lock);
                drop(lock);
            });
            Ok(session)
        })();
        if started.is_err() && target.modded() {
            let _ = set_doorstop_enabled(&plan.game_directory, false);
        }
        started
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
        use base64::Engine;
        use std::os::windows::process::CommandExt;
        let literal = |s: &str| format!("'{}'", s.replace('\'', "''"));
        let script = format!(
            "$ErrorActionPreference='Stop'\n$s=New-Object -ComObject WScript.Shell\n$l=$s.CreateShortcut({})\n$l.TargetPath={}\n$l.Arguments={}\n$l.WorkingDirectory={}\n$l.Description={}\n$l.Save()",
            literal(&output.to_string_lossy()),
            literal(&launcher.to_string_lossy()),
            literal(&args),
            literal(&launcher.parent().unwrap_or(Path::new("")).to_string_lossy()),
            literal(&request.title)
        );
        let utf16: Vec<u8> = script.encode_utf16().flat_map(u16::to_le_bytes).collect();
        let encoded = base64::engine::general_purpose::STANDARD.encode(utf16);
        let powershell = std::env::var_os("SystemRoot")
            .map(PathBuf::from)
            .ok_or("Не найдена папка Windows")?
            .join(r"System32\WindowsPowerShell\v1.0\powershell.exe");
        let result = Command::new(powershell)
            .args(["-NoProfile", "-NonInteractive", "-EncodedCommand", &encoded])
            .creation_flags(0x08000000)
            .output()
            .map_err(|e| e.to_string())?;
        if !result.status.success() || !output.is_file() {
            return Err("Не удалось создать ярлык игры".into());
        }
        Ok(())
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
mod tests {
    use super::*;
    fn fixture() -> (tempfile::TempDir, AppPaths, GameRecord, SteamGame) {
        let temp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(temp.path());
        let mut game = GameRecord {
            game_id: "peak".into(),
            latest_version: "1".into(),
            exe_relative_path: "PEAK.exe".into(),
            ..Default::default()
        };
        game.mods = Some(ModsRecord {
            has_latest: true,
            version: "pack1".into(),
            revision: "abc".into(),
            steam_app_id: "3527290".into(),
            ..Default::default()
        });
        let steam_root = temp.path().join("steam");
        fs::create_dir_all(&steam_root).unwrap();
        fs::write(steam_root.join("steam.exe"), b"fixture only").unwrap();
        let steam_game = steam_root.join("game");
        for root in [paths.game_root("peak").unwrap(), steam_game.clone()] {
            fs::create_dir_all(&root).unwrap();
            for (name, contents) in [
                ("PEAK.exe", "fixture only"),
                (".version", "1"),
                (".mods.version", "pack1"),
                (".mods.revision", "abc"),
                ("winhttp.dll", "fixture"),
                ("doorstop_config.ini", "[General]\nenabled = false\n"),
            ] {
                fs::write(root.join(name), contents).unwrap();
            }
        }
        let steam = SteamGame {
            outcome: SteamLookup::Found,
            game_dir: steam_game,
            steam_exe: steam_root.join("steam.exe"),
            trace: vec![],
        };
        (temp, paths, game, steam)
    }
    #[test]
    fn four_plans_are_read_only_and_pick_correct_executables() {
        let (_temp, paths, game, steam) = fixture();
        let service = LaunchService::new(paths);
        let before = fs::read(steam.game_dir.join("doorstop_config.ini")).unwrap();
        for target in [
            LaunchTarget::SteamModded,
            LaunchTarget::SteamVanilla,
            LaunchTarget::LocalModded,
            LaunchTarget::LocalVanilla,
        ] {
            let plan = service
                .plan_with_steam(&game, target, Some(&steam))
                .unwrap();
            assert_eq!(
                plan.executable.file_name().unwrap(),
                if target.via_steam() {
                    "steam.exe"
                } else {
                    "PEAK.exe"
                }
            );
            assert_eq!(
                plan.arguments,
                if target.via_steam() {
                    vec!["-applaunch", "3527290"]
                } else {
                    vec![]
                }
            );
        }
        assert_eq!(
            fs::read(steam.game_dir.join("doorstop_config.ini")).unwrap(),
            before
        );
        assert!(!service.paths.config_path().exists());
    }
    #[test]
    fn absent_exe_and_unfinished_update_cannot_be_launched() {
        let (_temp, paths, game, steam) = fixture();
        let local = paths.game_root("peak").unwrap();
        let service = LaunchService::new(paths);
        fs::remove_file(local.join("PEAK.exe")).unwrap();
        assert!(
            service
                .plan_with_steam(&game, LaunchTarget::LocalVanilla, Some(&steam))
                .is_err()
        );
        fs::write(steam.game_dir.join(".updating"), "1").unwrap();
        assert!(
            service
                .plan_with_steam(&game, LaunchTarget::SteamVanilla, Some(&steam))
                .is_err()
        );
    }
    #[test]
    fn mods_only_and_plain_only_have_correct_supported_targets() {
        let (_temp, paths, mut game, steam) = fixture();
        game.latest_version.clear();
        let opts = options_with_steam(&game, &paths, Some(&steam)).unwrap();
        assert_eq!(opts.len(), 2);
        assert!(opts.iter().all(|o| o.target.via_steam()));
        game.latest_version = "1".into();
        game.mods = None;
        let opts = options_with_steam(&game, &paths, None).unwrap();
        assert_eq!(opts.len(), 1);
        assert_eq!(opts[0].target, LaunchTarget::LocalVanilla);
    }
    #[test]
    fn rebuilt_revision_requires_update_and_broken_loader_repair() {
        let (_temp, paths, mut game, steam) = fixture();
        game.mods.as_mut().unwrap().revision = "new".into();
        assert_eq!(
            options_with_steam(&game, &paths, Some(&steam)).unwrap()[0].action,
            LaunchAction::UpdateMods
        );
        game.mods.as_mut().unwrap().revision = "abc".into();
        fs::remove_file(steam.game_dir.join("winhttp.dll")).unwrap();
        assert_eq!(
            options_with_steam(&game, &paths, Some(&steam)).unwrap()[0].action,
            LaunchAction::RepairMods
        );
    }
    #[test]
    fn doorstop_preserves_comments_and_rejects_missing_key() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("doorstop_config.ini");
        fs::write(
            &path,
            "[General]\r\n#enabled=false\r\nenabled = true ; comment\r\nother=1\r\n",
        )
        .unwrap();
        set_doorstop_enabled(temp.path(), false).unwrap();
        assert_eq!(
            fs::read_to_string(&path).unwrap(),
            "[General]\r\n#enabled=false\r\nenabled = false ; comment\r\nother=1\r\n"
        );
        fs::write(&path, "[General]\nother=1").unwrap();
        assert!(set_doorstop_enabled(temp.path(), true).is_err());
    }
    #[test]
    fn steam_discovers_legacy_and_modern_libraries_and_nested_game() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("Steam");
        let library = temp.path().join("Библиотека");
        fs::create_dir_all(root.join("steamapps")).unwrap();
        fs::create_dir_all(library.join("steamapps/common/Outer/Inner")).unwrap();
        fs::write(root.join("steam.exe"), "fixture").unwrap();
        fs::write(
            library.join("steamapps/appmanifest_123.acf"),
            "\"AppState\" { \"installdir\" \"Outer\" }",
        )
        .unwrap();
        let escaped = library.to_string_lossy().replace('\\', "\\\\");
        for body in [
            format!("\"LibraryFolders\" {{ \"1\" \"{escaped}\" }}"),
            format!("\"libraryfolders\" {{ \"1\" {{ \"path\" \"{escaped}\" }} }}"),
        ] {
            fs::write(root.join("steamapps/libraryfolders.vdf"), body).unwrap();
            let found = locate_steam_at(Some(&root), "123", "Outer/Inner");
            assert!(found.available());
            assert!(found.game_dir.ends_with("Outer/Inner"));
        }
        assert_eq!(
            locate_steam_at(Some(&root), "../evil", "").outcome,
            SteamLookup::NoAppId
        );
    }
    #[test]
    fn shortcut_rejects_path_escape_and_uri_command_injection() {
        let (_temp, paths, _game, steam) = fixture();
        let request = ShortcutRequest {
            game_id: "peak".into(),
            exe_path: Some(steam.steam_exe),
            ..Default::default()
        };
        assert!(request.allowed_fallback(&paths).is_err());
        assert!(ShortcutRequest::parse_uri("chillhub://game/peak?exe=C:/evil.exe").is_err());
        assert_eq!(
            ShortcutRequest::parse_uri("chillhub://game/peak?target=SteamVanilla")
                .unwrap()
                .target,
            Some(LaunchTarget::SteamVanilla)
        );
        assert!(ShortcutRequest::parse_args(&["--game".into(), "../evil".into()]).is_err());
        assert_eq!(quote_windows_argument("a\"b\\"), "\"a\\\"b\\\\\"");
    }
    #[test]
    fn shortcut_legacy_keys_do_not_consume_the_next_option() {
        let request = ShortcutRequest::parse_args(&[
            "--TITLE".into(),
            "--GAME".into(),
            "peak".into(),
            "--EXE=C:/Games/peak/game.exe".into(),
        ])
        .unwrap()
        .unwrap();
        assert_eq!(request.game_id, "peak");
        assert!(request.title.is_empty());
        assert_eq!(
            request.exe_path,
            Some(PathBuf::from("C:/Games/peak/game.exe"))
        );
        assert!(
            ShortcutRequest::parse_args(&["--game".into()])
                .unwrap()
                .is_none()
        );
        assert!(ShortcutRequest::parse_args(&["--GAME=../escape".into()]).is_err());
    }
    #[test]
    fn malformed_vdf_is_bounded_and_rejected() {
        assert!(parse_vdf("\"a\" { \"b\"").is_err());
        assert!(parse_vdf(&format!("{}{}", "\"a\" {".repeat(40), "}".repeat(40))).is_err());
    }
}
