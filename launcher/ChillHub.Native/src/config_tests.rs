use super::*;
#[test]
fn migrates_pascal_case_without_deleting_old_or_unknown_fields() {
    let tmp = tempfile::tempdir().unwrap();
    let mut paths = AppPaths::isolated(tmp.path());
    let legacy = tmp.path().join("legacy");
    fs::create_dir_all(&legacy).unwrap();
    fs::write(legacy.join("config.json"), r#"{"GamesPath":"D:\\Games\\custom","DownloadThreads":99,"LaunchTargets":{"PEAK":"SteamVanilla"},"FutureField":42}"#).unwrap();
    paths.legacy_dir = Some(legacy.clone());
    let store = ConfigStore::new(paths);
    let mut cfg = store.load().unwrap();
    assert_eq!(cfg.download_threads, 16);
    assert_eq!(cfg.remembered("peak"), Some(LaunchTarget::SteamVanilla));
    cfg.games_path = "different".into();
    store.save(&cfg).unwrap();
    assert_eq!(store.load().unwrap().games_path, "different");
    assert_eq!(store.load().unwrap().extra["FutureField"], 42);
    assert!(legacy.join("config.json").exists());
}
#[test]
fn corrupt_backup_is_exact_and_roundtrip_preserves_all_settings() {
    let tmp = tempfile::tempdir().unwrap();
    let store = ConfigStore::new(AppPaths::isolated(tmp.path()));
    fs::create_dir_all(&store.paths.data_dir).unwrap();
    fs::write(store.paths.config_path(), b"broken").unwrap();
    let mut cfg = store.load().unwrap();
    assert_eq!(
        fs::read(store.paths.data_dir.join("config.corrupted.json")).unwrap(),
        b"broken"
    );
    cfg.remember("peak", LaunchTarget::LocalVanilla);
    cfg.window_width = 1360.;
    cfg.minimize_to_tray = false;
    store.save(&cfg).unwrap();
    let back = store.load().unwrap();
    assert_eq!(back.window_width, 1360.);
    assert!(!back.minimize_to_tray);
    assert_eq!(back.remembered("PEAK"), Some(LaunchTarget::LocalVanilla));
}
#[test]
fn api_endpoint_rejects_insecure_remote() {
    assert!(acceptable_api_url("https://launcher.samoy.love"));
    assert!(acceptable_api_url("http://127.0.0.1:55700"));
    for s in [
        "http://example.com",
        "file:///tmp",
        "https://user:secret@example.com",
        "javascript:x",
    ] {
        assert!(!acceptable_api_url(s));
    }
}
#[test]
fn oversized_settings_are_preserved_instead_of_reset() {
    let tmp = tempfile::tempdir().unwrap();
    let store = ConfigStore::new(AppPaths::isolated(tmp.path()));
    fs::create_dir_all(&store.paths.data_dir).unwrap();
    let bytes = vec![b' '; MAX_CONFIG_BYTES as usize + 1];
    fs::write(store.paths.config_path(), &bytes).unwrap();
    assert!(store.load().is_err());
    assert_eq!(fs::read(store.paths.config_path()).unwrap(), bytes);
    assert!(!store.paths.data_dir.join("config.corrupted.json").exists());
}
