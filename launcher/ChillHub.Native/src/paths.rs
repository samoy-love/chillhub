use std::path::{Component, Path, PathBuf};
use std::{fs, io};

/// Resolve aliases in existing ancestors without creating a missing suffix.
/// This is an identity operation, not a replacement for traversal/reparse guards.
pub fn canonical_path_with_missing(path: &Path) -> io::Result<PathBuf> {
    if !path.is_absolute()
        || path
            .components()
            .any(|part| matches!(part, Component::ParentDir))
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Invalid absolute path",
        ));
    }
    let mut existing = path;
    let mut missing = Vec::new();
    loop {
        match fs::canonicalize(existing) {
            Ok(mut canonical) => {
                if !missing.is_empty() && !fs::metadata(&canonical)?.is_dir() {
                    return Err(io::Error::new(
                        io::ErrorKind::NotADirectory,
                        "Ancestor is not a directory",
                    ));
                }
                for part in missing.into_iter().rev() {
                    canonical.push(part);
                }
                return Ok(canonical);
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                missing.push(
                    existing
                        .file_name()
                        .ok_or_else(|| {
                            io::Error::new(io::ErrorKind::NotFound, "No existing ancestor")
                        })?
                        .to_owned(),
                );
                existing = existing.parent().ok_or(error)?;
            }
            Err(error) => return Err(error),
        }
    }
}

/// Windows path identity for comparison only. Unknown identities never compare equal.
pub fn path_identity(path: &Path) -> Option<String> {
    let canonical = canonical_path_with_missing(path).ok()?;
    let text = canonical.to_str()?.replace('/', "\\");
    let text = if let Some(unc) = text.strip_prefix(r"\\?\UNC\") {
        format!(r"\\{unc}")
    } else {
        text.strip_prefix(r"\\?\").unwrap_or(&text).to_owned()
    };
    Some(text.trim_end_matches('\\').to_lowercase())
}

#[cfg(all(test, windows))]
pub(crate) fn short_test_path(path: &Path) -> PathBuf {
    use std::os::windows::ffi::{OsStrExt, OsStringExt};
    let source: Vec<u16> = path.as_os_str().encode_wide().chain([0]).collect();
    let mut output = vec![0; 32768];
    let count = unsafe {
        windows_sys::Win32::Storage::FileSystem::GetShortPathNameW(
            source.as_ptr(),
            output.as_mut_ptr(),
            output.len() as u32,
        )
    } as usize;
    assert!(
        count > 0 && count < output.len(),
        "GetShortPathNameW failed: {}",
        io::Error::last_os_error()
    );
    std::ffi::OsString::from_wide(&output[..count]).into()
}

#[derive(Clone, Debug)]
pub struct AppPaths {
    pub data_dir: PathBuf,
    pub legacy_dir: Option<PathBuf>,
    pub games_dir: PathBuf,
}

impl AppPaths {
    /// Environment overrides isolate all mutable user data, including migration.
    pub fn discover() -> Result<Self, String> {
        let isolated = std::env::var_os("CHILLHUB_DATA_DIR").map(PathBuf::from);
        let data_dir = isolated
            .clone()
            .or_else(|| std::env::var_os("APPDATA").map(|p| PathBuf::from(p).join("ChillHub")))
            .ok_or("Не удалось найти папку настроек пользователя")?;
        let legacy_dir = if isolated.is_some() {
            None
        } else {
            std::env::var_os("LOCALAPPDATA").map(|p| PathBuf::from(p).join("ChillHub"))
        };
        Ok(Self {
            data_dir,
            legacy_dir,
            games_dir: default_games_dir(),
        })
    }

    pub fn isolated(root: impl AsRef<Path>) -> Self {
        Self {
            data_dir: root.as_ref().join("data"),
            legacy_dir: None,
            games_dir: root.as_ref().join("games"),
        }
    }

    pub fn game_root(&self, game_id: &str) -> Result<PathBuf, String> {
        validate_game_id(game_id)?;
        contained_path(&self.games_dir, Path::new(game_id))
    }

    pub fn config_path(&self) -> PathBuf {
        self.data_dir.join("config.json")
    }
}

pub fn default_games_dir() -> PathBuf {
    std::env::var_os("CHILLHUB_GAMES_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            if Path::new("D:\\").is_dir() {
                PathBuf::from(r"D:\Games\ChillHub")
            } else {
                PathBuf::from(r"C:\Games\ChillHub")
            }
        })
}

