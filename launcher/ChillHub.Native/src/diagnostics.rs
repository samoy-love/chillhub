use crate::{net::ApiClient, paths::atomic_write};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    fs,
    io::{Read, Write},
    path::{Path, PathBuf},
    sync::Mutex,
    time::Duration,
};
static LOG_LOCK: Mutex<()> = Mutex::new(());

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
    let Ok(_guard) = LOG_LOCK.lock() else {
        return;
    };
    let directory = data.join("logs");
    if fs::create_dir_all(&directory).is_err() {
        return;
    }
    let path = directory.join("launcher.log");
    if fs::metadata(&path).is_ok_and(|m| m.len() > 2 * 1024 * 1024) {
        let previous = directory.join("launcher.previous.log");
        let _ = fs::remove_file(&previous);
        let _ = fs::rename(&path, &previous);
    }
    if let Ok(mut file) = fs::OpenOptions::new().create(true).append(true).open(path) {
        let _ = writeln!(
            file,
            "{} [{kind}] {}",
            chrono::Utc::now().to_rfc3339(),
            redact(message)
                .chars()
                .take(12000)
                .collect::<String>()
                .replace(['\r', '\n'], " ")
        );
    }
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
mod tests {
    use super::*;
    #[test]
    fn logging_respects_the_existing_environment_switch() {
        for value in ["0", "false", " OFF ", "No"] {
            assert!(!logging_enabled(value));
        }
        for value in ["", "1", "true", "anything"] {
            assert!(logging_enabled(value));
        }
    }
    #[test]
    fn diagnostics_rotate_without_growing_forever() {
        let temp = tempfile::tempdir().unwrap();
        let logs = temp.path().join("logs");
        fs::create_dir_all(&logs).unwrap();
        fs::write(logs.join("launcher.log"), vec![b'a'; 2 * 1024 * 1024 + 1]).unwrap();
        log(temp.path(), "test", "message");
        assert!(fs::metadata(logs.join("launcher.log")).unwrap().len() < 1024);
        assert!(logs.join("launcher.previous.log").is_file());
    }
    #[test]
    fn metrics_schema_has_no_user_machine_or_path_fields() {
        let value = serde_json::to_value(Metric::default()).unwrap();
        for forbidden in ["username", "computer", "path", "logs", "contact", "name"] {
            assert!(value.get(forbidden).is_none());
        }
    }

    struct ReportServer {
        api: ApiClient,
        status: std::sync::Arc<std::sync::atomic::AtomicU16>,
        stopped: std::sync::Arc<std::sync::atomic::AtomicBool>,
        bodies: std::sync::Arc<Mutex<Vec<(String, serde_json::Value)>>>,
        worker: Option<std::thread::JoinHandle<()>>,
    }
    impl ReportServer {
        fn new(status: u16) -> Self {
            use std::sync::{
                Arc,
                atomic::{AtomicBool, AtomicU16, Ordering},
            };
            let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
            let api = ApiClient::new(&format!("http://{}", server.server_addr())).unwrap();
            let status = Arc::new(AtomicU16::new(status));
            let reply = status.clone();
            let stopped = Arc::new(AtomicBool::new(false));
            let stop = stopped.clone();
            let bodies = Arc::new(Mutex::new(Vec::new()));
            let received = bodies.clone();
            let worker = std::thread::spawn(move || {
                while !stop.load(Ordering::Relaxed) {
                    if let Some(mut request) =
                        server.recv_timeout(Duration::from_millis(20)).unwrap()
                    {
                        let mut bytes = Vec::new();
                        request.as_reader().read_to_end(&mut bytes).unwrap();
                        received.lock().unwrap().push((
                            request.url().into(),
                            serde_json::from_slice(&bytes).unwrap(),
                        ));
                        let _ = request.respond(
                            tiny_http::Response::from_string("{}")
                                .with_status_code(reply.load(Ordering::Relaxed)),
                        );
                    }
                }
            });
            Self {
                api,
                status,
                stopped,
                bodies,
                worker: Some(worker),
            }
        }
    }
    impl Drop for ReportServer {
        fn drop(&mut self) {
            self.stopped
                .store(true, std::sync::atomic::Ordering::Relaxed);
            self.worker.take().unwrap().join().unwrap();
        }
    }

