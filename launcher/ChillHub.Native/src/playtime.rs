use crate::{
    config::LaunchTarget,
    paths::{AppPaths, atomic_write},
};
use chrono::{DateTime, Local, Utc};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, HashSet},
    fs,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
    time::Duration,
};

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct PlaytimeEntry {
    pub total_seconds: i64,
    pub last_session_at: Option<DateTime<Utc>>,
    pub last_session_seconds: i64,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct PendingSession {
    pub game_id: String,
    #[serde(default)]
    pub target: Option<String>,
    pub process_id: u32,
    pub process_start_time_ticks: i64,
    pub session_start_utc: DateTime<Utc>,
    #[serde(default)]
    pub modded_dir: Option<String>,
    #[serde(default)]
    pub process_creation_file_time: Option<u64>,
    #[serde(default)]
    pub last_observed_utc: Option<DateTime<Utc>>,
}

#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ProcessIdentity {
    pub pid: u32,
    pub creation_file_time: u64,
    pub legacy_start_ticks: i64,
    pub exe_path: PathBuf,
}

impl ProcessIdentity {
    pub fn capture(pid: u32) -> Result<Self, String> {
        capture_process(pid)
    }
    pub fn is_running(&self) -> bool {
        Self::capture(self.pid).is_ok_and(|p| p.creation_file_time == self.creation_file_time)
    }
}

#[derive(Clone, Debug)]
pub struct SessionHandle {
    pub game_id: String,
    pub target: LaunchTarget,
    pub process: ProcessIdentity,
    pub warning: Option<String>,
}

#[derive(Clone)]
pub struct PlaytimeStore {
    data_dir: PathBuf,
    watching: Arc<Mutex<HashSet<(u32, u64)>>>,
}

impl PlaytimeStore {
    pub fn new(paths: &AppPaths) -> Self {
        Self {
            data_dir: paths.data_dir.clone(),
            watching: Arc::new(Mutex::new(HashSet::new())),
        }
    }

    fn locked<T>(&self, operation: impl FnOnce() -> Result<T, String>) -> Result<T, String> {
        fs::create_dir_all(&self.data_dir).map_err(|e| e.to_string())?;
        let lock = fs::OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(self.data_dir.join("playtime.lock"))
            .map_err(|e| e.to_string())?;
        fs2::FileExt::lock_exclusive(&lock).map_err(|e| e.to_string())?;
        self.recover_commit_locked()?;
        operation()
    }

    pub fn entries(&self) -> Result<BTreeMap<String, PlaytimeEntry>, String> {
        self.locked(|| read_map(&self.data_dir.join("playtime.json")))
    }

    pub fn totals(&self, game_id: &str) -> Result<PlaytimeEntry, String> {
        let mut total = PlaytimeEntry::default();
        for (key, entry) in self.entries()? {
            if !key
                .split('#')
                .next()
                .unwrap_or("")
                .eq_ignore_ascii_case(game_id)
            {
                continue;
            }
            total.total_seconds = total
                .total_seconds
                .saturating_add(entry.total_seconds.max(0));
            if entry.last_session_at > total.last_session_at {
                total.last_session_at = entry.last_session_at;
                total.last_session_seconds = entry.last_session_seconds;
            }
        }
        Ok(total)
    }

    pub fn pending(&self) -> Result<BTreeMap<String, PendingSession>, String> {
        self.locked(|| read_map(&self.data_dir.join("playtime.sessions.json")))
    }

    pub fn begin(
        &self,
        game_id: &str,
        target: LaunchTarget,
        process: ProcessIdentity,
        modded_dir: Option<&Path>,
    ) -> Result<SessionHandle, String> {
        crate::paths::validate_game_id(game_id)?;
        let session = SessionHandle {
            game_id: game_id.into(),
            target,
            process: process.clone(),
            warning: None,
        };
        self.locked(|| {
            let path = self.data_dir.join("playtime.sessions.json");
            let mut pending: BTreeMap<String, PendingSession> = read_map(&path)?;
            let key = process.pid.to_string();
            if let Some(old) = pending.get(&key) {
                if matches_process(old, &process) {
                    return Ok(());
                }
                return Err("Старая игровая сессия ещё не обработана".into());
            }
            pending.insert(
                key,
                PendingSession {
                    game_id: game_id.into(),
                    target: Some(target.as_str().into()),
                    process_id: process.pid,
                    process_start_time_ticks: process.legacy_start_ticks,
                    session_start_utc: Utc::now(),
                    modded_dir: modded_dir.map(|p| p.to_string_lossy().into_owned()),
                    process_creation_file_time: Some(process.creation_file_time),
                    last_observed_utc: Some(Utc::now()),
                },
            );
            write_json(&path, &pending)
        })?;
        self.watch(process);
        Ok(session)
    }

