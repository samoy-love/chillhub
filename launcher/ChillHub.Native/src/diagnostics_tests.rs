use super::*;
#[test]
fn empty_primary_directory_does_not_hide_existing_fallback_logs() {
    let temp = tempfile::tempdir().unwrap();
    let data = temp.path().join("profile");
    let fallback = temp.path().join("user-temp");
    fs::create_dir_all(data.join("logs")).unwrap();
    let directory = fallback_log_data(Some(&data), &fallback)
        .unwrap()
        .join("logs");
    fs::create_dir_all(&directory).unwrap();
    let log = directory.join(format!("security-{}.jsonl", uuid::Uuid::new_v4()));
    fs::write(&log, b"previous helper log").unwrap();
    assert!(actual_log_path(&data).is_none());
    assert_eq!(existing_log_directory_at(&data, &fallback), Some(directory));
    assert_eq!(fs::read_dir(data.join("logs")).unwrap().count(), 0);
    assert_eq!(fs::read(log).unwrap(), b"previous helper log");
    assert!(!data.join("config.json").exists());
}

#[test]
fn newest_existing_log_wins_after_restart_but_active_process_path_has_priority() {
    let temp = tempfile::tempdir().unwrap();
    let data = temp.path().join("profile");
    let fallback = temp.path().join("user-temp");
    let primary = data.join("logs");
    fs::create_dir_all(&primary).unwrap();
    let old = primary.join("client.log");
    fs::write(&old, b"old primary log").unwrap();
    fs::OpenOptions::new()
        .write(true)
        .open(&old)
        .unwrap()
        .set_times(
            fs::FileTimes::new()
                .set_modified(std::time::SystemTime::now() - Duration::from_secs(3600)),
        )
        .unwrap();
    let directory = fallback_log_data(Some(&data), &fallback)
        .unwrap()
        .join("logs");
    fs::create_dir_all(&directory).unwrap();
    let current = directory.join("launcher.log");
    fs::write(&current, b"current fallback log").unwrap();
    fs::write(primary.join("unrelated-private.log"), b"not a launcher log").unwrap();
    assert_eq!(existing_log_directory_at(&data, &fallback), Some(directory));
    assert_eq!(fs::read(&old).unwrap(), b"old primary log");
    assert_eq!(fs::read(&current).unwrap(), b"current fallback log");
    assert!(!primary.join("launcher.log").exists());
    log_with_fallback(Some(&data), &fallback, "test", "active primary log").unwrap();
    fs::OpenOptions::new()
        .write(true)
        .open(&current)
        .unwrap()
        .set_times(
            fs::FileTimes::new()
                .set_modified(std::time::SystemTime::now() + Duration::from_secs(3600)),
        )
        .unwrap();
    assert_eq!(existing_log_directory_at(&data, &fallback), Some(primary));
}

#[test]
fn old_logs_can_be_opened_without_enabling_or_writing_new_records() {
    let temp = tempfile::tempdir().unwrap();
    let data = temp.path().join("profile");
    let primary = data.join("logs");
    let fallback = temp.path().join("user-temp");
    fs::create_dir_all(&primary).unwrap();
    let file = primary.join("client.log");
    fs::write(&file, b"old client log").unwrap();
    let modified = fs::metadata(&file).unwrap().modified().unwrap();
    // An empty runtime map models a new process with logging disabled.
    assert!(actual_log_path(&data).is_none());
    assert_eq!(existing_log_directory_at(&data, &fallback), Some(primary));
    assert_eq!(fs::read(&file).unwrap(), b"old client log");
    assert_eq!(fs::metadata(&file).unwrap().modified().unwrap(), modified);
    assert!(actual_log_path(&data).is_none());
    assert!(!fallback.exists());
    assert!(!data.join("logs/launcher.log").exists());
    assert!(!data.join("config.json").exists());
}

