//! Small, local-only support for the two auxiliary executables.
use crate::manifest::{DirectoryGuard, is_reparse};
use serde::Serialize;
use std::{
    fs::{self, File, OpenOptions},
    io::Write,
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
};

const MAX_LOG_BYTES: u64 = 512 * 1024;
const KEEP_LOGS: usize = 10;

#[derive(Clone, Copy)]
pub enum HelperKind {
    Updater,
    Security,
}
impl HelperKind {
    fn name(self) -> &'static str {
        match self {
            Self::Updater => "updater",
            Self::Security => "security",
        }
    }
}

struct LogFile {
    file: File,
    bytes: u64,
    _guard: DirectoryGuard,
}
#[derive(Clone)]
pub struct HelperLog {
    path: PathBuf,
    inner: Arc<Mutex<LogFile>>,
}
impl HelperLog {
    pub fn open(kind: HelperKind) -> Result<Self, String> {
        Self::with_fallback(
            crate::diagnostics::log_data_directory().as_deref(),
            &std::env::temp_dir(),
            kind,
        )
    }

    fn with_fallback(data: Option<&Path>, temp: &Path, kind: HelperKind) -> Result<Self, String> {
        let preferred = data
            .map(|p| Self::in_directory(p, kind))
            .unwrap_or_else(|| Err("Не найдена папка настроек".into()));
        match preferred {
            Ok(log) => Ok(log),
            Err(primary_error) => {
                let fallback = crate::diagnostics::fallback_log_data(data, temp)
                    .map_err(|error| format!("{primary_error}; резервный журнал: {error}"))?;
                let log = Self::in_directory(&fallback, kind).map_err(|error| {
                    format!("Основной журнал недоступен: {primary_error}; резервный: {error}")
                })?;
                // The fallback uses the same held directory handles and fresh file
                // as the primary path, including in the elevated security helper.
                log.record("log.fallback", &primary_error)?;
                Ok(log)
            }
        }
    }

    pub fn in_directory(data: &Path, kind: HelperKind) -> Result<Self, String> {
        let directory = data.join("logs");
        let guard = DirectoryGuard::acquire(&directory, true).map_err(|e| e.to_string())?;
        let prefix = format!("{}-", kind.name());
        let mut previous: Vec<_> = fs::read_dir(&directory)
            .map_err(|e| e.to_string())?
            .filter_map(Result::ok)
            .filter(|entry| {
                let name = entry.file_name();
                let name = name.to_string_lossy();
                name.strip_prefix(&prefix)
                    .and_then(|s| s.strip_suffix(".jsonl"))
                    .is_some_and(|s| uuid::Uuid::parse_str(s).is_ok())
                    && fs::symlink_metadata(entry.path())
                        .is_ok_and(|m| m.is_file() && !is_reparse(&m))
            })
            .collect();
        previous.sort_by_key(|entry| entry.metadata().and_then(|m| m.modified()).ok());
        let remove_count = previous.len().saturating_sub(KEEP_LOGS - 1);
        for entry in previous.into_iter().take(remove_count) {
            // Delete a directory entry only; never truncate an existing file.
            let _ = fs::remove_file(entry.path());
        }
        let path = directory.join(format!("{prefix}{}.jsonl", uuid::Uuid::new_v4()));
        // This helper may be elevated. create_new rejects pre-existing hardlinks,
        // while DirectoryGuard prevents swapping an ancestor for a junction.
        let mut options = OpenOptions::new();
        options.write(true).create_new(true);
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            // Retention cannot delete a still-active helper log.
            options.share_mode(1);
        }
        let file = options.open(&path).map_err(|e| e.to_string())?;
        Ok(Self {
            path,
            inner: Arc::new(Mutex::new(LogFile {
                file,
                bytes: 0,
                _guard: guard,
            })),
        })
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn record(&self, event: &str, message: &str) -> Result<(), String> {
        #[derive(Serialize)]
        struct Entry<'a> {
            at: String,
            event: &'a str,
            message: String,
        }
        let mut bytes = serde_json::to_vec(&Entry {
            at: chrono::Utc::now().to_rfc3339(),
            event,
            message: message.chars().take(4000).collect(),
        })
        .map_err(|e| e.to_string())?;
        bytes.push(b'\n');
        let mut state = self.inner.lock().map_err(|_| "Журнал недоступен")?;
        if state.bytes + bytes.len() as u64 > MAX_LOG_BYTES {
            return Err("Достигнут размер журнала".into());
        }
        state.file.write_all(&bytes).map_err(|e| e.to_string())?;
        state.file.flush().map_err(|e| e.to_string())?;
        state.bytes += bytes.len() as u64;
        Ok(())
    }
}

pub fn caught<T>(work: impl FnOnce() -> Result<T, String>) -> Result<T, String> {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(work)).unwrap_or_else(|panic| {
        let text = panic
            .downcast_ref::<String>()
            .map(String::as_str)
            .or_else(|| panic.downcast_ref::<&str>().copied())
            .unwrap_or("неизвестная ошибка");
        Err(format!(
            "Операция остановлена из-за внутренней ошибки: {}",
            text.chars().take(1000).collect::<String>()
        ))
    })
}