    /// Resume sessions from both the C# and native formats; PID reuse never attaches an unrelated process.
    pub fn reconcile(&self) -> Result<Vec<SessionHandle>, String> {
        self.recover_commit()?;
        let mut resumed = Vec::new();
        for session in self.pending()?.into_values() {
            match ProcessIdentity::capture(session.process_id) {
                Ok(process) if matches_process(&session, &process) => {
                    if let Some(target) = session.target.as_deref().and_then(LaunchTarget::parse) {
                        resumed.push(SessionHandle {
                            game_id: session.game_id.clone(),
                            target,
                            process: process.clone(),
                            warning: None,
                        });
                    }
                    self.watch(process);
                }
                _ => {
                    // Old C# files have no heartbeat; retain their existing reconciliation behavior.
                    let end = session.last_observed_utc.unwrap_or_else(Utc::now);
                    self.finish(session.process_id, session.process_creation_file_time, end)?;
                }
            }
        }
        Ok(resumed)
    }

    fn watch(&self, process: ProcessIdentity) {
        let key = (process.pid, process.creation_file_time);
        if !self
            .watching
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .insert(key)
        {
            return;
        }
        let store = self.clone();
        std::thread::spawn(move || {
            let mut elapsed = 0;
            while process.is_running() {
                std::thread::sleep(Duration::from_secs(1));
                elapsed += 1;
                if elapsed % 15 == 0 {
                    let _ = store.locked(|| {
                        let path = store.data_dir.join("playtime.sessions.json");
                        let mut all: BTreeMap<String, PendingSession> = read_map(&path)?;
                        if let Some(s) = all
                            .get_mut(&process.pid.to_string())
                            .filter(|s| matches_process(s, &process))
                        {
                            s.last_observed_utc = Some(Utc::now());
                            write_json(&path, &all)?;
                        }
                        Ok(())
                    });
                }
            }
            let _ = store.finish(process.pid, Some(process.creation_file_time), Utc::now());
            store
                .watching
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .remove(&key);
        });
    }

    fn finish(&self, pid: u32, creation: Option<u64>, end: DateTime<Utc>) -> Result<(), String> {
        self.locked(|| {
            let pending_path = self.data_dir.join("playtime.sessions.json");
            let mut pending: BTreeMap<String, PendingSession> = read_map(&pending_path)?;
            let Some(session) = pending.get(&pid.to_string()) else {
                return Ok(());
            };
            if creation.is_some()
                && session.process_creation_file_time.is_some()
                && creation != session.process_creation_file_time
            {
                return Ok(());
            }
            let session = session.clone();
            let all_path = self.data_dir.join("playtime.json");
            let mut all: BTreeMap<String, PlaytimeEntry> = read_map(&all_path)?;
            let target = session.target.as_deref().and_then(LaunchTarget::parse);
            let bucket = target
                .map(|t| format!("{}#{}", session.game_id, t.as_str()))
                .unwrap_or_else(|| session.game_id.clone());
            let entry = all.entry(bucket).or_default();
            let elapsed = (end - session.session_start_utc).num_seconds().max(0);
            entry.total_seconds = entry.total_seconds.saturating_add(elapsed);
            entry.last_session_at = Some(end);
            entry.last_session_seconds = elapsed;
            // A small journal makes the two-file commit idempotent after a power interruption.
            pending.remove(&pid.to_string());
            let restore = session.modded_dir.into_iter().collect::<Vec<_>>();
            let journal = serde_json::json!({"Playtime": all, "Pending": pending, "RestoreModdedDirs": restore});
            let journal_path = self.data_dir.join("playtime.commit.json");
            write_json(&journal_path, &journal)?;
            write_json(&all_path, &all)?;
            write_json(&pending_path, &pending)?;
            // Keep the commit journal until loader cleanup finishes as well.
            // Otherwise a crash after removing Pending permanently loses the
            // only record telling us to restore plain-game behaviour.
            restore_session_loaders(&journal)?;
            fs::remove_file(journal_path).map_err(|e| e.to_string())?;
            Ok(())
        })
    }

    pub fn recover_commit(&self) -> Result<(), String> {
        self.locked(|| Ok(()))
    }

