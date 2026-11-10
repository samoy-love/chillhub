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

#[cfg(windows)]
pub(crate) fn choose_folder(owner: isize, initial: &Path) -> Result<Option<PathBuf>, String> {
    use windows::{
        Win32::{
            Foundation::HWND,
            System::Com::{
                CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx,
                CoTaskMemFree, CoUninitialize,
            },
            UI::Shell::{
                FOS_FORCEFILESYSTEM, FOS_NOCHANGEDIR, FOS_PATHMUSTEXIST, FOS_PICKFOLDERS,
                FileOpenDialog, IFileOpenDialog, IShellItem, SHCreateItemFromParsingName,
                SIGDN_FILESYSPATH,
            },
        },
        core::HSTRING,
    };
    // Called on a dedicated worker. Every initialized apartment is uninitialized,
    // including user cancellation and COM errors; no window is created in tests.
    unsafe {
        CoInitializeEx(None, COINIT_APARTMENTTHREADED)
            .ok()
            .map_err(|e| e.to_string())?;
        struct Apartment;
        impl Drop for Apartment {
            fn drop(&mut self) {
                unsafe {
                    CoUninitialize();
                }
            }
        }
        let _apartment = Apartment;
        let dialog: IFileOpenDialog = CoCreateInstance(&FileOpenDialog, None, CLSCTX_INPROC_SERVER)
            .map_err(|e| e.to_string())?;
        dialog
            .SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | FOS_NOCHANGEDIR)
            .map_err(|e| e.to_string())?;
        dialog
            .SetTitle(&HSTRING::from("Выберите папку для игр Chill Hub"))
            .map_err(|e| e.to_string())?;
        let initial = normalize_games_path(&initial.to_string_lossy());
        if initial.is_dir() {
            let folder: windows::core::Result<IShellItem> =
                SHCreateItemFromParsingName(&HSTRING::from(initial.as_os_str()), None);
            if let Ok(folder) = folder {
                let _ = dialog.SetFolder(&folder);
            }
        }
        if let Err(e) = dialog.Show(if owner == 0 {
            None
        } else {
            Some(HWND(owner as *mut _))
        }) {
            if e.code().0 as u32 == 0x800704c7 {
                return Ok(None);
            }
            return Err(format!("Не удалось открыть выбор папки: {e}"));
        }
        let name = dialog
            .GetResult()
            .and_then(|v| v.GetDisplayName(SIGDN_FILESYSPATH))
            .map_err(|e| e.to_string())?;
        let result = name
            .to_string()
            .map(PathBuf::from)
            .map_err(|e| e.to_string());
        CoTaskMemFree(Some(name.0 as *const _));
        result.map(Some)
    }
}
#[cfg(not(windows))]
pub(crate) fn choose_folder(_: isize, _: &Path) -> Result<Option<PathBuf>, String> {
    Err("Выбор папки доступен в Windows".into())
}

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
