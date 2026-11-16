use super::*;
#[test]
fn includes_versions_mod_logs_and_only_safe_settings() {
    let tmp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(tmp.path());
    let store = ConfigStore::new(paths.clone());
    let mut config = store.load().unwrap();
    config
        .extra
        .insert("Password".into(), "never-collect-this".into());
    config.api_base_url = "https://example.com/?secret=private-query".into();
    store.save(&config).unwrap();
    let game = paths.games_dir.join("peak");
    fs::create_dir_all(game.join("BepInEx")).unwrap();
    fs::write(game.join(".version"), "1.2.3").unwrap();
    fs::write(game.join("BepInEx/LogOutput.log"), "mod diagnostic line").unwrap();
    let report = collect_at(&paths.data_dir, None);
    assert!(report.contains("1.2.3") && report.contains("mod diagnostic line"));
    assert!(!report.contains("never-collect-this") && !report.contains("private-query"));
}
#[test]
fn huge_logs_are_tailed_without_exceeding_bundle_budget() {
    let tmp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(tmp.path());
    ConfigStore::new(paths.clone()).load().unwrap();
    fs::create_dir_all(paths.data_dir.join("logs")).unwrap();
    let mut log = "x".repeat(3 * 1024 * 1024);
    log.push_str("RECENT-END");
    fs::write(paths.data_dir.join("logs/launcher.log"), log).unwrap();
    let report = collect_at(&paths.data_dir, None);
    assert!(report.len() <= MAX_BUNDLE && report.contains("RECENT-END"));
    assert!(report.contains("earlier content omitted"));
}
#[test]
fn collecting_missing_or_corrupt_settings_never_writes_them() {
    let tmp = tempfile::tempdir().unwrap();
    let path = tmp.path().join("config.json");
    let _ = collect_at(tmp.path(), None);
    assert!(!path.exists());
    fs::write(&path, b"broken").unwrap();
    let report = collect_at(tmp.path(), None);
    assert!(report.contains("повреждён"));
    assert_eq!(fs::read(path).unwrap(), b"broken");
}
#[test]
fn includes_current_helper_logs_and_ignores_unrecognized_names() {
    let tmp = tempfile::tempdir().unwrap();
    fs::create_dir(tmp.path().join("logs")).unwrap();
    for (kind, message) in [
        ("updater", "update-failure-details"),
        ("security", "security-check-details"),
    ] {
        fs::write(
            tmp.path()
                .join(format!("logs/{kind}-{}.jsonl", uuid::Uuid::new_v4())),
            serde_json::json!({"at":"2026-11-10T00:00:00Z","event":"failure","message":message})
                .to_string(),
        )
        .unwrap();
    }
    fs::write(
        tmp.path().join("logs/security-private-notes.jsonl"),
        "unrelated-private-text",
    )
    .unwrap();
    let report = collect_at(tmp.path(), None);
    assert!(report.contains("update-failure-details") && report.contains("security-check-details"));
    assert!(!report.contains("unrelated-private-text"));
    assert!(report.len() <= MAX_BUNDLE);
}