    fn recover_commit_locked(&self) -> Result<(), String> {
        let path = self.data_dir.join("playtime.commit.json");
        if !path.exists() {
            return Ok(());
        }
        let journal: serde_json::Value =
            serde_json::from_slice(&fs::read(&path).map_err(|e| e.to_string())?)
                .map_err(|e| e.to_string())?;
        let all: BTreeMap<String, PlaytimeEntry> =
            serde_json::from_value(journal["Playtime"].clone()).map_err(|e| e.to_string())?;
        let pending: BTreeMap<String, PendingSession> =
            serde_json::from_value(journal["Pending"].clone()).map_err(|e| e.to_string())?;
        write_json(&self.data_dir.join("playtime.json"), &all)?;
        write_json(&self.data_dir.join("playtime.sessions.json"), &pending)?;
        restore_session_loaders(&journal)?;
        fs::remove_file(path).map_err(|e| e.to_string())
    }
}

fn restore_session_loaders(journal: &serde_json::Value) -> Result<(), String> {
    let Some(value) = journal.get("RestoreModdedDirs") else {
        return Ok(());
    };
    let directories: Vec<String> =
        serde_json::from_value(value.clone()).map_err(|e| e.to_string())?;
    if directories.len() > 16 {
        return Err("Некорректный журнал игровых сессий".into());
    }
    for directory in directories {
        let path = Path::new(&directory);
        if path.is_dir() {
            crate::launch::set_doorstop_enabled(path, false)?;
        }
    }
    Ok(())
}

fn matches_process(session: &PendingSession, process: &ProcessIdentity) -> bool {
    session.process_id == process.pid
        && match session.process_creation_file_time {
            Some(stamp) => stamp == process.creation_file_time,
            None => {
                session.process_start_time_ticks != 0
                    && session.process_start_time_ticks == process.legacy_start_ticks
            }
        }
}

fn read_map<T: serde::de::DeserializeOwned>(path: &Path) -> Result<BTreeMap<String, T>, String> {
    match fs::read(path) {
        Ok(bytes) => {
            serde_json::from_slice(bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes))
                .map_err(|e| format!("{}: {e}", path.display()))
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(BTreeMap::new()),
        Err(e) => Err(e.to_string()),
    }
}
fn write_json(path: &Path, value: &impl Serialize) -> Result<(), String> {
    atomic_write(
        path,
        &serde_json::to_vec_pretty(value).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())
}

#[cfg(windows)]
fn capture_process(pid: u32) -> Result<ProcessIdentity, String> {
    use windows_sys::Win32::{
        Foundation::{CloseHandle, FILETIME, WAIT_TIMEOUT},
        System::Threading::*,
    };
    let handle = unsafe {
        OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
            0,
            pid,
        )
    };
    if handle.is_null() {
        return Err(std::io::Error::last_os_error().to_string());
    }
    let result = (|| {
        if unsafe { WaitForSingleObject(handle, 0) } != WAIT_TIMEOUT {
            return Err("Процесс завершён".into());
        }
        let mut start: FILETIME = unsafe { std::mem::zeroed() };
        let mut exit = start;
        let mut kernel = start;
        let mut user = start;
        if unsafe { GetProcessTimes(handle, &mut start, &mut exit, &mut kernel, &mut user) } == 0 {
            return Err(std::io::Error::last_os_error().to_string());
        }
        let creation_file_time = ((start.dwHighDateTime as u64) << 32) | start.dwLowDateTime as u64;
        let unix_100ns = creation_file_time as i64 - 116_444_736_000_000_000;
        let utc = DateTime::from_timestamp(
            unix_100ns.div_euclid(10_000_000),
            (unix_100ns.rem_euclid(10_000_000) * 100) as u32,
        )
        .ok_or("Неверное время процесса")?;
        let local: DateTime<Local> = utc.into();
        let legacy_start_ticks = unix_100ns
            + 621_355_968_000_000_000
            + i64::from(local.offset().local_minus_utc()) * 10_000_000;
        let mut buffer = vec![0u16; 32768];
        let mut length = buffer.len() as u32;
        if unsafe { QueryFullProcessImageNameW(handle, 0, buffer.as_mut_ptr(), &mut length) } == 0 {
            return Err(std::io::Error::last_os_error().to_string());
        }
        Ok(ProcessIdentity {
            pid,
            creation_file_time,
            legacy_start_ticks,
            exe_path: String::from_utf16_lossy(&buffer[..length as usize]).into(),
        })
    })();
    unsafe {
        CloseHandle(handle);
    }
    result
}
#[cfg(not(windows))]
fn capture_process(_: u32) -> Result<ProcessIdentity, String> {
    Err("Запуск игр поддерживается в Windows".into())
}

pub fn processes_in(root: &Path) -> Result<Vec<ProcessIdentity>, String> {
    let canonical_root = fs::canonicalize(root).map_err(|e| e.to_string())?;
    let mut result = Vec::new();
    for pid in process_ids()? {
        if let Ok(p) = ProcessIdentity::capture(pid)
            && fs::canonicalize(&p.exe_path).is_ok_and(|exe| exe.starts_with(&canonical_root))
        {
            result.push(p);
        }
    }
    Ok(result)
}

