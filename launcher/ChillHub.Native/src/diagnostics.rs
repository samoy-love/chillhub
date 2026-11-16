use crate::{net::ApiClient, paths::atomic_write};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    fs,
    io::{self, Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::Mutex,
    time::Duration,
};
static LOG_LOCK: Mutex<()> = Mutex::new(());
static LOG_PATHS: Mutex<BTreeMap<PathBuf, PathBuf>> = Mutex::new(BTreeMap::new());

/// One bounded structured record, with the same rotation and privacy rules as
/// the human-readable log. Callers record state changes, never frame/progress ticks.
pub fn event(data: &Path, name: &str, fields: serde_json::Value) {
    let value = serde_json::json!({"event": name, "fields": fields});
    log(data, "event", &redact(&value.to_string()));
}

pub fn log(data: &Path, kind: &str, message: &str) {
    if !logging_enabled(&std::env::var("CHILLHUB_CLIENT_LOG").unwrap_or_default()) {
        return;
    }
    let _ = log_with_fallback(Some(data), &std::env::temp_dir(), kind, message);
}

/// Startup can fail before AppPaths or the configuration can be loaded. Only
/// this log is redirected; callers must not use its directory for user state.
pub fn startup_log(kind: &str, message: &str) -> Option<PathBuf> {
    if !logging_enabled(&std::env::var("CHILLHUB_CLIENT_LOG").unwrap_or_default()) {
        return None;
    }
    log_with_fallback(
        log_data_directory().as_deref(),
        &std::env::temp_dir(),
        kind,
        message,
    )
    .ok()
}

pub(crate) fn log_data_directory() -> Option<PathBuf> {
    std::env::var_os("CHILLHUB_DATA_DIR")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("APPDATA").map(|p| PathBuf::from(p).join("ChillHub")))
}

/// The explicit temporary root is also the test seam: tests never modify TEMP
/// or write in the real profile. A profile hash keeps isolated runs apart.
pub(crate) fn fallback_log_data(data: Option<&Path>, temp: &Path) -> io::Result<PathBuf> {
    if !temp.is_absolute()
        || temp.components().any(|c| {
            matches!(
                c,
                std::path::Component::ParentDir | std::path::Component::CurDir
            )
        })
    {
        return Err(io::Error::other(
            "Не найдена безопасная временная папка пользователя",
        ));
    }
    let identity = data
        .map(|p| p.as_os_str().as_encoded_bytes())
        .unwrap_or(b"profile-unavailable");
    let hash = blake3::hash(identity).to_hex();
    Ok(temp.join("ChillHub").join(format!("logs-{}", &hash[..16])))
}

/// Last successfully written file, not a guessed preferred destination.
pub fn actual_log_path(data: &Path) -> Option<PathBuf> {
    LOG_PATHS.lock().ok()?.get(data).cloned()
}

/// Opening old logs remains available when new logging is disabled. Resolve
/// only existing safe directories; this action must never create a log/profile.
pub fn existing_log_directory(data: &Path) -> Option<PathBuf> {
    existing_log_directory_at(data, &std::env::temp_dir())
}

fn existing_log_directory_at(data: &Path, temp: &Path) -> Option<PathBuf> {
    let actual = actual_log_path(data);
    let mut first_existing = None;
    let mut newest = None;
    for directory in log_directories_at(data, temp) {
        let Ok(_guard) = crate::manifest::DirectoryGuard::acquire(&directory, false) else {
            continue;
        };
        if !fs::metadata(&directory).is_ok_and(|metadata| metadata.is_dir()) {
            continue;
        }
        if actual.as_ref().is_some_and(|path| {
            path.parent() == Some(directory.as_path())
                && fs::symlink_metadata(path)
                    .is_ok_and(|meta| meta.is_file() && !crate::manifest::is_reparse(&meta))
        }) {
            return Some(directory);
        }
        first_existing.get_or_insert_with(|| directory.clone());
        let Ok(entries) = fs::read_dir(&directory) else {
            continue;
        };
        // At most three candidates and 256 immediate entries per candidate;
        // opening logs never walks a tree or reads arbitrary file contents.
        for entry in entries.take(256).filter_map(Result::ok) {
            if !is_supported_log_name(&entry.file_name().to_string_lossy()) {
                continue;
            }
            let Ok(meta) = fs::symlink_metadata(entry.path()) else {
                continue;
            };
            if !meta.is_file() || crate::manifest::is_reparse(&meta) {
                continue;
            }
            let modified = meta.modified().unwrap_or(std::time::SystemTime::UNIX_EPOCH);
            if newest.as_ref().is_none_or(|(time, _)| modified > *time) {
                newest = Some((modified, directory.clone()));
            }
        }
    }
    newest.map(|(_, directory)| directory).or(first_existing)
}

