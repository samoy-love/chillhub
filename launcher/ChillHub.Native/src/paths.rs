use std::path::{Component, Path, PathBuf};
use std::{fs, io};

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