pub fn validate_game_id(id: &str) -> Result<(), String> {
    if id.is_empty()
        || id.len() > 128
        || !id
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || c == b'-' || c == b'_')
    {
        return Err("Некорректное имя игры".into());
    }
    relative_path(id)?;
    Ok(())
}

/// Accept Windows and manifest separators, reject ADS, device names and traversal.
pub fn relative_path(raw: &str) -> Result<PathBuf, String> {
    if raw.is_empty() || raw.contains([':', '\0']) || raw.starts_with(['/', '\\']) {
        return Err("Недопустимый путь к файлу".into());
    }
    let normalized = raw.replace('\\', "/");
    let mut result = PathBuf::new();
    for part in normalized.split('/') {
        let stem = part.split('.').next().unwrap_or("").to_ascii_uppercase();
        let reserved = matches!(stem.as_str(), "CON" | "PRN" | "AUX" | "NUL")
            || ((stem.starts_with("COM") || stem.starts_with("LPT"))
                && stem.len() == 4
                && matches!(stem.as_bytes()[3], b'1'..=b'9'));
        if part.is_empty()
            || matches!(part, "." | "..")
            || part.ends_with([' ', '.'])
            || part
                .chars()
                .any(|c| c.is_control() || "<>\"|?*".contains(c))
            || reserved
        {
            return Err("Недопустимый путь к файлу".into());
        }
        result.push(part);
    }
    Ok(result)
}

/// Also checks existing ancestors, so a junction cannot redirect a write outside root.
pub fn contained_path(root: &Path, relative: &Path) -> Result<PathBuf, String> {
    if relative.is_absolute()
        || relative
            .components()
            .any(|c| !matches!(c, Component::Normal(_)))
    {
        return Err("Файл находится вне папки игры".into());
    }
    let target = root.join(relative);
    if root.exists() {
        let canonical_root = fs::canonicalize(root).map_err(|e| e.to_string())?;
        let mut current = root.to_path_buf();
        for component in relative.components() {
            current.push(component.as_os_str());
            if current.exists() {
                let canonical = fs::canonicalize(&current).map_err(|e| e.to_string())?;
                if !canonical.starts_with(&canonical_root) {
                    return Err("Файл находится вне папки игры".into());
                }
            }
        }
    }
    Ok(target)
}

pub fn atomic_write(path: &Path, bytes: &[u8]) -> io::Result<()> {
    use std::io::Write;
    let parent = path
        .parent()
        .ok_or_else(|| io::Error::other("У файла нет папки"))?;
    fs::create_dir_all(parent)?;
    let tmp = parent.join(format!(".{}.tmp", uuid::Uuid::new_v4()));
    let result = (|| {
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&tmp)?;
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        replace_file(&tmp, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&tmp);
    }
    result
}

#[cfg(windows)]
fn replace_file(source: &Path, dest: &Path) -> io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::{
        MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
    };
    let s: Vec<u16> = source.as_os_str().encode_wide().chain(Some(0)).collect();
    let d: Vec<u16> = dest.as_os_str().encode_wide().chain(Some(0)).collect();
    if unsafe {
        MoveFileExW(
            s.as_ptr(),
            d.as_ptr(),
            MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
        )
    } == 0
    {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(not(windows))]
