//! Native, user-initiated desktop actions. No settings are changed by the picker.
use std::path::{Path, PathBuf};
pub(crate) fn normalize_games_path(raw: &str) -> PathBuf {
    let raw = raw.trim();
    if raw.is_empty() {
        return chillhub_native::paths::default_games_dir();
    }
    let raw = raw.replace('/', "\\");
    let prefix = if raw.starts_with("\\\\") {
        "\\\\"
    } else if raw.starts_with('\\') {
        "\\"
    } else {
        ""
    };
    let parts = raw
        .split('\\')
        .filter(|part| !part.is_empty())
        .collect::<Vec<_>>()
        .join("\\");
    PathBuf::from(format!("{prefix}{parts}"))
}

pub(crate) fn same_path(a: &str, b: &str) -> bool {
    let clean = |s: &str| {
        s.trim()
            .trim_end_matches(['/', '\\'])
            .replace('/', "\\")
            .to_lowercase()
    };
    clean(a) == clean(b)
}

pub(crate) fn validate_path_override(
    path: &str,
    forced: Option<&std::ffi::OsStr>,
) -> Result<(), String> {
    if forced.is_some_and(|forced| !same_path(path, &forced.to_string_lossy())) {
        return Err("Папка игр закреплена параметрами запуска. Для её изменения откройте лаунчер без проверочного профиля.".into());
    }
    Ok(())
}

pub(crate) use chillhub_native::folder_picker::choose_folder;

pub(crate) fn modpack_url(mods: &chillhub_native::model::ModsRecord) -> Option<String> {
    let (team, rest) = mods.version.trim().split_once('-')?;
    let (package, _) = rest.rsplit_once('-')?;
    let valid = |s: &str| {
        !s.is_empty()
            && s.bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-')
    };
    if ![mods.community.trim(), team, package]
        .into_iter()
        .all(valid)
    {
        return None;
    }
    Some(format!(
        "https://thunderstore.io/c/{}/p/{team}/{package}/",
        mods.community.trim()
    ))
}

pub(crate) fn open_package(url: &str) -> Result<(), String> {
    let parsed = url::Url::parse(url).map_err(|e| e.to_string())?;
    if parsed.scheme() != "https"
        || parsed.host_str() != Some("thunderstore.io")
        || !parsed.username().is_empty()
        || parsed.password().is_some()
    {
        return Err("Ссылка на сборку недоступна".into());
    }
    #[cfg(windows)]
    {
        use windows_sys::Win32::UI::{Shell::ShellExecuteW, WindowsAndMessaging::SW_SHOWNORMAL};
        let wide: Vec<u16> = parsed.as_str().encode_utf16().chain(Some(0)).collect();
        let result = unsafe {
            ShellExecuteW(
                std::ptr::null_mut(),
                std::ptr::null(),
                wide.as_ptr(),
                std::ptr::null(),
                std::ptr::null(),
                SW_SHOWNORMAL,
            )
        } as isize;
        if result <= 32 {
            Err("Не удалось открыть страницу сборки".into())
        } else {
            Ok(())
        }
    }
    #[cfg(not(windows))]
    {
        Err("Доступно только в Windows".into())
    }
}

#[cfg(windows)]
pub(crate) fn copy_text(owner: isize, text: &str) -> Result<(), String> {
    use std::ffi::c_void;
    #[link(name = "user32")]
    unsafe extern "system" {
        fn OpenClipboard(owner: *mut c_void) -> i32;
        fn CloseClipboard() -> i32;
        fn EmptyClipboard() -> i32;
        fn SetClipboardData(format: u32, memory: *mut c_void) -> *mut c_void;
    }
    #[link(name = "kernel32")]
    unsafe extern "system" {
        fn GlobalAlloc(flags: u32, bytes: usize) -> *mut c_void;
        fn GlobalLock(memory: *mut c_void) -> *mut c_void;
        fn GlobalUnlock(memory: *mut c_void) -> i32;
        fn GlobalFree(memory: *mut c_void) -> *mut c_void;
    }
    let wide: Vec<u16> = text.encode_utf16().chain(Some(0)).collect();
    unsafe {
        let memory = GlobalAlloc(2, wide.len() * 2);
        if memory.is_null() {
            return Err("Не удалось подготовить текст для копирования".into());
        }
        let address = GlobalLock(memory);
        if address.is_null() {
            GlobalFree(memory);
            return Err("Не удалось подготовить текст для копирования".into());
        }
        std::ptr::copy_nonoverlapping(wide.as_ptr(), address.cast::<u16>(), wide.len());
        GlobalUnlock(memory);
        if OpenClipboard(owner as *mut c_void) == 0 {
            GlobalFree(memory);
            return Err("Буфер обмена занят. Попробуйте ещё раз.".into());
        }
        let success = EmptyClipboard() != 0 && !SetClipboardData(13, memory).is_null();
        CloseClipboard();
        if success {
            Ok(())
        } else {
            GlobalFree(memory);
            Err("Не удалось скопировать путь".into())
        }
    }
}
#[cfg(not(windows))]
pub(crate) fn copy_text(_: isize, _: &str) -> Result<(), String> {
    Err("Доступно только в Windows".into())
}