fn is_supported_log_name(name: &str) -> bool {
    let name = name.to_ascii_lowercase();
    if matches!(
        name.as_str(),
        "launcher.log"
            | "launcher.previous.log"
            | "client.log"
            | "client.1.log"
            | "client.2.log"
            | "client.3.log"
            | "boot.log"
            | "updater.log"
            | "security.log"
            | "security-helper.log"
    ) {
        return true;
    }
    ["updater-", "security-"].into_iter().any(|prefix| {
        name.strip_prefix(prefix)
            .and_then(|suffix| suffix.strip_suffix(".jsonl"))
            .is_some_and(|id| uuid::Uuid::parse_str(id).is_ok())
    })
}

pub(crate) fn log_directories(data: &Path) -> Vec<PathBuf> {
    log_directories_at(data, &std::env::temp_dir())
}

fn log_directories_at(data: &Path, temp: &Path) -> Vec<PathBuf> {
    let mut directories = Vec::new();
    if let Some(path) = actual_log_path(data)
        && let Some(parent) = path.parent()
    {
        directories.push(parent.to_owned());
    }
    for directory in std::iter::once(data.join("logs")).chain(
        fallback_log_data(Some(data), temp)
            .ok()
            .map(|p| p.join("logs")),
    ) {
        if !directories.contains(&directory) {
            directories.push(directory);
        }
    }
    directories
}

fn log_with_fallback(
    data: Option<&Path>,
    temp: &Path,
    kind: &str,
    message: &str,
) -> io::Result<PathBuf> {
    let _lock = LOG_LOCK
        .lock()
        .map_err(|_| io::Error::other("Журнал занят"))?;
    let preferred = data
        .map(|p| write_log(p, kind, message))
        .unwrap_or_else(|| Err(io::Error::other("Не найдена папка настроек пользователя")));
    let path = match preferred {
        Ok(path) => path,
        Err(primary_error) => {
            let fallback = fallback_log_data(data, temp)?;
            write_log(&fallback, kind, message).map_err(|fallback_error| {
                io::Error::other(format!(
                    "Основной журнал недоступен: {primary_error}; резервный: {fallback_error}"
                ))
            })?
        }
    };
    if let Some(data) = data
        && let Ok(mut paths) = LOG_PATHS.lock()
    {
        // A normal process has one profile; bound diagnostic/test callers too.
        if paths.len() >= 64 && !paths.contains_key(data) {
            paths.pop_first();
        }
        paths.insert(data.to_owned(), path.clone());
    }
    Ok(path)
}

fn write_log(data: &Path, kind: &str, message: &str) -> io::Result<PathBuf> {
    let directory = data.join("logs");
    let _guard = crate::manifest::DirectoryGuard::acquire(&directory, true)?;
    let path = directory.join("launcher.log");
    let mut file = crate::manifest::open_regular(&path, true, true, false)?;
    if file.metadata()?.len() > 2 * 1024 * 1024 {
        drop(file);
        let previous = directory.join("launcher.previous.log");
        crate::manifest::check_no_links(&previous)?;
        match fs::remove_file(&previous) {
            Ok(()) => (),
            Err(error) if error.kind() == io::ErrorKind::NotFound => (),
            Err(error) => return Err(error),
        }
        fs::rename(&path, &previous)?;
        file = crate::manifest::open_regular(&path, true, true, false)?;
    }
    file.seek(SeekFrom::End(0))?;
    writeln!(
        file,
        "{} [{kind}] {}",
        chrono::Utc::now().to_rfc3339(),
        redact(message)
            .chars()
            .take(12000)
            .collect::<String>()
            .replace(['\r', '\n'], " ")
    )?;
    file.flush()?;
    Ok(path)
}

