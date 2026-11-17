//! Game shortcuts are identified by their actual target and parsed arguments.
//! Automatic actions are disabled for isolated/preview profiles.
use crate::{
    launch::ShortcutRequest, manifest::DirectoryGuard, model::GameRecord, paths::AppPaths,
};
use std::{
    fs,
    io::Read,
    path::{Path, PathBuf},
};

const MAX_LINK_BYTES: u64 = 512 * 1024;

#[cfg(windows)]
pub(crate) mod native;

#[derive(Debug)]
struct Link {
    path: PathBuf,
    target: PathBuf,
    arguments: String,
}

#[cfg(windows)]
pub(crate) fn system_directory() -> Result<PathBuf, String> {
    use std::os::windows::ffi::OsStringExt;
    let mut buffer = vec![0u16; 32768];
    let count = unsafe {
        windows_sys::Win32::System::SystemInformation::GetSystemDirectoryW(
            buffer.as_mut_ptr(),
            buffer.len() as u32,
        )
    } as usize;
    if count == 0 || count >= buffer.len() {
        return Err("Не найдена системная папка Windows".into());
    }
    Ok(std::ffi::OsString::from_wide(&buffer[..count]).into())
}

fn environment(paths: &AppPaths) -> Result<Option<(PathBuf, PathBuf)>, String> {
    if paths.legacy_dir.is_none() || std::env::var_os("CHILLHUB_DATA_DIR").is_some() {
        return Ok(None);
    }
    let launcher = std::env::current_exe().map_err(|e| e.to_string())?;
    if launcher
        .file_name()
        .is_none_or(|name| !name.eq_ignore_ascii_case("ChillHub.exe"))
    {
        return Ok(None);
    }
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStringExt;
        use windows_sys::Win32::{
            System::Com::CoTaskMemFree,
            UI::Shell::{FOLDERID_Desktop, SHGetKnownFolderPath},
        };
        let mut value = std::ptr::null_mut();
        if unsafe { SHGetKnownFolderPath(&FOLDERID_Desktop, 0, std::ptr::null_mut(), &mut value) }
            < 0
            || value.is_null()
        {
            return Err("Рабочий стол недоступен".into());
        }
        let desktop = unsafe {
            let mut len = 0;
            while len < 32768 && *value.add(len) != 0 {
                len += 1;
            }
            let path = std::ffi::OsString::from_wide(std::slice::from_raw_parts(value, len));
            CoTaskMemFree(value.cast());
            PathBuf::from(path)
        };
        Ok(Some((desktop, launcher)))
    }
    #[cfg(not(windows))]
    {
        Ok(None)
    }
}

pub fn create_after_install(
    paths: &AppPaths,
    game: &GameRecord,
) -> Result<Option<PathBuf>, String> {
    let Some((desktop, launcher)) = environment(paths)? else {
        return Ok(None);
    };
    let root = paths.game_root(&game.game_id)?;
    let exe =
        crate::manifest::safe_join(&root, &game.exe_relative_path).map_err(|e| e.to_string())?;
    // A dependent modpack may already be running after the base finished.
    // The shortcut opens the launcher, whose normal launch checks own readiness.
    if !exe.is_file() {
        return Ok(None);
    }
    let request = ShortcutRequest {
        game_id: game.game_id.clone(),
        title: if game.title.trim().is_empty() {
            game.game_id.clone()
        } else {
            game.title.clone()
        },
        exe_path: Some(exe),
        target: None,
    };
    create_at(&desktop, &launcher, &request).map(Some)
}

pub fn remove_for_game(paths: &AppPaths, game_id: &str) -> Result<usize, String> {
    let Some((desktop, launcher)) = environment(paths)? else {
        return Ok(0);
    };
    remove_at(&desktop, &launcher, game_id, &paths.game_root(game_id)?)
}

