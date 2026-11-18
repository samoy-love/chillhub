//! Authenticated UAC handoff. The ordinary caller owns the journal and the
//! installation lock until the elevated confirmation/results window has closed.
use super::*;
use fs2::FileExt;

pub struct SecurityLifetime {
    _install: crate::update::ProcessLock,
    _ui: crate::update::ProcessLock,
    _lease: fs::File,
}
impl SecurityLifetime {
    pub fn acquire() -> Result<Self, String> {
        let exe = std::env::current_exe().map_err(|e| e.to_string())?;
        let install = exe.parent().ok_or("Не найдена папка установки")?;
        let install_lock =
            crate::update::ProcessLock::acquire(&crate::update::update_mutex_name(install)?)?;
        let ui = crate::update::ProcessLock::acquire("Local\\ChillHub.SecurityUi")?;
        let lease = open_lease(
            &journal_path()?.with_file_name("security-session.lock"),
            true,
            false,
        )?;
        Ok(Self {
            _install: install_lock,
            _ui: ui,
            _lease: lease,
        })
    }
}

fn open_lease(path: &Path, create: bool, exclusive: bool) -> Result<fs::File, String> {
    let parent = path.parent().ok_or("Не найдена папка сессии")?;
    let _guard =
        crate::manifest::DirectoryGuard::acquire(parent, create).map_err(|e| e.to_string())?;
    crate::manifest::check_no_links(path).map_err(|e| e.to_string())?;
    let file = fs::OpenOptions::new()
        .read(true)
        .write(create)
        .create(create)
        .truncate(false)
        .open(path)
        .map_err(|e| e.to_string())?;
    if exclusive {
        file.try_lock_exclusive()
    } else {
        FileExt::try_lock_shared(&file)
    }
    .map_err(|_| "Сначала завершите настройку защиты или обновление лаунчера")?;
    Ok(file)
}

pub(crate) fn update_lease(config: &Path) -> Result<fs::File, String> {
    open_lease(&config.join("security-session.lock"), true, true)
}

#[derive(Serialize, Deserialize)]
struct Envelope {
    token: String,
    request: SecurityRequest,
    lease_path: PathBuf,
}

fn validate_token(value: &str) -> bool {
    value.len() == 64 && value.bytes().all(|b| b.is_ascii_hexdigit())
}

#[cfg(windows)]
#[path = "security_session/windows.rs"]
mod windows;

#[cfg(windows)]
pub use windows::{ElevatedSession, elevate_helper, elevated_request_from_args};

#[cfg(test)]
#[path = "security_session_tests.rs"]
mod tests;
