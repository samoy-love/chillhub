use crate::playtime::{ProcessIdentity, processes_in};
use std::{
    path::Path,
    time::{Duration, Instant},
};

#[derive(Clone, Copy)]
pub(super) struct SteamWait {
    pub report_after: Duration,
    pub watch_after: Duration,
    pub interval: Duration,
}

impl Default for SteamWait {
    fn default() -> Self {
        Self {
            report_after: Duration::from_secs(3 * 60),
            watch_after: Duration::from_secs(3 * 60),
            interval: Duration::from_secs(1),
        }
    }
}

pub(super) fn find_game_process(
    root: &Path,
    game_executable: &Path,
) -> Result<Option<ProcessIdentity>, String> {
    // processes_in canonicalizes each image and checks the game directory
    // boundary. Steam can choose another subfolder of that same installation.
    Ok(processes_in(root)?
        .into_iter()
        .find(|process| same_executable_name(&process.exe_path, game_executable)))
}

pub(super) fn same_executable_name(candidate: &Path, expected: &Path) -> bool {
    let (Some(candidate), Some(expected)) = (candidate.file_name(), expected.file_name()) else {
        return false;
    };
    candidate.eq_ignore_ascii_case(expected)
        || candidate.to_string_lossy().to_lowercase() == expected.to_string_lossy().to_lowercase()
}

pub(super) fn wait_for_game<T>(
    deadline: Instant,
    interval: Duration,
    mut find: impl FnMut() -> Option<T>,
) -> Option<T> {
    loop {
        if let Some(process) = find() {
            return Some(process);
        }
        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return None;
        }
        std::thread::sleep(remaining.min(interval));
    }
}