pub fn select_renderer() -> Result<(), String> {
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name("software".into())
        .select()
        .map_err(|e| e.to_string())
}

pub fn record_result(log: &Result<HelperLog, String>, result: &Result<(), String>) -> String {
    match log {
        Ok(log) => {
            let message = result
                .as_ref()
                .err()
                .map(String::as_str)
                .unwrap_or("Операция завершена");
            match log.record(if result.is_ok() { "complete" } else { "error" }, message) {
                Ok(()) => format!("Подробности: {}", log.path().display()),
                Err(error) => format!(
                    "Не удалось дописать журнал {}: {error}",
                    log.path().display()
                ),
            }
        }
        Err(error) => format!("Журнал недоступен: {error}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn both_helpers_fall_back_to_injected_temp_and_report_the_real_log() {
        let temp = tempfile::tempdir().unwrap();
        let data = temp.path().join("blocked-roaming");
        fs::write(&data, b"profile unchanged").unwrap();
        let fallback = temp.path().join("user-temp");
        for kind in [HelperKind::Updater, HelperKind::Security] {
            let log = HelperLog::with_fallback(Some(&data), &fallback, kind).unwrap();
            assert!(log.path().starts_with(&fallback));
            let caption = record_result(&Ok(log.clone()), &Err("fixture failure".into()));
            assert!(caption.contains(log.path().to_str().unwrap()));
            let contents = fs::read_to_string(log.path()).unwrap();
            assert!(contents.contains("log.fallback") && contents.contains("fixture failure"));
        }
        assert_eq!(fs::read(&data).unwrap(), b"profile unchanged");
        assert!(!fallback.join("config.json").exists());
        let unknown = HelperLog::with_fallback(None, &fallback, HelperKind::Updater).unwrap();
        assert!(unknown.path().starts_with(&fallback));
    }

    #[test]
    fn helper_fallback_preserves_retention_and_reports_both_location_failures() {
        let temp = tempfile::tempdir().unwrap();
        let data = temp.path().join("blocked-roaming");
        fs::write(&data, b"profile unchanged").unwrap();
        let fallback = temp.path().join("user-temp");
        for _ in 0..12 {
            HelperLog::with_fallback(Some(&data), &fallback, HelperKind::Security).unwrap();
        }
        let fallback_data = crate::diagnostics::fallback_log_data(Some(&data), &fallback).unwrap();
        assert!(fs::read_dir(fallback_data.join("logs")).unwrap().count() <= KEEP_LOGS);
        let blocked = temp.path().join("blocked-temp");
        fs::write(&blocked, b"temp unchanged").unwrap();
        let result = HelperLog::with_fallback(Some(&data), &blocked, HelperKind::Updater);
        let caption = record_result(&result, &Err("fixture operation failure".into()));
        assert!(caption.contains("Основной журнал") && caption.contains("резервный"));
        assert!(!caption.contains("Подробности:"));
        assert_eq!(fs::read(&blocked).unwrap(), b"temp unchanged");
    }

    #[test]
    fn helper_log_is_local_bounded_and_preserves_unrelated_files() {
        let temp = tempfile::tempdir().unwrap();
        for _ in 0..12 {
            HelperLog::in_directory(temp.path(), HelperKind::Updater).unwrap();
        }
        let log = HelperLog::in_directory(temp.path(), HelperKind::Updater).unwrap();
        let sentinel = temp.path().join("logs/launcher.log");
        fs::write(&sentinel, "keep").unwrap();
        for _ in 0..200 {
            let _ = log.record("progress", &"я".repeat(4000));
        }
        let data = fs::read(log.path()).unwrap();
        assert!(data.len() as u64 <= MAX_LOG_BYTES);
        for line in String::from_utf8(data).unwrap().lines() {
            serde_json::from_str::<serde_json::Value>(line).unwrap();
        }
        assert_eq!(fs::read_to_string(&sentinel).unwrap(), "keep");
        assert!(fs::read_dir(temp.path().join("logs")).unwrap().count() <= KEEP_LOGS + 1);
    }
    #[test]
    fn worker_panic_becomes_a_reported_error() {
        let error = caught::<()>(|| panic!("fixture failure")).unwrap_err();
        assert!(error.contains("fixture failure"));
        assert_eq!(caught(|| Ok(7)).unwrap(), 7);
        let temp = tempfile::tempdir().unwrap();
        let log = HelperLog::in_directory(temp.path(), HelperKind::Security).unwrap();
        let caption = record_result(&Ok(log.clone()), &Err(error));
        assert!(caption.contains(log.path().to_str().unwrap()));
        assert!(
            fs::read_to_string(log.path())
                .unwrap()
                .contains("fixture failure")
        );
        assert!(record_result(&Err("fixture io failure".into()), &Ok(())).contains("недоступен"));
    }
}