fn logging_enabled(value: &str) -> bool {
    !matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "0" | "false" | "off" | "no"
    )
}

pub fn os_version() -> String {
    use winreg::{RegKey, enums::HKEY_LOCAL_MACHINE};
    let key = RegKey::predef(HKEY_LOCAL_MACHINE)
        .open_subkey(r"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
    if let Ok(key) = key
        && let Ok(build) = key.get_value::<String, _>("CurrentBuildNumber")
    {
        let revision = key.get_value::<u32, _>("UBR").unwrap_or(0);
        let display = key
            .get_value::<String, _>("DisplayVersion")
            .unwrap_or_default();
        return format!("Windows build {build}.{revision} ({display})");
    }
    std::env::consts::OS.into()
}

const REPORT_WINDOW_SECONDS: i64 = 180;
const REPORT_LIMIT: u32 = 3;
const OUTBOX_LIMIT: usize = 20;
const REPORT_FILE_LIMIT: u64 = 128 * 1024;

#[derive(Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
struct ReportQuota {
    count: u32,
    window_start_utc: Option<chrono::DateTime<chrono::Utc>>,
    signatures: BTreeMap<String, chrono::DateTime<chrono::Utc>>,
}
#[derive(Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
struct ErrorReport {
    id: String,
    origin: String,
    signature: String,
    context: String,
    message: String,
    logs: String,
    created_at: chrono::DateTime<chrono::Utc>,
}

/// Persist first, then send without blocking the UI on the network. Even when a
/// panic ends the process before HTTP finishes, the next startup can retry it.
pub fn report_error(api: &ApiClient, data: &Path, kind: &str, message: &str) {
    if !error_reports_enabled(api) {
        return;
    }
    match enqueue_error(api, data, kind, message) {
        Ok(true) => flush_error_reports(api, data),
        Ok(false) => (),
        Err(error) => log(
            data,
            "auto-report",
            &format!("Не удалось сохранить отчёт: {error}"),
        ),
    }
}

/// Call on startup and the normal background service tick, including after an
/// offline startup. There is at most one sender per data directory/process set.
pub fn flush_error_reports(api: &ApiClient, data: &Path) {
    if !error_reports_enabled(api) {
        return;
    }
    let api = api.clone();
    let data = data.to_owned();
    let _ = std::thread::Builder::new()
        .name("chillhub-error-reports".into())
        .spawn(move || {
            if let Err(error) = flush_error_reports_sync(&api, &data) {
                log(&data, "auto-report", &error.to_string());
            }
        });
}

fn error_reports_enabled(api: &ApiClient) -> bool {
    let setting = std::env::var("CHILLHUB_ERROR_REPORTS").unwrap_or_default();
    telemetry_allowed(
        &setting,
        std::env::var_os("CHILLHUB_DATA_DIR").is_some(),
        &api.base,
    )
}
fn telemetry_allowed(setting: &str, isolated: bool, base: &url::Url) -> bool {
    let setting = setting.trim();
    if setting.trim() == "0" {
        return false;
    }
    let loopback = match base.host() {
        Some(url::Host::Domain(host)) => host.eq_ignore_ascii_case("localhost"),
        Some(url::Host::Ipv4(ip)) => ip.is_loopback(),
        Some(url::Host::Ipv6(ip)) => ip.is_loopback(),
        _ => false,
    };
    // A test opt-in can never accidentally post to production.
    if setting == "test" {
        return loopback;
    }
    !isolated
}

fn report_lock(data: &Path, name: &str) -> std::io::Result<fs::File> {
    fs::create_dir_all(data)?;
    fs::OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .open(data.join(name))
}
fn read_bounded(path: &Path, limit: u64) -> std::io::Result<Vec<u8>> {
    let file = fs::File::open(path)?;
    if file.metadata()?.len() > limit {
        return Err(std::io::Error::other("Файл отчёта слишком большой"));
    }
    let mut bytes = Vec::new();
    file.take(limit + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 > limit {
        return Err(std::io::Error::other("Файл отчёта слишком большой"));
    }
    Ok(bytes)
}
fn report_files(data: &Path) -> std::io::Result<Vec<PathBuf>> {
    let directory = data.join("error-reports");
    if !directory.exists() {
        return Ok(vec![]);
    }
    let mut files = fs::read_dir(directory)?
        .filter_map(Result::ok)
        .filter(|entry| {
            entry.path().extension().is_some_and(|s| s == "json")
                && entry.file_type().is_ok_and(|t| t.is_file())
        })
        .take(OUTBOX_LIMIT + 1)
        .map(|entry| entry.path())
        .collect::<Vec<_>>();
    files.sort();
    Ok(files)
}
fn read_report(path: &Path) -> std::io::Result<ErrorReport> {
    serde_json::from_slice(&read_bounded(path, REPORT_FILE_LIMIT)?).map_err(std::io::Error::other)
}
fn quota(data: &Path, now: chrono::DateTime<chrono::Utc>) -> ReportQuota {
    let mut quota: ReportQuota = read_bounded(&data.join("report_rl.json"), 32 * 1024)
        .ok()
        .and_then(|bytes| {
            serde_json::from_slice(bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(&bytes)).ok()
        })
        .unwrap_or_default();
    if quota
        .window_start_utc
        .is_none_or(|start| !(0..REPORT_WINDOW_SECONDS).contains(&(now - start).num_seconds()))
    {
        quota.count = 0;
        quota.window_start_utc = Some(now);
    }
    quota
        .signatures
        .retain(|_, time| (0..REPORT_WINDOW_SECONDS).contains(&(now - *time).num_seconds()));
    quota
}
fn save_quota(data: &Path, quota: &ReportQuota) -> std::io::Result<()> {
    atomic_write(
        &data.join("report_rl.json"),
        &serde_json::to_vec(quota).map_err(std::io::Error::other)?,
    )
}
fn enqueue_error(api: &ApiClient, data: &Path, kind: &str, message: &str) -> std::io::Result<bool> {
    let context = bounded_text(&redact(&bounded_text(kind, 1024)), 160);
    let message = bounded_text(&redact(&bounded_text(message, 16 * 1024)), 7600);
    let signature = blake3::hash(format!("{}\n{context}\n{message}", api.base.as_str()).as_bytes())
        .to_hex()
        .to_string();
    let lock = report_lock(data, "error-reports.lock")?;
    fs2::FileExt::lock_exclusive(&lock)?;
    let now = chrono::Utc::now();
    let quota = quota(data, now);
    if quota.signatures.contains_key(&signature) {
        return Ok(false);
    }
    let pending = report_files(data)?;
    if pending.len() >= OUTBOX_LIMIT
        || pending
            .iter()
            .any(|path| read_report(path).is_ok_and(|r| r.signature == signature))
    {
        return Ok(false);
    }
    let mut report = ErrorReport {
        id: uuid::Uuid::new_v4().to_string(),
        origin: api.base.as_str().into(),
        signature,
        context,
        message,
        logs: bounded_log(&crate::diagnostic_bundle::recent_log(data), 64 * 1024),
        created_at: now,
    };
    let output = data
        .join("error-reports")
        .join(format!("{}.json", report.id));
    let bytes = loop {
        let bytes = serde_json::to_vec(&report).map_err(std::io::Error::other)?;
        if bytes.len() as u64 <= REPORT_FILE_LIMIT {
            break bytes;
        }
        if report.logs.is_empty() {
            return Err(std::io::Error::other("Отчёт слишком большой"));
        }
        report.logs = bounded_log(&report.logs, report.logs.len() / 2);
    };
    atomic_write(&output, &bytes)?;
    Ok(true)
}

fn flush_error_reports_sync(api: &ApiClient, data: &Path) -> std::io::Result<usize> {
    let sender = report_lock(data, "error-reports-sending.lock")?;
    if fs2::FileExt::try_lock_exclusive(&sender).is_err() {
        return Ok(0);
    }
    let mut sent = 0;
    for path in report_files(data)? {
        let report = match read_report(&path) {
            Ok(report) => report,
            Err(_) => continue,
        };
        // A changed development endpoint must not inherit another server's outbox.
        if report.origin != api.base.as_str() {
            continue;
        }
        let reservation = {
            let lock = report_lock(data, "error-reports.lock")?;
            fs2::FileExt::lock_exclusive(&lock)?;
            let mut quota = quota(data, chrono::Utc::now());
            if quota.signatures.contains_key(&report.signature) {
                let _ = fs::remove_file(&path);
                continue;
            }
            if quota.count >= REPORT_LIMIT {
                let retry = quota
                    .window_start_utc
                    .map(|start| REPORT_WINDOW_SECONDS - (chrono::Utc::now() - start).num_seconds())
                    .unwrap_or(REPORT_WINDOW_SECONDS)
                    .max(1) as u64;
                crate::diagnostic_notifications::notify(
                    data,
                    crate::diagnostic_notifications::ReportNotice::Delayed {
                        retry_after_seconds: retry,
                    },
                    quota.window_start_utc.map(|time| time.timestamp()),
                );
                break;
            }
            quota.count += 1;
            save_quota(data, &quota)?;
            quota.window_start_utc
        };
        // This runs in the sender worker, never in a UI callback/panic hook.
        let inventory = crate::diagnostic_bundle::collect(data);
        let logs = format!(
            "{}\n\n## Error-time log\n{}",
            bounded_text(&inventory, 32 * 1024),
            bounded_log(&report.logs, 30 * 1024)
        );
        let body = serde_json::json!({"name":"auto","contact":"","type":"bug",
            "comment":format!("[AUTO] Context: {}\n\n{}",bounded_text(&redact(&report.context),160),bounded_text(&redact(&report.message),7600)),
            "attachLogs":true,"logs":bounded_text(&redact(&logs),64*1024),
            "system":{"auto":"1","os":os_version(),"arch":std::env::consts::ARCH,"appVersion":env!("CARGO_PKG_VERSION"),"runtime":"Rust + Slint"}});
        let delivered = api
            .resolve("feedback/submit")
            .ok()
            .and_then(|endpoint| {
                api.client
                    .post(endpoint)
                    .timeout(Duration::from_secs(5))
                    .header("X-Request-ID", &report.id)
                    .json(&body)
                    .send()
                    .ok()
            })
            .is_some_and(|response| response.status().is_success());
        if delivered {
            crate::diagnostic_notifications::notify(
                data,
                crate::diagnostic_notifications::ReportNotice::Sent,
                None,
            );
        }
        {
            let lock = report_lock(data, "error-reports.lock")?;
            fs2::FileExt::lock_exclusive(&lock)?;
            let mut quota = quota(data, chrono::Utc::now());
            if delivered {
                quota
                    .signatures
                    .insert(report.signature, chrono::Utc::now());
            } else if quota.window_start_utc == reservation {
                quota.count = quota.count.saturating_sub(1);
            }
            save_quota(data, &quota)?;
            if delivered {
                fs::remove_file(path)?;
                sent += 1;
            }
        }
        // A failed endpoint is retried on a later tick, without burning the quota
        // or issuing the same burst again in this pass.
        if !delivered {
            break;
        }
    }
    Ok(sent)
}

fn bounded_text(text: &str, limit: usize) -> String {
    let mut end = text.len().min(limit);
    while !text.is_char_boundary(end) {
        end -= 1;
    }
    text[..end].to_owned()
}
fn bounded_log(text: &str, limit: usize) -> String {
    if text.len() <= limit {
        return text.to_owned();
    }
    let head = if limit >= 1024 {
        bounded_text(text, 512)
    } else {
        String::new()
    };
    let marker = if head.is_empty() { "" } else { "\n[…]\n" };
    let mut start = text
        .len()
        .saturating_sub(limit.saturating_sub(head.len() + marker.len()));
    while !text.is_char_boundary(start) {
        start += 1;
    }
    format!("{head}{marker}{}", &text[start..])
}
fn replace_insensitive(text: &str, needle: &str, replacement: &str) -> String {
    if needle.is_empty() {
        return text.to_owned();
    }
    let folded = needle.to_lowercase();
    let mut result = String::with_capacity(text.len());
    let mut index = 0;
    while index < text.len() {
        if text[index..]
            .get(..needle.len())
            .is_some_and(|part| part.to_lowercase() == folded)
        {
            result.push_str(replacement);
            index += needle.len();
        } else {
            let c = text[index..].chars().next().unwrap_or_default();
            result.push(c);
            index += c.len_utf8();
        }
    }
    result
}
pub fn redact(text: &str) -> String {
    let mut result = text.to_owned();
    for key in ["LOCALAPPDATA", "APPDATA", "USERPROFILE"] {
        if let Ok(path) = std::env::var(key)
            && !path.is_empty()
        {
            let replacement = format!("%{key}%");
            for form in [path.replace('\\', "\\\\"), path.replace('\\', "/"), path] {
                result = replace_insensitive(&result, &form, &replacement);
            }
        }
    }
    for (key, replacement) in [("USERNAME", "%USER%"), ("COMPUTERNAME", "%COMPUTER%")] {
        if let Ok(value) = std::env::var(key)
            && value.chars().count() >= 3
        {
            result = replace_insensitive(&result, &value, replacement);
        }
    }
    result
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Metric {
    pub event: String,
    pub game_id: String,
    pub version: String,
    pub result: String,
    pub duration_ms: u64,
    pub bytes: u64,
    pub error_code: String,
    pub files_downloaded: usize,
    pub files_total: usize,
    pub full_bytes: u64,
    pub hash_mismatches: usize,
}
pub fn metric(api: &ApiClient, data: &Path, event: Metric) {
    // Local troubleshooting remains available when online telemetry is disabled.
    self::event(
        data,
        "operation.complete",
        serde_json::to_value(&event).unwrap_or_default(),
    );
    let setting = std::env::var("CHILLHUB_METRICS").unwrap_or_default();
    // Isolated app tests cannot send player metrics even if a caller forgot the flag.
    if !telemetry_allowed(
        &setting,
        std::env::var_os("CHILLHUB_DATA_DIR").is_some(),
        &api.base,
    ) {
        return;
    }
    let api = api.clone();
    let data = data.to_owned();
    std::thread::spawn(move || {
        let path = data.join("install-id");
        let id = fs::read_to_string(&path)
            .ok()
            .and_then(|v| uuid::Uuid::parse_str(v.trim()).ok())
            .or_else(|| {
                let id = uuid::Uuid::new_v4();
                atomic_write(&path, id.to_string().as_bytes())
                    .ok()
                    .map(|_| id)
            })
            .map(|id| id.simple().to_string())
            .unwrap_or_default();
        let id = if setting == "test" {
            format!("test-{id}")
        } else {
            id
        };
        let Ok(mut body) = serde_json::to_value(event) else {
            return;
        };
        body["installId"] = id.into();
        body["appVersion"] = env!("CARGO_PKG_VERSION").into();
        body["os"] = std::env::consts::OS.into();
        if let Ok(endpoint) = api.resolve("metrics/report") {
            let _ = api
                .client
                .post(endpoint)
                .timeout(Duration::from_secs(5))
                .json(&body)
                .send();
        }
    });
}
#[cfg(test)]
#[path = "diagnostics_tests.rs"]
mod tests;