#[test]
fn opening_existing_fallback_logs_does_not_create_primary_or_missing_folders() {
    let temp = tempfile::tempdir().unwrap();
    let data = temp.path().join("unavailable-profile");
    let fallback = temp.path().join("user-temp");
    assert!(existing_log_directory_at(&data, &fallback).is_none());
    assert!(!data.exists() && !fallback.exists());
    let directory = fallback_log_data(Some(&data), &fallback)
        .unwrap()
        .join("logs");
    fs::create_dir_all(&directory).unwrap();
    let log = directory.join("launcher.log");
    fs::write(&log, b"previous fallback log").unwrap();
    assert_eq!(existing_log_directory_at(&data, &fallback), Some(directory));
    assert!(!data.exists());
    assert_eq!(fs::read(log).unwrap(), b"previous fallback log");
    assert!(actual_log_path(&data).is_none());
    // A file at the preferred directory is not an openable log directory.
    fs::create_dir_all(&data).unwrap();
    fs::write(data.join("logs"), b"not a directory").unwrap();
    let unused = temp.path().join("unused-temp");
    assert!(existing_log_directory_at(&data, &unused).is_none());
    assert!(!unused.exists());
}

#[test]
fn unavailable_profile_logs_only_to_injected_temp_and_reports_actual_path() {
    let temp = tempfile::tempdir().unwrap();
    let data = temp.path().join("blocked-roaming");
    fs::write(&data, b"profile must remain unchanged").unwrap();
    let fallback = temp.path().join("user-temp");
    let path = log_with_fallback(Some(&data), &fallback, "startup", "fixture failure").unwrap();
    assert!(path.starts_with(&fallback));
    assert_eq!(actual_log_path(&data).as_deref(), Some(path.as_path()));
    assert!(
        fs::read_to_string(&path)
            .unwrap()
            .contains("fixture failure")
    );
    assert_eq!(fs::read(&data).unwrap(), b"profile must remain unchanged");
    assert!(!fallback.join("config.json").exists());
    assert!(!fallback.join("games").exists());
    assert!(!fallback.join("error-reports").exists());
    let report = crate::diagnostic_bundle::recent_log(&data);
    assert!(report.contains("fixture failure"));
    assert!(!report.contains(&fallback.to_string_lossy().to_string()));
    assert_eq!(
        log_directories_at(&data, &fallback).first().unwrap(),
        path.parent().unwrap()
    );
    let other = fallback_log_data(Some(&temp.path().join("other-profile")), &fallback).unwrap();
    assert_ne!(other.join("logs/launcher.log"), path);
}

#[test]
fn writable_profile_keeps_primary_log_and_does_not_create_fallback() {
    let temp = tempfile::tempdir().unwrap();
    let data = temp.path().join("profile");
    let fallback = temp.path().join("unused-temp");
    let path = log_with_fallback(Some(&data), &fallback, "test", "primary").unwrap();
    assert_eq!(path, data.join("logs/launcher.log"));
    assert!(!fallback.exists());
    assert_eq!(actual_log_path(&data), Some(path));
}

#[test]
fn fallback_rotation_remains_bounded_and_unknown_profile_never_uses_cwd() {
    let temp = tempfile::tempdir().unwrap();
    let fallback = temp.path().join("user-temp");
    let path = log_with_fallback(None, &fallback, "startup", "first").unwrap();
    fs::write(&path, vec![b'a'; 2 * 1024 * 1024 + 1]).unwrap();
    let second = log_with_fallback(None, &fallback, "startup", "second").unwrap();
    assert_eq!(path, second);
    assert!(path.starts_with(&fallback));
    assert!(fs::metadata(&path).unwrap().len() < 1024);
    assert!(path.with_file_name("launcher.previous.log").is_file());
    assert!(fallback_log_data(None, Path::new("relative-temp")).is_err());
    assert!(fallback_log_data(None, &temp.path().join("child/../escape")).is_err());
}