    #[test]
    fn auto_error_reports_use_server_schema_and_redact_private_paths() {
        let server = ReportServer::new(200);
        let temp = tempfile::tempdir().unwrap();
        let data = temp.path();
        let profile = std::env::var("USERPROFILE").unwrap_or_default();
        let private = format!(
            "{} {} {}",
            profile.to_uppercase(),
            profile.replace('\\', "/"),
            profile.replace('\\', "\\\\")
        );
        fs::create_dir_all(data.join("logs")).unwrap();
        fs::write(
            data.join("logs/launcher.log"),
            format!("{}\n{private}", "\0".repeat(140 * 1024)),
        )
        .unwrap();
        assert!(
            enqueue_error(
                &server.api,
                data,
                "download",
                &format!("{private}: {}", "Сбой".repeat(10000))
            )
            .unwrap()
        );
        let queued = report_files(data).unwrap();
        assert_eq!(queued.len(), 1);
        assert!(fs::metadata(&queued[0]).unwrap().len() <= REPORT_FILE_LIMIT);
        assert!(
            !enqueue_error(
                &server.api,
                data,
                "download",
                &format!("{private}: {}", "Сбой".repeat(10000))
            )
            .unwrap()
        );
        assert_eq!(flush_error_reports_sync(&server.api, data).unwrap(), 1);
        let received = server.bodies.lock().unwrap();
        let (path, body) = &received[0];
        assert_eq!(path, "/feedback/submit");
        assert_eq!(body["name"], "auto");
        assert_eq!(body["type"], "bug");
        assert_eq!(body["system"]["auto"], "1");
        assert_eq!(body["attachLogs"], true);
        assert!(body["comment"].as_str().unwrap().len() < 8000);
        assert!(body["logs"].as_str().unwrap().len() <= 64 * 1024);
        if !profile.is_empty() {
            let text = format!(
                "{} {}",
                body["comment"].as_str().unwrap(),
                body["logs"].as_str().unwrap()
            )
            .to_lowercase();
            for form in [
                profile.to_lowercase(),
                profile.replace('\\', "/").to_lowercase(),
                profile.replace('\\', "\\\\").to_lowercase(),
            ] {
                assert!(!text.contains(&form));
            }
        }
        assert!(
            !body["system"]
                .as_object()
                .unwrap()
                .contains_key("machineName")
        );
        assert!(report_files(data).unwrap().is_empty());
    }

    #[test]
    fn auto_error_quota_persists_and_failed_delivery_refunds() {
        let server = ReportServer::new(503);
        let temp = tempfile::tempdir().unwrap();
        let data = temp.path();
        for error in ["first", "second", "third"] {
            assert!(enqueue_error(&server.api, data, "test", error).unwrap());
        }
        assert_eq!(flush_error_reports_sync(&server.api, data).unwrap(), 0);
        assert_eq!(quota(data, chrono::Utc::now()).count, 0);
        assert_eq!(report_files(data).unwrap().len(), 3);
        server
            .status
            .store(200, std::sync::atomic::Ordering::Relaxed);
        assert_eq!(flush_error_reports_sync(&server.api, data).unwrap(), 3);
        assert_eq!(quota(data, chrono::Utc::now()).count, 3);
        assert!(!enqueue_error(&server.api, data, "test", "first").unwrap());
        assert!(enqueue_error(&server.api, data, "test", "fourth").unwrap());
        let restarted = ApiClient::new(server.api.base.as_str()).unwrap();
        assert_eq!(flush_error_reports_sync(&restarted, data).unwrap(), 0);
        assert_eq!(report_files(data).unwrap().len(), 1);
        let mut expired = quota(data, chrono::Utc::now());
        expired.window_start_utc = Some(chrono::Utc::now() - chrono::Duration::seconds(181));
        save_quota(data, &expired).unwrap();
        assert_eq!(flush_error_reports_sync(&restarted, data).unwrap(), 1);
        assert_eq!(quota(data, chrono::Utc::now()).count, 1);
        assert!(report_files(data).unwrap().is_empty());
    }

    #[test]
    fn auto_error_legacy_quota_and_outbox_origin_are_respected() {
        let server = ReportServer::new(200);
        let other = ReportServer::new(200);
        let temp = tempfile::tempdir().unwrap();
        let data = temp.path();
        fs::write(
            data.join("report_rl.json"),
            serde_json::to_vec(&serde_json::json!({"Count":3,"WindowStartUtc":chrono::Utc::now()}))
                .unwrap(),
        )
        .unwrap();
        assert!(enqueue_error(&server.api, data, "legacy", "failure").unwrap());
        assert_eq!(flush_error_reports_sync(&server.api, data).unwrap(), 0);
        let mut expired = quota(data, chrono::Utc::now());
        expired.window_start_utc = Some(chrono::Utc::now() - chrono::Duration::seconds(181));
        save_quota(data, &expired).unwrap();
        assert_eq!(flush_error_reports_sync(&other.api, data).unwrap(), 0);
        assert!(other.bodies.lock().unwrap().is_empty());
        assert_eq!(flush_error_reports_sync(&server.api, data).unwrap(), 1);
    }

    #[test]
    fn error_reports_and_metrics_test_optin_cannot_send_to_production() {
        let production = url::Url::parse("https://launcher.samoy.love").unwrap();
        let local = url::Url::parse("http://127.0.0.1:1234").unwrap();
        assert!(!telemetry_allowed("0", false, &production));
        assert!(!telemetry_allowed("", true, &production));
        assert!(!telemetry_allowed("test", true, &production));
        assert!(!telemetry_allowed(" test ", false, &production));
        assert!(telemetry_allowed("test", true, &local));
        assert!(telemetry_allowed("", false, &production));
    }
}
