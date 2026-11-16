use super::{set_doorstop_enabled, steam_handoff::SteamWait};
use crate::{
    diagnostics,
    manifest::{DirectoryGuard, open_regular},
    paths::{atomic_write, contained_path},
    playtime::processes_in,
};
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::{
    fs::{self, File, OpenOptions},
    io::{self, ErrorKind, Read},
    path::{Path, PathBuf},
    time::{Duration, Instant},
};

const JOURNAL_SUFFIX: &str = ".loader.json";
const MAX_JOURNAL_BYTES: u64 = 256 * 1024;

#[derive(Serialize, Deserialize)]
struct LoaderJournal {
    game_root: PathBuf,
    wait_for_steam_until: Option<DateTime<Utc>>,
}

pub(super) struct LaunchLocks {
    root: PathBuf,
    journal: PathBuf,
    _journal_directory: DirectoryGuard,
    _launch: File,
    _operation: Option<File>,
}

impl LaunchLocks {
    pub(super) fn acquire(data_dir: &Path, root: &Path, vanilla: bool) -> Result<Self, String> {
        let root = fs::canonicalize(root).map_err(|e| e.to_string())?;
        fs::create_dir_all(data_dir).map_err(|e| e.to_string())?;
        let lock_dir = fs::canonicalize(data_dir)
            .map_err(|e| e.to_string())?
            .join("launch-locks");
        let journal_directory =
            DirectoryGuard::acquire(&lock_dir, true).map_err(|e| e.to_string())?;
        let key = copy_key(&root);
        let launch = try_lock(&lock_dir, &key)?.ok_or("Игра уже запускается или запущена")?;
        Self::with_launch_lock(
            root,
            lock_dir.join(format!("{key}{JOURNAL_SUFFIX}")),
            vanilla,
            launch,
            journal_directory,
        )
    }

    fn with_launch_lock(
        root: PathBuf,
        journal: PathBuf,
        vanilla: bool,
        launch: File,
        journal_directory: DirectoryGuard,
    ) -> Result<Self, String> {
        let operation_path = contained_path(&root, Path::new(".chillhub.lock"))?;
        let operation = match open_operation_lock(&operation_path) {
            Ok(file) => {
                fs2::FileExt::try_lock_exclusive(&file)
                    .map_err(|_| "Файлы игры сейчас обновляются или игра уже запущена")?;
                Some(file)
            }
            Err(error)
                if error.kind() == ErrorKind::PermissionDenied
                    && vanilla
                    && operation_path.try_exists().is_ok_and(|exists| !exists)
                    && root
                        .join("doorstop_config.ini")
                        .try_exists()
                        .is_ok_and(|exists| !exists) =>
            {
                None
            }
            Err(error) => {
                return Err(format!(
                    "Не удалось защитить файлы игры на время запуска: {error}"
                ));
            }
        };
        Ok(Self {
            root,
            journal,
            _journal_directory: journal_directory,
            _launch: launch,
            _operation: operation,
        })
    }

    pub(super) fn prepare_loader(
        &self,
        modded: bool,
        wait_for_steam_until: Option<DateTime<Utc>>,
    ) -> Result<(), String> {
        if !modded {
            return self.restore_loader();
        }
        // Record the recovery obligation before enabling Doorstop. Playtime
        // can fail independently, including before a Steam process exists.
        let journal = LoaderJournal {
            game_root: self.root.clone(),
            wait_for_steam_until,
        };
        let bytes = serde_json::to_vec(&journal).map_err(|e| e.to_string())?;
        atomic_write(&self.journal, &bytes).map_err(|e| e.to_string())?;
        set_doorstop_enabled(&self.root, true)
    }

    pub(super) fn restore_loader(&self) -> Result<(), String> {
        set_doorstop_enabled(&self.root, false)?;
        match fs::remove_file(&self.journal) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == ErrorKind::NotFound => Ok(()),
            Err(error) => Err(error.to_string()),
        }
    }
}

fn copy_key(root: &Path) -> String {
    blake3::hash(root.to_string_lossy().to_lowercase().as_bytes())
        .to_hex()
        .to_string()
}

