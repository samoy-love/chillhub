//! Filesystem boundary used by both interactive and silent Native uninstall.
use std::{
    fs, io,
    path::{Component, Path, PathBuf, Prefix},
};

use crate::{
    helper_runtime::{HelperKind, HelperLog},
    manifest::{DirectoryGuard, check_no_links, is_reparse},
    paths::path_identity,
    update::{ProcessLock, update_mutex_name},
};

const UNINSTALLER: &str = "Uninstall.exe";
const UPDATER: &str = "ChillHub.Updater.exe";
const SECURITY_JOURNAL: &str = "security-exclusions.json";
pub(super) const EXPECTED_GAMES_ENV: &str = "CHILLHUB_NATIVE_EXPECT_GAMES_PATH";

pub fn run_uninstall_cleanup(args: Vec<String>) -> Result<(), String> {
    let result = (|| match args.as_slice() {
        [flag] if flag == "0" || flag == "1" => {
            let (install, games) = super::engine_paths()?;
            let delete_games = flag == "1";
            let roaming = known_folder(&windows_sys::Win32::UI::Shell::FOLDERID_RoamingAppData)
                .ok_or("Не удалось найти папку настроек пользователя")?
                .join("ChillHub");
            let legacy = known_folder(&windows_sys::Win32::UI::Shell::FOLDERID_LocalAppData)
                .ok_or("Не удалось найти локальную папку пользователя")?
                .join("ChillHub");
            let _install_lock = ProcessLock::acquire(&update_mutex_name(&install)?)?;
            let _security = ProcessLock::acquire("Local\\ChillHub.SecurityUi")?;
            if delete_games {
                if let Some(expected) = std::env::var_os(EXPECTED_GAMES_ENV) {
                    if !same_known_path(&games, Path::new(&expected)) {
                        return Err("Папка игр изменилась. Откройте удаление заново".into());
                    }
                } else {
                    require_current_library(&games, &roaming, &legacy)?;
                }
            }
            clean_installation(
                &install,
                &games,
                delete_games,
                &roaming,
                &legacy,
                &protected_folders(),
            )
        }
        _ => Err("Неверные параметры удаления Chill Hub".into()),
    })();
    if let Err(error) = &result
        && let Ok(log) = HelperLog::in_directory(
            &std::env::temp_dir().join("ChillHub/setup"),
            HelperKind::Setup,
        )
    {
        let _ = log.record("setup.cleanup-error", error);
    }
    result
}

fn require_current_library(registered: &Path, roaming: &Path, legacy: &Path) -> Result<(), String> {
    let roaming_config = roaming.join("config.json");
    check_no_links(&roaming_config).map_err(|e| e.to_string())?;
    let data_dir = if roaming_config.try_exists().map_err(|e| e.to_string())? {
        roaming
    } else {
        check_no_links(&legacy.join("config.json")).map_err(|e| e.to_string())?;
        legacy
    };
    let config = crate::config::ConfigStore::new(crate::paths::AppPaths {
        data_dir: data_dir.to_owned(),
        legacy_dir: None,
        games_dir: registered.to_owned(),
    })
    .load_readonly()?;
    if !same_known_path(registered, Path::new(&config.games_path)) {
        return Err("Папки игр в настройках и установке различаются. Для удаления папки используйте окно деинсталлятора".into());
    }
    Ok(())
}

fn known_folder(id: &windows_sys::core::GUID) -> Option<PathBuf> {
    use std::{ffi::OsString, os::windows::ffi::OsStringExt, ptr};
    use windows_sys::Win32::{System::Com::CoTaskMemFree, UI::Shell::SHGetKnownFolderPath};
    let mut raw = ptr::null_mut();
    let result = unsafe { SHGetKnownFolderPath(id, 0, ptr::null_mut(), &mut raw) };
    if result < 0 || raw.is_null() {
        return None;
    }
    let path = unsafe {
        let length = (0..).position(|i| *raw.add(i) == 0).unwrap();
        let path = OsString::from_wide(std::slice::from_raw_parts(raw, length));
        CoTaskMemFree(raw.cast());
        PathBuf::from(path)
    };
    Some(path)
}