fn replace_file(source: &Path, dest: &Path) -> io::Result<()> {
    fs::rename(source, dest)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn invalid_ancestor_errors_are_not_treated_as_missing_directories() {
        let temp = tempfile::tempdir().unwrap();
        assert!(canonical_path_with_missing(&temp.path().join("invalid\0name")).is_err());
        assert_eq!(fs::read_dir(temp.path()).unwrap().count(), 0);
    }

    #[test]
    fn game_identifiers_have_a_bounded_ascii_namespace() {
        for id in [
            "a".to_owned(),
            "Game_01-test".to_owned(),
            "a".repeat(127),
            "a".repeat(128),
        ] {
            assert!(validate_game_id(&id).is_ok(), "{id:?}");
        }
        for id in [
            String::new(),
            "a".repeat(129),
            "a".repeat(130),
            "игра".into(),
            "game.name".into(),
            "game name".into(),
            "game/name".into(),
        ] {
            assert!(validate_game_id(&id).is_err(), "{id:?}");
        }
    }

    #[test]
    fn manifest_paths_reject_ambiguous_components_and_allow_ordinary_names() {
        for raw in [
            "", "a//b", "a/./b", "a/", "a /b", "a./b", "a\n/b", "a\t/b", "a</b", "a>/b", "a\"/b",
            "a|/b", "a?/b", "a*/b", "con", "prn.dat", "Aux", "nul.exe", "com9.txt", "lpt1",
            "LPT9.exe",
        ] {
            assert!(relative_path(raw).is_err(), "{raw:?}");
        }
        for raw in [
            "COM0",
            "COM10",
            "LPT0",
            "LPT10",
            "COMx",
            "LPTx",
            "Company",
            "example.dat",
            "a/b",
        ] {
            assert!(relative_path(raw).is_ok(), "{raw:?}");
        }
    }

    #[test]
    fn containment_rejects_parent_components_even_with_no_existing_root() {
        let temp = tempfile::tempdir().unwrap();
        for root in [temp.path().to_path_buf(), temp.path().join("missing")] {
            for raw in ["../outside", "game/../../outside", "./file"] {
                assert!(contained_path(&root, Path::new(raw)).is_err(), "{raw:?}");
            }
            assert_eq!(
                contained_path(&root, Path::new("game/file")).unwrap(),
                root.join("game/file")
            );
        }
    }

    #[test]
    fn configured_game_directory_is_respected_in_an_isolated_process() {
        const MARKER: &str = "CHILLHUB_PATH_TEST_CHILD";
        if std::env::var_os(MARKER).is_some() {
            assert_eq!(
                default_games_dir(),
                PathBuf::from(std::env::var_os("CHILLHUB_GAMES_DIR").unwrap())
            );
            return;
        }
        let temp = tempfile::tempdir().unwrap();
        let output = std::process::Command::new(std::env::current_exe().unwrap())
            .args([
                "--exact",
                "paths::tests::configured_game_directory_is_respected_in_an_isolated_process",
                "--nocapture",
            ])
            .env(MARKER, "1")
            .env("CHILLHUB_GAMES_DIR", temp.path().join("Chosen Games"))
            .output()
            .unwrap();
        assert!(
            output.status.success(),
            "{}\n{}",
            String::from_utf8_lossy(&output.stdout),
            String::from_utf8_lossy(&output.stderr)
        );
    }

    #[test]
    fn failed_atomic_replacement_preserves_destination_and_removes_temporary_file() {
        let temp = tempfile::tempdir().unwrap();
        let dest = temp.path().join("directory");
        fs::create_dir(&dest).unwrap();
        fs::write(dest.join("keep"), b"original").unwrap();
        assert!(atomic_write(&dest, b"replacement").is_err());
        assert_eq!(fs::read(dest.join("keep")).unwrap(), b"original");
        assert_eq!(fs::read_dir(temp.path()).unwrap().count(), 1);
    }
    #[cfg(windows)]
    #[test]
    fn identity_resolves_short_and_verbatim_ancestors_without_creating_missing_paths() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("Long game directory name");
        fs::create_dir(&root).unwrap();
        let short = short_test_path(&root);
        let canonical = fs::canonicalize(&root).unwrap();
        let suffix = Path::new("Missing Game").join("Game.exe");
        assert_eq!(path_identity(&short), path_identity(&root));
        assert_eq!(path_identity(&canonical), path_identity(&root));
        assert_eq!(
            path_identity(&short.join(&suffix)),
            path_identity(&canonical.join(&suffix))
        );
        assert_eq!(
            canonical_path_with_missing(&short.join(&suffix)).unwrap(),
            canonical.join(&suffix)
        );
        assert!(!root.join("Missing Game").exists());
        assert!(path_identity(&short.join("../outside")).is_none());
        assert!(path_identity(Path::new("relative/Game.exe")).is_none());
        let file = root.join("existing-file");
        fs::write(&file, b"fixture").unwrap();
        assert!(path_identity(&file.join("child")).is_none());
        assert_ne!(
            path_identity(&root.join("one")),
            path_identity(&root.join("two"))
        );
    }
    #[test]
    fn rejects_windows_path_escapes_and_device_names() {
        for p in [
            "../x.exe",
            "a/../x",
            "C:\\evil.exe",
            "\\\\host\\x",
            "x:stream",
            "CON.txt",
            "a/COM1",
            "a./x",
            "x\0.exe",
        ] {
            assert!(relative_path(p).is_err(), "{p}");
        }
        assert_eq!(
            relative_path(r"Game\Игра.exe").unwrap(),
            PathBuf::from("Game").join("Игра.exe")
        );
    }
    #[test]
    fn atomic_replacement_preserves_complete_json() {
        let tmp = tempfile::tempdir().unwrap();
        let p = tmp.path().join("data.json");
        atomic_write(&p, b"first").unwrap();
        atomic_write(&p, b"second").unwrap();
        assert_eq!(fs::read(p).unwrap(), b"second");
    }
}