#[cfg(windows)]
fn process_ids() -> Result<Vec<u32>, String> {
    use windows_sys::Win32::{
        Foundation::{CloseHandle, INVALID_HANDLE_VALUE},
        System::Diagnostics::ToolHelp::*,
    };
    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
    if snapshot == INVALID_HANDLE_VALUE {
        return Err(std::io::Error::last_os_error().to_string());
    }
    let mut entry: PROCESSENTRY32W = unsafe { std::mem::zeroed() };
    entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
    let mut ids = Vec::new();
    let mut more = unsafe { Process32FirstW(snapshot, &mut entry) };
    while more != 0 {
        ids.push(entry.th32ProcessID);
        more = unsafe { Process32NextW(snapshot, &mut entry) };
    }
    unsafe {
        CloseHandle(snapshot);
    }
    Ok(ids)
}
#[cfg(not(windows))]
fn process_ids() -> Result<Vec<u32>, String> {
    Ok(Vec::new())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn preserves_legacy_and_variant_buckets() {
        let temp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(temp.path());
        fs::create_dir_all(&paths.data_dir).unwrap();
        fs::write(paths.data_dir.join("playtime.json"), r#"{"peak":{"TotalSeconds":20},"peak#SteamVanilla":{"TotalSeconds":10},"peak-other":{"TotalSeconds":999}}"#).unwrap();
        assert_eq!(
            PlaytimeStore::new(&paths)
                .totals("PEAK")
                .unwrap()
                .total_seconds,
            30
        );
    }
    #[test]
    fn pid_reuse_does_not_match() {
        let p = ProcessIdentity {
            pid: 5,
            creation_file_time: 20,
            legacy_start_ticks: 100,
            exe_path: "x.exe".into(),
        };
        let mut s = PendingSession {
            game_id: "peak".into(),
            target: None,
            process_id: 5,
            process_start_time_ticks: 100,
            session_start_utc: Utc::now(),
            modded_dir: None,
            process_creation_file_time: Some(19),
            last_observed_utc: None,
        };
        assert!(!matches_process(&s, &p));
        s.process_creation_file_time = None;
        assert!(matches_process(&s, &p));
    }
    #[test]
    fn finish_is_idempotent_and_disables_mods() {
        let temp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(temp.path());
        let store = PlaytimeStore::new(&paths);
        fs::create_dir_all(&paths.data_dir).unwrap();
        let game = paths.game_root("peak").unwrap();
        fs::create_dir_all(&game).unwrap();
        fs::write(
            game.join("doorstop_config.ini"),
            "[General]\nenabled = true\n",
        )
        .unwrap();
        let start = Utc::now();
        let session = PendingSession {
            game_id: "peak".into(),
            target: Some("SteamModded".into()),
            process_id: 999,
            process_start_time_ticks: 1,
            session_start_utc: start,
            modded_dir: Some(game.to_string_lossy().into_owned()),
            process_creation_file_time: Some(1),
            last_observed_utc: None,
        };
        write_json(
            &paths.data_dir.join("playtime.sessions.json"),
            &BTreeMap::from([("999".to_string(), session)]),
        )
        .unwrap();
        store
            .finish(999, Some(1), start + chrono::Duration::seconds(40))
            .unwrap();
        store.finish(999, Some(1), Utc::now()).unwrap();
        assert_eq!(store.totals("peak").unwrap().total_seconds, 40);
        assert!(
            fs::read_to_string(game.join("doorstop_config.ini"))
                .unwrap()
                .contains("false")
        );
    }

    #[test]
    fn interrupted_commit_restores_loader_before_forgetting_the_session() {
        let temp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(temp.path());
        let store = PlaytimeStore::new(&paths);
        let game = paths.game_root("peak").unwrap();
        fs::create_dir_all(&game).unwrap();
        fs::write(
            game.join("doorstop_config.ini"),
            "[General]\nenabled=true\n",
        )
        .unwrap();
        let journal = serde_json::json!({
            "Playtime":{"peak#SteamModded":{"TotalSeconds":40,"LastSessionSeconds":40}},
            "Pending":{},"RestoreModdedDirs":[game.to_string_lossy()]
        });
        write_json(&paths.data_dir.join("playtime.commit.json"), &journal).unwrap();
        store.recover_commit().unwrap();
        store.recover_commit().unwrap();
        assert_eq!(store.totals("peak").unwrap().total_seconds, 40);
        assert!(!paths.data_dir.join("playtime.commit.json").exists());
        assert!(
            fs::read_to_string(game.join("doorstop_config.ini"))
                .unwrap()
                .contains("false")
        );
    }
}
