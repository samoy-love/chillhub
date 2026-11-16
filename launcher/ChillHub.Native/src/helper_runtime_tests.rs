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