#[test]
fn both_unavailable_locations_are_an_error_without_claiming_a_log_path() {
    let temp = tempfile::tempdir().unwrap();
    let data = temp.path().join("blocked-profile");
    let fallback = temp.path().join("blocked-temp");
    fs::write(&data, b"profile").unwrap();
    fs::write(&fallback, b"temp").unwrap();
    let result = log_with_fallback(Some(&data), &fallback, "test", "not written");
    let error = result.unwrap_err().to_string();
    assert!(error.contains("Основной журнал") && error.contains("резервный"));
    assert!(actual_log_path(&data).is_none());
    assert_eq!(fs::read(&data).unwrap(), b"profile");
    assert_eq!(fs::read(&fallback).unwrap(), b"temp");
}

#[test]
fn planted_hardlink_is_not_written_and_fallback_is_also_guarded() {
    let temp = tempfile::tempdir().unwrap();
    let data = temp.path().join("profile");
    fs::create_dir_all(data.join("logs")).unwrap();
    let sentinel = temp.path().join("unrelated.txt");
    fs::write(&sentinel, b"keep").unwrap();
    fs::hard_link(&sentinel, data.join("logs/launcher.log")).unwrap();
    let fallback = temp.path().join("user-temp");
    let fallback_data = fallback_log_data(Some(&data), &fallback).unwrap();
    fs::create_dir_all(fallback_data.join("logs")).unwrap();
    fs::hard_link(&sentinel, fallback_data.join("logs/launcher.log")).unwrap();
    assert!(log_with_fallback(Some(&data), &fallback, "test", "must not write").is_err());
    assert_eq!(fs::read(&sentinel).unwrap(), b"keep");
    fs::remove_file(fallback_data.join("logs/launcher.log")).unwrap();
    let path = log_with_fallback(Some(&data), &fallback, "test", "safe fallback").unwrap();
    assert_eq!(path, fallback_data.join("logs/launcher.log"));
    assert_eq!(fs::read(&sentinel).unwrap(), b"keep");
}

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
                if let Some(mut request) = server.recv_timeout(Duration::from_millis(20)).unwrap() {
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
    let (notice_tx, notices) = std::sync::mpsc::channel();
    crate::diagnostic_notifications::subscribe(data, move |notice| {
        let _ = notice_tx.send(notice);
    });
    for error in ["first", "second", "third"] {
        assert!(enqueue_error(&server.api, data, "test", error).unwrap());
    }
    assert_eq!(flush_error_reports_sync(&server.api, data).unwrap(), 0);
    assert!(
        notices.try_recv().is_err(),
        "failed HTTP must not claim delivery"
    );
    assert_eq!(quota(data, chrono::Utc::now()).count, 0);
    assert_eq!(report_files(data).unwrap().len(), 3);
    server
        .status
        .store(200, std::sync::atomic::Ordering::Relaxed);
    assert_eq!(flush_error_reports_sync(&server.api, data).unwrap(), 3);
    for _ in 0..3 {
        assert_eq!(
            notices.try_recv().unwrap(),
            crate::diagnostic_notifications::ReportNotice::Sent
        );
    }
    assert!(notices.try_recv().is_err());
    assert_eq!(quota(data, chrono::Utc::now()).count, 3);
    assert!(!enqueue_error(&server.api, data, "test", "first").unwrap());
    assert!(enqueue_error(&server.api, data, "test", "fourth").unwrap());
    let restarted = ApiClient::new(server.api.base.as_str()).unwrap();
    assert_eq!(flush_error_reports_sync(&restarted, data).unwrap(), 0);
    assert!(matches!(
        notices.try_recv().unwrap(),
        crate::diagnostic_notifications::ReportNotice::Delayed { .. }
    ));
    assert_eq!(flush_error_reports_sync(&restarted, data).unwrap(), 0);
    assert!(
        notices.try_recv().is_err(),
        "quota notices must not repeat on every tick"
    );
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