pub(crate) fn folder_size(path: &Path) -> Result<u64, String> {
    if !path.is_dir() {
        return Ok(0);
    }
    // Do not follow junctions or symbolic links into unrelated directories.
    chillhub_native::manifest::check_no_links(path).map_err(|e| e.to_string())?;
    let mut bytes = 0_u64;
    for entry in walkdir::WalkDir::new(path)
        .follow_links(false)
        .into_iter()
        .filter_entry(|entry| {
            #[cfg(windows)]
            {
                use std::os::windows::fs::MetadataExt;
                std::fs::symlink_metadata(entry.path())
                    .is_ok_and(|m| m.file_attributes() & 0x400 == 0)
            }
            #[cfg(not(windows))]
            {
                !entry.file_type().is_symlink()
            }
        })
    {
        let entry = entry.map_err(|e| e.to_string())?;
        if entry.file_type().is_file() {
            let metadata = entry.metadata().map_err(|e| e.to_string())?;
            #[cfg(windows)]
            {
                use std::os::windows::fs::MetadataExt;
                if metadata.file_attributes() & 0x400 != 0 {
                    continue;
                }
            }
            bytes = bytes.saturating_add(metadata.len());
        }
    }
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fixed_profile_rejects_an_ineffective_change_but_allows_other_preferences() {
        let forced = std::ffi::OsStr::new(r"C:\Preview\Games");
        assert!(validate_path_override(r"D:\Games", None).is_ok());
        assert!(validate_path_override("c:/preview/games/", Some(forced)).is_ok());
        assert!(
            validate_path_override(r"D:\Games", Some(forced))
                .unwrap_err()
                .contains("закреплена")
        );
    }
    #[test]
    fn equivalent_windows_paths_do_not_block_speed_changes() {
        assert!(same_path("D:/Games/", r"d:\games"));
        assert!(!same_path(r"D:\Games", r"D:\Games2"));
    }
    #[test]
    fn package_url_uses_server_community_and_rejects_path_injection() {
        let mut m = chillhub_native::model::ModsRecord {
            community: "peak".into(),
            version: "Lart_Iste-PeakFriendsEdition-2.0.15".into(),
            ..Default::default()
        };
        assert_eq!(
            modpack_url(&m).unwrap(),
            "https://thunderstore.io/c/peak/p/Lart_Iste/PeakFriendsEdition/"
        );
        m.community = "../other".into();
        assert!(modpack_url(&m).is_none());
    }
    #[test]
    fn disk_size_reads_only_requested_tree() {
        let t = tempfile::tempdir().unwrap();
        std::fs::write(t.path().join("one"), [1, 2, 3]).unwrap();
        assert_eq!(folder_size(t.path()).unwrap(), 3);
    }
    #[test]
    fn path_normalization_keeps_unc_and_missing_drive() {
        assert_eq!(
            normalize_games_path(r"D:\Games\\ChillHub"),
            PathBuf::from(r"D:\Games\ChillHub")
        );
        assert_eq!(
            normalize_games_path(r"\\nas\\games\\ChillHub"),
            PathBuf::from(r"\\nas\games\ChillHub")
        );
        assert_eq!(
            normalize_games_path(" "),
            chillhub_native::paths::default_games_dir()
        );
    }
}
