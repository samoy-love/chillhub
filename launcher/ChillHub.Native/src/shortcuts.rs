//! Game shortcuts are identified by their actual target and parsed arguments.
//! Automatic actions are disabled for isolated/preview profiles.
use crate::{
    launch::ShortcutRequest, manifest::DirectoryGuard, model::GameRecord, paths::AppPaths,
};
use serde::Deserialize;
use std::{
    fs,
    io::{Read, Write},
    path::{Path, PathBuf},
};

const MAX_LINK_BYTES: u64 = 512 * 1024;

#[derive(Debug, Deserialize)]
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
    let input =
        serde_json::to_vec(&serde_json::json!({"paths":paths})).map_err(|e| e.to_string())?;
    let output = crate::security::powershell(
        r#"
$r=[Console]::In.ReadToEnd()|ConvertFrom-Json
$shell=New-Object -ComObject WScript.Shell
$items=@(foreach($p in $r.paths){try {$l=$shell.CreateShortcut([string]$p); @{path=[string]$p;target=[string]$l.TargetPath;arguments=[string]$l.Arguments}}catch{}})
ConvertTo-Json -InputObject $items -Compress
"#,
        Some(&input),
    )?;
    serde_json::from_str(&output).map_err(|_| "Не удалось прочитать ярлыки".into())
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
    let name = filename(&request.title);
    let output = desktop.join(&name);
    // Existing user links are never overwritten, even if their name matches.
    if output.exists() {
        link_bytes(&output)?;
        let links = read_links(std::slice::from_ref(&output))?;
        if links.iter().any(|link| {
            link.path == output
                && path_key(&link.target) == path_key(launcher)
                && arguments(&link.arguments)
                    .and_then(|args| ShortcutRequest::parse_args(&args).ok().flatten())
                    .is_some_and(|stored| {
                        stored.game_id == request.game_id
                            && stored.exe_path.as_ref().and_then(|p| path_key(p)) == path_key(exe)
                    })
        }) {
            return Ok(output);
        }
        return Err("Одноимённый ярлык уже существует; он сохранён без изменений".into());
    }
    let temporary = desktop.join(format!(".chillhub-{}.lnk", uuid::Uuid::new_v4()));
    let args = request
        .arguments()
        .iter()
        .map(|v| crate::launch::quote_windows_argument(v))
        .collect::<Vec<_>>()
        .join(" ");
    let input = serde_json::to_vec(&serde_json::json!({"output":temporary,"launcher":launcher,"arguments":args,"working":launcher.parent(),"title":request.title,"icon":format!("{},0",exe.display())})).map_err(|e|e.to_string())?;
    let result = (|| {
        crate::security::powershell(
            r#"
$r=[Console]::In.ReadToEnd()|ConvertFrom-Json
$s=New-Object -ComObject WScript.Shell
$l=$s.CreateShortcut([string]$r.output)
$l.TargetPath=[string]$r.launcher; $l.Arguments=[string]$r.arguments
$l.WorkingDirectory=[string]$r.working; $l.Description=[string]$r.title
$l.IconLocation=[string]$r.icon; $l.Save()
"#,
            Some(&input),
        )?;
        let bytes = link_bytes(&temporary)?;
        let mut options = fs::OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            options.share_mode(1);
        }
        let mut file = options.open(&output).map_err(|e| e.to_string())?;
        file.write_all(&bytes)
            .and_then(|_| file.sync_all())
            .map_err(|e| e.to_string())?;
        Ok(output.clone())
    })();
    let _ = fs::remove_file(&temporary);
    result
}

fn path_key(path: &Path) -> Option<String> {
    if !path.is_absolute()
        || path
            .components()
            .any(|p| matches!(p, std::path::Component::ParentDir))
    {
        return None;
    }
    Some(
        path.to_string_lossy()
            .replace('/', "\\")
            .trim_end_matches('\\')
            .to_lowercase(),
    )
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
mod tests {
    use super::*;
    #[test]
    fn isolated_profile_never_touches_desktop() {
        let t = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(t.path());
        assert!(
            create_after_install(&paths, &GameRecord::default())
                .unwrap()
                .is_none()
        );
        assert_eq!(remove_for_game(&paths, "peak").unwrap(), 0);
    }
    #[test]
    fn ownership_requires_target_and_matching_local_arguments() {
        let launcher = Path::new(r"C:\Apps\ChillHub.exe");
        let root = Path::new(r"D:\Games\peak");
        let link = |target: &str, args: &str| Link {
            path: "ignored.lnk".into(),
            target: target.into(),
            arguments: args.into(),
        };
        assert!(owned(
            &link(r"d:\games\PEAK\bin\Game.exe", ""),
            launcher,
            "peak",
            root
        ));
        assert!(!owned(
            &link(r"D:\Games\peak-2\Game.exe", ""),
            launcher,
            "peak",
            root
        ));
        assert!(!owned(
            &link(
                r"C:\Windows\notepad.exe",
                r#"--game peak --exe "D:\Games\peak\Game.exe""#
            ),
            launcher,
            "peak",
            root
        ));
        assert!(owned(
            &link(
                r"C:\Apps\ChillHub.exe",
                r#"--game peak --exe "D:\Games\peak\Game.exe""#
            ),
            launcher,
            "peak",
            root
        ));
        assert!(!owned(
            &link(r"C:\Apps\ChillHub.exe", "--game peak --target SteamVanilla"),
            launcher,
            "peak",
            root
        ));
    }
    #[test]
    fn temp_shell_shortcuts_roundtrip_and_remove_only_owned() {
        let t = tempfile::tempdir().unwrap();
        let desktop = t.path().join("desktop");
        fs::create_dir(&desktop).unwrap();
        let root = t.path().join("games/peak");
        fs::create_dir_all(&root).unwrap();
        let launcher = t.path().join("ChillHub.exe");
        let exe = root.join("Game.exe");
        fs::write(&launcher, b"fixture").unwrap();
        fs::write(&exe, b"fixture").unwrap();
        let request = ShortcutRequest {
            game_id: "peak".into(),
            title: "Игра: тест".into(),
            exe_path: Some(exe),
            target: None,
        };
        let link = create_at(&desktop, &launcher, &request).unwrap();
        assert_eq!(link.file_name().unwrap(), "Игра_ тест (Chill Hub).lnk");
        let original = fs::read(&link).unwrap();
        fs::write(desktop.join("note.txt"), b"keep").unwrap();
        assert_eq!(create_at(&desktop, &launcher, &request).unwrap(), link);
        assert_eq!(fs::read(&link).unwrap(), original);
        assert_eq!(
            remove_at(&desktop, &launcher, "different", &root).unwrap(),
            0
        );
        assert_eq!(remove_at(&desktop, &launcher, "peak", &root).unwrap(), 1);
        assert!(desktop.join("note.txt").exists());
    }
}
