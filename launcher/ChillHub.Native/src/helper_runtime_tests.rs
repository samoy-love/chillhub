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
    let oversized = "я".repeat(5000);
    let mut full = false;
    for _ in 0..200 {
        if let Err(error) = log.record("progress", &oversized) {
            assert!(error.contains("размер журнала"));
            full = true;
            break;
        }
    }
    assert!(full);
    // Rejecting a long message can still leave room for a short result.
    // Exhaust that remaining space with smaller valid records before testing
    // the result fallback, without depending on timestamp formatting length.
    let mut no_short_record_fits = false;
    for _ in 0..200 {
        if let Err(error) = log.record("fill", "x") {
            assert!(error.contains("размер журнала"));
            no_short_record_fits = true;
            break;
        }
    }
    assert!(no_short_record_fits);
    let bytes_before_result = fs::metadata(log.path()).unwrap().len();
    let caption = record_result(&Ok(log.clone()), &Err("operation failed".into()));
    assert!(caption.contains("Не удалось дописать журнал"));
    assert!(caption.contains(log.path().to_str().unwrap()));
    let data = fs::read(log.path()).unwrap();
    assert_eq!(data.len() as u64, bytes_before_result);
    assert!(data.len() as u64 <= MAX_LOG_BYTES);
    for line in String::from_utf8(data).unwrap().lines() {
        let entry = serde_json::from_str::<serde_json::Value>(line).unwrap();
        let message = entry["message"].as_str().unwrap();
        assert!(message == "я".repeat(4000) || message == "x");
    }
    assert_eq!(fs::read_to_string(&sentinel).unwrap(), "keep");
    assert!(fs::read_dir(temp.path().join("logs")).unwrap().count() <= KEEP_LOGS + 1);
}
#[test]
fn worker_panic_becomes_a_reported_error() {
    let error = caught::<()>(|| panic!("fixture failure")).unwrap_err();
    assert!(error.contains("fixture failure"));
    assert_eq!(caught(|| Ok(7)).unwrap(), 7);
    assert_eq!(
        caught::<()>(|| Err("original error".into())),
        Err("original error".into())
    );
    let long = caught::<()>(|| std::panic::panic_any("я".repeat(1200))).unwrap_err();
    assert!(long.ends_with(&"я".repeat(1000)));
    assert!(!long.ends_with(&"я".repeat(1001)));
    let unknown = caught::<()>(|| std::panic::panic_any(51u32)).unwrap_err();
    assert!(unknown.contains("неизвестная ошибка"));
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

#[test]
fn retention_expires_old_matching_logs_without_truncating_other_files() {
    let temp = tempfile::tempdir().unwrap();
    let directory = temp.path().join("logs");
    fs::create_dir(&directory).unwrap();
    let now = std::time::SystemTime::now();
    let mut old = Vec::new();
    for index in 0..KEEP_LOGS + 2 {
        let path = directory.join(format!("setup-{}.jsonl", uuid::Uuid::new_v4()));
        let file = File::create(&path).unwrap();
        file.set_times(
            fs::FileTimes::new()
                .set_modified(now - std::time::Duration::from_secs(120 - index as u64 * 2)),
        )
        .unwrap();
        old.push(path);
    }
    let unnamed = directory.join("setup-not-a-uuid.jsonl");
    fs::write(&unnamed, b"preserve malformed name").unwrap();
    let other = directory.join(format!("security-{}.jsonl", uuid::Uuid::new_v4()));
    fs::write(&other, b"preserve another helper").unwrap();
    let folder = directory.join(format!("setup-{}.jsonl", uuid::Uuid::new_v4()));
    fs::create_dir(&folder).unwrap();

    let log = HelperLog::in_directory(temp.path(), HelperKind::Setup).unwrap();
    assert!(
        log.path()
            .file_name()
            .unwrap()
            .to_string_lossy()
            .starts_with("setup-")
    );
    for file in &old[..3] {
        assert!(!file.exists());
    }
    for file in &old[3..] {
        assert_eq!(fs::read(file).unwrap(), b"");
    }
    assert_eq!(fs::read(unnamed).unwrap(), b"preserve malformed name");
    assert_eq!(fs::read(other).unwrap(), b"preserve another helper");
    assert!(folder.is_dir());
}