fn try_lock(lock_dir: &Path, key: &str) -> Result<Option<File>, String> {
    let file = match open_regular(&lock_dir.join(format!("{key}.lock")), true, true, false) {
        Ok(file) => file,
        // open_regular denies concurrent writers on Windows.
        #[cfg(windows)]
        Err(error) if error.raw_os_error() == Some(32) => return Ok(None),
        Err(error) => return Err(error.to_string()),
    };
    match fs2::FileExt::try_lock_exclusive(&file) {
        Ok(()) => Ok(Some(file)),
        Err(error) if error.raw_os_error() == fs2::lock_contended_error().raw_os_error() => {
            Ok(None)
        }
        Err(error) => Err(error.to_string()),
    }
}

fn open_operation_lock(path: &Path) -> io::Result<File> {
    match open_regular(path, false, false, false) {
        Err(error) if error.kind() == ErrorKind::NotFound => {
            match OpenOptions::new()
                .create_new(true)
                .read(true)
                .write(true)
                .open(path)
            {
                Err(error) if error.kind() == ErrorKind::AlreadyExists => {
                    open_regular(path, false, false, false)
                }
                result => result,
            }
        }
        result => result,
    }
}

pub(super) fn recover_loader_sessions(data_dir: &Path) -> Result<(), String> {
    let lock_dir = match fs::canonicalize(data_dir) {
        Ok(directory) => directory.join("launch-locks"),
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error.to_string()),
    };
    let _directory = DirectoryGuard::acquire(&lock_dir, false).map_err(|e| e.to_string())?;
    let entries = match fs::read_dir(&lock_dir) {
        Ok(entries) => entries,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error.to_string()),
    };
    let mut errors = Vec::new();
    for entry in entries {
        let entry = entry.map_err(|e| e.to_string())?;
        let filename = entry.file_name();
        let Some(key) = filename
            .to_str()
            .and_then(|name| name.strip_suffix(JOURNAL_SUFFIX))
        else {
            continue;
        };
        if key.len() != 64 || !key.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            continue;
        }
        if let Err(error) = recover_loader_session(data_dir, &lock_dir, key, entry.path()) {
            errors.push(error);
        }
    }
    if errors.is_empty() {
        Ok(())
    } else {
        Err(errors.join("\n"))
    }
}

fn recover_loader_session(
    data_dir: &Path,
    lock_dir: &Path,
    key: &str,
    path: PathBuf,
) -> Result<(), String> {
    let Some(launch) = try_lock(lock_dir, key)? else {
        return Ok(());
    };
    // Opening for update rejects hardlinks. This handle only reads the
    // journal, and is closed before recovery removes it.
    let file = match open_regular(&path, true, false, false) {
        Ok(file) => file,
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error.to_string()),
    };
    let mut bytes = Vec::new();
    file.take(MAX_JOURNAL_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() as u64 > MAX_JOURNAL_BYTES {
        return Err("Журнал загрузчика модов слишком большой".into());
    }
    let journal: LoaderJournal = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
    let root = match fs::canonicalize(&journal.game_root) {
        Ok(root) => root,
        // The game may be on a removable drive. Retain the record until the
        // directory is accessible again.
        Err(error) if error.kind() == ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error.to_string()),
    };
    if copy_key(&root) != key {
        return Err("Некорректный журнал загрузчика модов".into());
    }
    let journal_directory = DirectoryGuard::acquire(lock_dir, false).map_err(|e| e.to_string())?;
    let locks = LaunchLocks::with_launch_lock(root, path, false, launch, journal_directory)?;
    let running = !processes_in(&locks.root)?.is_empty();
    let maximum_wait = SteamWait::default();
    let remaining = journal
        .wait_for_steam_until
        .and_then(|until| (until - Utc::now()).to_std().ok())
        .unwrap_or_default()
        .min(maximum_wait.report_after + maximum_wait.watch_after);
    if !running && remaining.is_zero() {
        return locks.restore_loader();
    }

    let deadline = Instant::now() + remaining;
    let data_dir = data_dir.to_path_buf();
    std::thread::spawn(move || {
        let mut has_run = running;
        loop {
            std::thread::sleep(Duration::from_secs(1));
            match processes_in(&locks.root) {
                Ok(processes) if !processes.is_empty() => has_run = true,
                Ok(_) if has_run || Instant::now() >= deadline => {
                    if let Err(error) = locks.restore_loader() {
                        diagnostics::log(&data_dir, "loader-recovery", &error);
                    }
                    break;
                }
                Ok(_) => {}
                Err(error) => {
                    diagnostics::log(&data_dir, "loader-recovery", &error);
                    break;
                }
            }
        }
    });
    Ok(())
}