fn filename(title: &str) -> String {
    let name: String = title
        .chars()
        .take(100)
        .map(|c| {
            if c.is_control() || "<>:\"/\\|?*".contains(c) {
                '_'
            } else {
                c
            }
        })
        .collect();
    format!(
        "{} (Chill Hub).lnk",
        name.trim().trim_end_matches([' ', '.'])
    )
}

fn link_bytes(path: &Path) -> Result<Vec<u8>, String> {
    let mut file =
        crate::manifest::open_regular(path, false, false, false).map_err(|e| e.to_string())?;
    let mut bytes = Vec::new();
    Read::by_ref(&mut file)
        .take(MAX_LINK_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() as u64 > MAX_LINK_BYTES {
        return Err("Ярлык слишком большой".into());
    }
    Ok(bytes)
}

fn read_links(paths: &[PathBuf]) -> Result<Vec<Link>, String> {
    #[cfg(windows)]
    return native::read(paths);
    #[cfg(not(windows))]
    {
        let _ = paths;
        Err("Ярлыки доступны только в Windows".into())
    }
}

#[cfg(windows)]
fn known_folder(id: &windows_sys::core::GUID) -> Option<PathBuf> {
    use std::os::windows::ffi::OsStringExt;
    use windows_sys::Win32::{System::Com::CoTaskMemFree, UI::Shell::SHGetKnownFolderPath};
    let mut value = std::ptr::null_mut();
    let status = unsafe { SHGetKnownFolderPath(id, 0, std::ptr::null_mut(), &mut value) };
    if value.is_null() {
        return None;
    }
    unsafe {
        let mut len = 0;
        while len < 32768 && *value.add(len) != 0 {
            len += 1;
        }
        let path = (status >= 0 && len > 0 && len < 32768).then(|| {
            PathBuf::from(std::ffi::OsString::from_wide(std::slice::from_raw_parts(
                value, len,
            )))
        });
        CoTaskMemFree(value.cast());
        path
    }
}

#[cfg(windows)]
pub(crate) fn launcher_shortcuts(exe: &Path) -> Vec<PathBuf> {
    if std::env::var_os("CHILLHUB_DATA_DIR").is_some() {
        return vec![];
    }
    use windows_sys::Win32::UI::Shell::*;
    let mut folders = Vec::new();
    for (id, recursive) in [
        (FOLDERID_Desktop, false),
        (FOLDERID_PublicDesktop, false),
        (FOLDERID_Programs, true),
        (FOLDERID_CommonPrograms, true),
    ] {
        if let Some(path) = known_folder(&id) {
            folders.push((path, recursive));
        }
    }
    if let Some(roaming) = known_folder(&FOLDERID_RoamingAppData) {
        folders.push((
            roaming.join("Microsoft/Internet Explorer/Quick Launch/User Pinned/TaskBar"),
            false,
        ));
    }
    shortcuts_to_refresh(exe, &folders)
}

#[cfg(windows)]
fn shortcuts_to_refresh(exe: &Path, folders: &[(PathBuf, bool)]) -> Vec<PathBuf> {
    let Some(target) = path_key(exe) else {
        return vec![];
    };
    let mut candidates = Vec::new();
    let mut guards = Vec::new();
    for (folder, recursive) in folders {
        let Ok(guard) = DirectoryGuard::acquire(folder, false) else {
            continue;
        };
        guards.push(guard);
        for entry in walkdir::WalkDir::new(folder)
            .follow_links(false)
            .max_depth(if *recursive { 8 } else { 1 })
            .into_iter()
            .take(4096)
            .filter_map(Result::ok)
        {
            if candidates.len() >= 512 {
                break;
            }
            if entry.file_type().is_file()
                && entry
                    .path()
                    .extension()
                    .is_some_and(|ext| ext.eq_ignore_ascii_case("lnk"))
                && link_bytes(entry.path()).is_ok()
            {
                candidates.push(entry.into_path());
            }
        }
    }
    let links = read_links(&candidates).unwrap_or_default();
    links
        .into_iter()
        .filter(|link| path_key(&link.target).as_ref() == Some(&target))
        .map(|link| link.path)
        .collect()
}

fn create_at(
    desktop: &Path,
    launcher: &Path,
    request: &ShortcutRequest,
) -> Result<PathBuf, String> {
    crate::paths::validate_game_id(&request.game_id)?;
    let _directory = DirectoryGuard::acquire(desktop, false).map_err(|e| e.to_string())?;
    let exe = request.exe_path.as_ref().ok_or("Не указан файл игры")?;
    crate::manifest::check_no_links(exe).map_err(|e| e.to_string())?;
    crate::manifest::check_no_links(launcher).map_err(|e| e.to_string())?;
    if !exe.is_file() || !launcher.is_file() {
        return Err("Файл игры или лаунчера не найден".into());
    }
    let output = desktop.join(filename(&request.title));
    publish(&output, launcher, &request.arguments(), &request.title, exe)?;
    Ok(output)
}

/// Publish only a complete Shell Link, without replacing any existing file.
#[cfg(windows)]
fn publish_move(temporary: &Path, output: &Path) -> std::io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::MoveFileExW;
    let from: Vec<_> = temporary.as_os_str().encode_wide().chain([0]).collect();
    let to: Vec<_> = output.as_os_str().encode_wide().chain([0]).collect();
    if unsafe { MoveFileExW(from.as_ptr(), to.as_ptr(), 0) } == 0 {
        return Err(std::io::Error::last_os_error());
    }
    Ok(())
}