fn protected_folders() -> Vec<PathBuf> {
    use windows_sys::Win32::UI::Shell::{
        FOLDERID_Desktop, FOLDERID_Documents, FOLDERID_LocalAppData, FOLDERID_Profile,
        FOLDERID_ProgramData, FOLDERID_RoamingAppData,
    };
    let mut paths: Vec<_> = [
        &FOLDERID_Desktop,
        &FOLDERID_Documents,
        &FOLDERID_LocalAppData,
        &FOLDERID_Profile,
        &FOLDERID_ProgramData,
        &FOLDERID_RoamingAppData,
    ]
    .into_iter()
    .filter_map(known_folder)
    .collect();
    paths.push(std::env::temp_dir());
    paths.extend(
        ["WINDIR", "ProgramFiles", "ProgramFiles(x86)", "PUBLIC"]
            .into_iter()
            .filter_map(std::env::var_os)
            .map(PathBuf::from),
    );
    paths
}

fn identity(path: &Path) -> Option<PathBuf> {
    path_identity(path).map(PathBuf::from)
}

fn same_known_path(path: &Path, expected: &Path) -> bool {
    matches!((identity(path), identity(expected)), (Some(a), Some(b)) if a == b)
}

fn local_directory_path(path: &Path) -> bool {
    matches!(
        path.components().next(),
        Some(Component::Prefix(prefix)) if matches!(prefix.kind(), Prefix::Disk(_))
    ) && path.is_absolute()
        && path.file_name().is_some()
}

fn validate_target(path: &Path, protected: &[PathBuf]) -> Result<PathBuf, String> {
    if !local_directory_path(path) {
        return Err("Удаление возможно только из отдельной локальной папки".into());
    }
    for part in path.components() {
        match part {
            Component::ParentDir | Component::CurDir => {
                return Err("В пути для удаления есть переход к другой папке".into());
            }
            Component::Normal(name) => {
                let name = name.to_string_lossy();
                if name.ends_with(['.', ' ']) || name.contains([':', '*', '?']) {
                    return Err("Некорректное имя папки для удаления".into());
                }
            }
            _ => {}
        }
    }
    check_no_links(path).map_err(|e| e.to_string())?;
    let canonical = identity(path).ok_or("Не удалось проверить путь для удаления")?;
    if canonical.file_name().is_none()
        || (canonical.components().count() == 3
            && canonical.file_name().is_some_and(|name| name == "users"))
        || std::env::var_os("WINDIR")
            .and_then(|path| identity(Path::new(&path)))
            .is_some_and(|windows| canonical.starts_with(windows))
        || protected
            .iter()
            .filter_map(|p| identity(p))
            .any(|protected| protected.starts_with(&canonical))
    {
        return Err("Выбран защищённый каталог Windows или его родительская папка".into());
    }
    match fs::symlink_metadata(path) {
        Ok(meta) if !meta.is_dir() || is_reparse(&meta) => {
            Err("Для удаления нужна обычная папка".into())
        }
        Ok(_) => Ok(canonical),
        Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(canonical),
        Err(e) => Err(e.to_string()),
    }
}

fn overlap(a: &Path, b: &Path) -> bool {
    a.starts_with(b) || b.starts_with(a)
}

