//! Refresh the per-user installer entry after a client/game update.
//! Preview runs and portable folders never create an Installed Apps entry.
use std::{
    path::{Path, PathBuf},
    sync::atomic::{AtomicBool, Ordering},
    time::{Duration, Instant},
};
static RUNNING: AtomicBool = AtomicBool::new(false);
const KEY: &str = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\ChillHub";

pub fn refresh(games: &Path, data: &Path) {
    if std::env::var_os("CHILLHUB_DATA_DIR").is_some() || RUNNING.swap(true, Ordering::AcqRel) {
        return;
    }
    let games = games.to_owned();
    let data = data.to_owned();
    if std::thread::Builder::new()
        .name("chillhub-installed-size".into())
        .spawn(move || {
            struct Reset;
            impl Drop for Reset {
                fn drop(&mut self) {
                    RUNNING.store(false, Ordering::Release);
                }
            }
            let _reset = Reset;
            if let Err(error) = refresh_entry(&games) {
                crate::diagnostics::log(&data, "installed-apps", &error);
            }
        })
        .is_err()
    {
        RUNNING.store(false, Ordering::Release);
    }
}

fn install_directory() -> Option<PathBuf> {
    let exe = std::env::current_exe().ok()?;
    let install = exe.parent()?;
    // The setup program places Uninstall.exe only in an installed copy. Keep
    // supporting custom install directories and restoration of a missing key.
    if !install.join("Uninstall.exe").is_file()
        || crate::manifest::check_no_links(&install.join("Uninstall.exe")).is_err()
    {
        return None;
    }
    Some(install.to_owned())
}

fn refresh_entry(games: &Path) -> Result<(), String> {
    use winreg::{RegKey, enums::HKEY_CURRENT_USER};
    let Some(install) = install_directory() else {
        return Ok(());
    };
    let (key, _) = RegKey::predef(HKEY_CURRENT_USER)
        .create_subkey(KEY)
        .map_err(|e| e.to_string())?;
    for (name, value) in [
        ("DisplayName", "Chill Hub"),
        ("Publisher", "Chill Hub"),
        ("URLInfoAbout", crate::config::DEFAULT_API_BASE_URL),
        ("HelpLink", crate::config::DEFAULT_API_BASE_URL),
    ] {
        key.set_value(name, &value).map_err(|e| e.to_string())?;
    }
    key.set_value(
        "UninstallString",
        &format!("\"{}\"", install.join("Uninstall.exe").display()),
    )
    .map_err(|e| e.to_string())?;
    for name in ["NoModify", "NoRepair"] {
        key.set_value(name, &1_u32).map_err(|e| e.to_string())?;
    }
    if key.get_value::<String, _>("InstallDate").is_err() {
        key.set_value(
            "InstallDate",
            &chrono::Local::now().format("%Y%m%d").to_string(),
        )
        .map_err(|e| e.to_string())?;
    }
    key.set_value("DisplayVersion", &env!("CARGO_PKG_VERSION"))
        .map_err(|e| e.to_string())?;
    key.set_value(
        "DisplayIcon",
        &install.join("ChillHub.exe").to_string_lossy().as_ref(),
    )
    .map_err(|e| e.to_string())?;
    key.set_value("InstallLocation", &install.to_string_lossy().as_ref())
        .map_err(|e| e.to_string())?;
    let deadline = Instant::now() + Duration::from_secs(20);
    if let Some(bytes) = total_size(&install, games, deadline) {
        key.set_value("EstimatedSize", &to_kib(bytes))
            .map_err(|e| e.to_string())?;
    }
    // The uninstaller uses this path even when settings have changed since setup.
    let (app, _) = RegKey::predef(HKEY_CURRENT_USER)
        .create_subkey(r"Software\ChillHub\Install")
        .map_err(|e| e.to_string())?;
    app.set_value("GamesDir", &games.to_string_lossy().as_ref())
        .map_err(|e| e.to_string())?;
    Ok(())
}

fn to_kib(bytes: u64) -> u32 {
    bytes.div_ceil(1024).min(i32::MAX as u64) as u32
}
fn size(root: &Path, deadline: Instant) -> Option<u64> {
    if !root.exists() {
        return Some(0);
    }
    crate::manifest::check_no_links(root).ok()?;
    let mut bytes = 0_u64;
    for entry in walkdir::WalkDir::new(root).follow_links(false) {
        if Instant::now() >= deadline {
            return None;
        }
        let entry = entry.ok()?;
        if entry.file_type().is_file() {
            bytes = bytes.saturating_add(entry.metadata().ok()?.len());
        }
    }
    Some(bytes)
}
fn total_size(install: &Path, games: &Path, deadline: Instant) -> Option<u64> {
    let install_root = std::fs::canonicalize(install).ok()?;
    let game_root = std::fs::canonicalize(games).ok();
    if game_root
        .as_ref()
        .is_some_and(|p| install_root.starts_with(p))
    {
        return size(games, deadline);
    }
    let nested = game_root.is_some_and(|p| p.starts_with(&install_root));
    let bytes = size(install, deadline)?;
    Some(if nested {
        bytes
    } else {
        bytes.saturating_add(size(games, deadline)?)
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn counts_games_once_and_never_publishes_partial_size() {
        let root = tempfile::tempdir().unwrap();
        let games = root.path().join("games");
        std::fs::create_dir(&games).unwrap();
        std::fs::write(root.path().join("launcher"), [0_u8; 100]).unwrap();
        std::fs::write(games.join("game"), [0_u8; 100]).unwrap();
        assert_eq!(
            total_size(root.path(), &games, Instant::now() + Duration::from_secs(2)),
            Some(200)
        );
        assert_eq!(total_size(root.path(), &games, Instant::now()), None);
        assert_eq!(to_kib(1025), 2);
        assert_eq!(to_kib(u64::MAX), i32::MAX as u32);
    }
}