pub(crate) fn publish(
    output: &Path,
    launcher: &Path,
    argv: &[String],
    title: &str,
    icon: &Path,
) -> Result<(), String> {
    let directory = output.parent().ok_or("Не найдена папка ярлыка")?;
    let _guard = DirectoryGuard::acquire(directory, false).map_err(|e| e.to_string())?;
    crate::manifest::check_no_links(launcher).map_err(|e| e.to_string())?;
    let existing = || -> Result<(), String> {
        link_bytes(output)?;
        let links = read_links(&[output.to_path_buf()])?;
        if links.iter().any(|link| {
            path_key(&link.target)
                .zip(path_key(launcher))
                .is_some_and(|(a, b)| a == b)
                && arguments(&link.arguments).is_some_and(|stored| same_arguments(&stored, argv))
        }) {
            Ok(())
        } else {
            Err("Одноимённый ярлык уже существует; он сохранён без изменений".into())
        }
    };
    if output.symlink_metadata().is_ok() {
        return existing();
    }
    let temporary = directory.join(format!(".chillhub-{}.lnk", uuid::Uuid::new_v4()));
    let args = argv
        .iter()
        .map(|v| crate::launch::quote_windows_argument(v))
        .collect::<Vec<_>>()
        .join(" ");
    let result = (|| {
        #[cfg(windows)]
        {
            native::write(
                &temporary,
                launcher,
                &args,
                launcher.parent().ok_or("Не найдена папка лаунчера")?,
                title,
                icon,
            )?;
            // Validate the finished temporary before making it visible under its final name.
            link_bytes(&temporary)?;
            match publish_move(&temporary, output) {
                Ok(()) => Ok(()),
                Err(error) if output.symlink_metadata().is_ok() => existing().map_err(|_| {
                    format!("Одноимённый ярлык уже существует; он сохранён без изменений: {error}")
                }),
                Err(error) => Err(error.to_string()),
            }
        }
        #[cfg(not(windows))]
        {
            let _ = (&args, title, icon);
            Err("Ярлыки доступны только в Windows".into())
        }
    })();
    let _ = fs::remove_file(&temporary);
    result
}

fn same_arguments(stored: &[String], expected: &[String]) -> bool {
    if stored.len() != expected.len() {
        return false;
    }
    stored
        .iter()
        .zip(expected)
        .enumerate()
        .all(|(index, (a, b))| {
            // File aliases are equivalent, while target/profile and every other argument
            // must retain their exact meaning and ordering.
            a == b
                || (index > 0
                    && expected[index - 1] == "--exe"
                    && path_key(Path::new(a))
                        .zip(path_key(Path::new(b)))
                        .is_some_and(|(a, b)| a == b))
        })
}