fn clean_installation(
    install: &Path,
    games: &Path,
    delete_games: bool,
    roaming: &Path,
    legacy: &Path,
    protected: &[PathBuf],
) -> Result<(), String> {
    let install_id = validate_target(install, protected)?;
    let roaming_id = identity(roaming).ok_or("Не удалось проверить папку настроек")?;
    let legacy_id = identity(legacy).ok_or("Не удалось проверить прежнюю папку приложения")?;
    if overlap(&install_id, &roaming_id)
        || (install_id != legacy_id && overlap(&install_id, &legacy_id))
    {
        return Err("Папка установки пересекается с папкой данных Chill Hub".into());
    }
    let uninstaller = install.join(UNINSTALLER);
    let _install_guard = DirectoryGuard::acquire(install, false).map_err(|e| e.to_string())?;
    match fs::symlink_metadata(&uninstaller) {
        Ok(meta) if meta.is_file() && !is_reparse(&meta) => {}
        _ => return Err("В указанной папке нет установленного Chill Hub".into()),
    }
    let games_id = if delete_games {
        let games_id = validate_target(games, protected)?;
        if [&install_id, &roaming_id, &legacy_id]
            .iter()
            .any(|path| overlap(path, &games_id))
        {
            return Err("Папка игр пересекается с папкой лаунчера или настроек".into());
        }
        Some(games_id)
    } else {
        // A missing or unreadable library must not prevent removing the launcher.
        // Keep enough identity information to avoid removing a nested legacy library.
        local_directory_path(games).then(|| {
            identity(games).unwrap_or_else(|| PathBuf::from(games.to_string_lossy().to_lowercase()))
        })
    };
    if let Some(path) = games_id.as_ref()
        && path.starts_with(&install_id)
    {
        return Err("Папка игр пересекается с установкой. Сначала переместите её".into());
    }
    let _games_parent = if delete_games {
        let guard = DirectoryGuard::acquire(games.parent().ok_or("Папка игр недоступна")?, false)
            .map_err(|e| e.to_string())?;
        let current = validate_target(games, protected)?;
        if Some(&current) != games_id.as_ref() {
            return Err("Папка игр изменилась. Повторите удаление".into());
        }
        if games.is_dir() && !crate::playtime::processes_in(games)?.is_empty() {
            return Err("Закройте игры перед удалением их файлов".into());
        }
        Some(guard)
    } else {
        None
    };

    if delete_games {
        remove_directory(games)?;
    }
    if !games_id
        .as_ref()
        .is_some_and(|games| games.starts_with(&roaming_id))
    {
        clean_data(roaming, SECURITY_JOURNAL)?;
    }
    // A second installation in the default location belongs to its uninstaller.
    if install_id != legacy_id
        && !legacy.join("ChillHub.exe").exists()
        && !legacy.join(UNINSTALLER).exists()
        && !games_id
            .as_ref()
            .is_some_and(|games| games.starts_with(&legacy_id))
    {
        clean_data(legacy, SECURITY_JOURNAL)?;
    }
    // The NSIS stub may itself run from the installation with /S _?=….
    // Keep both entrypoints until all other cleanup has succeeded.
    clean_children(install, &[UNINSTALLER, UPDATER])?;
    remove_file_if_present(&install.join(UPDATER))?;
    if let Err(error) = fs::remove_file(&uninstaller)
        && !matches!(error.raw_os_error(), Some(5 | 32 | 33))
    {
        return Err(format!("Не удалось удалить деинсталлятор: {error}"));
    }
    Ok(())
}

fn remove_file_if_present(path: &Path) -> Result<(), String> {
    match fs::remove_file(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(format!("Не удалось удалить «{}»: {error}", path.display())),
    }
}

fn remove_directory(path: &Path) -> Result<(), String> {
    check_no_links(path).map_err(|e| e.to_string())?;
    match fs::remove_dir_all(path) {
        Ok(()) => Ok(()),
        Err(error) if error.kind() == io::ErrorKind::NotFound => Ok(()),
        Err(error) => Err(format!(
            "Не удалось удалить папку «{}»: {error}",
            path.display()
        )),
    }
}

fn clean_data(path: &Path, keep: &str) -> Result<(), String> {
    if !path.exists() {
        return Ok(());
    }
    {
        let _guard = DirectoryGuard::acquire(path, false).map_err(|e| e.to_string())?;
        clean_children(path, &[keep])?;
    }
    if !path.join(keep).exists() {
        // Remove an empty directory without recursively touching anything that
        // might have appeared after our guarded cleanup.
        fs::remove_dir(path).map_err(|e| e.to_string())?;
    }
    Ok(())
}

fn clean_children(path: &Path, keep: &[&str]) -> Result<(), String> {
    for child in fs::read_dir(path).map_err(|e| e.to_string())? {
        let child = child.map_err(|e| e.to_string())?;
        if keep
            .iter()
            .any(|name| child.file_name().eq_ignore_ascii_case(name))
        {
            continue;
        }
        let path = child.path();
        let kind = child.file_type().map_err(|e| e.to_string())?;
        match kind {
            kind if kind.is_dir() || std::os::windows::fs::FileTypeExt::is_symlink_dir(&kind) => {
                fs::remove_dir_all(&path)
            }
            _ => fs::remove_file(&path),
        }
        .map_err(|error| format!("Не удалось удалить «{}»: {error}", path.display()))?;
    }
    Ok(())
}

#[cfg(test)]
#[path = "cleanup_tests.rs"]
mod tests;