fn path_key(path: &Path) -> Option<String> {
    crate::paths::path_identity(path)
}
fn inside_exe(path: &Path, root: &Path) -> bool {
    path.extension()
        .is_some_and(|s| s.eq_ignore_ascii_case("exe"))
        && path_key(path)
            .zip(path_key(root))
            .is_some_and(|(p, r)| p.starts_with(&(r + "\\")))
}
#[cfg(windows)]
fn arguments(value: &str) -> Option<Vec<String>> {
    use windows_sys::Win32::{Foundation::LocalFree, UI::Shell::CommandLineToArgvW};
    let text: Vec<u16> = format!("launcher.exe {value}")
        .encode_utf16()
        .chain([0])
        .collect();
    let mut count = 0;
    let raw = unsafe { CommandLineToArgvW(text.as_ptr(), &mut count) };
    if raw.is_null() {
        return None;
    }
    let result = unsafe {
        std::slice::from_raw_parts(raw, count as usize)
            .iter()
            .skip(1)
            .map(|&p| {
                let mut n = 0;
                while *p.add(n) != 0 {
                    n += 1;
                }
                String::from_utf16_lossy(std::slice::from_raw_parts(p, n))
            })
            .collect()
    };
    unsafe {
        LocalFree(raw.cast());
    }
    Some(result)
}
#[cfg(not(windows))]
fn arguments(_: &str) -> Option<Vec<String>> {
    None
}
fn owned(link: &Link, launcher: &Path, game_id: &str, root: &Path) -> bool {
    if inside_exe(&link.target, root) {
        return true;
    }
    if path_key(&link.target)
        .zip(path_key(launcher))
        .is_none_or(|(a, b)| a != b)
    {
        return false;
    }
    let Some(args) = arguments(&link.arguments) else {
        return false;
    };
    let Ok(Some(request)) = ShortcutRequest::parse_args(&args) else {
        return false;
    };
    request.game_id.eq_ignore_ascii_case(game_id)
        && request.target.is_none_or(|t| !t.via_steam())
        && request
            .exe_path
            .as_ref()
            .is_none_or(|exe| inside_exe(exe, root))
}
fn remove_at(desktop: &Path, launcher: &Path, game_id: &str, root: &Path) -> Result<usize, String> {
    crate::paths::validate_game_id(game_id)?;
    if !desktop.is_dir() {
        return Ok(0);
    }
    let _directory = DirectoryGuard::acquire(desktop, false).map_err(|e| e.to_string())?;
    let mut snapshots = std::collections::HashMap::new();
    let mut remaining = 8 * 1024 * 1024;
    for entry in fs::read_dir(desktop)
        .map_err(|e| e.to_string())?
        .take(512)
        .filter_map(Result::ok)
    {
        let p = entry.path();
        if p.extension().is_some_and(|s| s.eq_ignore_ascii_case("lnk"))
            && let Ok(bytes) = link_bytes(&p)
        {
            if bytes.len() > remaining {
                continue;
            }
            remaining -= bytes.len();
            snapshots.insert(p, bytes);
        }
    }
    if snapshots.is_empty() {
        return Ok(0);
    }
    let links = read_links(&snapshots.keys().cloned().collect::<Vec<_>>())?;
    let mut removed = 0;
    for link in links {
        let Some(before) = snapshots.get(&link.path) else {
            continue;
        };
        if owned(&link, launcher, game_id, root)
            && link_bytes(&link.path).is_ok_and(|now| &now == before)
            && fs::remove_file(&link.path).is_ok()
        {
            removed += 1;
        }
    }
    Ok(removed)
}

#[cfg(test)]
#[path = "shortcuts_tests.rs"]
mod tests;
