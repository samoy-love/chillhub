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
    assert!(acceptable_api_url("http://[::1]:55700"));
    for s in [
        "broken url",
        "http://example.com",
        "http://[::2]:55700",
        "file:///tmp",
        "https://user:secret@example.com",
        "javascript:x",
    ] {
        assert!(!acceptable_api_url(s));
    }
}

#[test]
fn readonly_settings_preserve_legacy_defaults_and_refuse_unreadable_state() {
    let temp = tempfile::tempdir().unwrap();
    let store = ConfigStore::new(AppPaths::isolated(temp.path()));
    let path = store.paths.config_path();
    let saved = b"\xef\xbb\xbf{ \"GamesPath\": null, \"ApiBaseUrl\": null, \"LaunchTargets\": null, \"SelectedVersions\": null, \"FutureField\": null }";
    fs::create_dir_all(&store.paths.data_dir).unwrap();
    fs::write(&path, saved).unwrap();
    let view = store.load_readonly().unwrap();
    assert_eq!(view.games_path, AppConfig::default().games_path);
    assert_eq!(view.api_base_url, DEFAULT_API_BASE_URL);
    assert!(view.launch_targets.is_empty() && view.selected_versions.is_empty());
    assert_eq!(view.extra["FutureField"], serde_json::Value::Null);
    assert_eq!(fs::read(&path).unwrap(), saved);

    let oversized = vec![b' '; MAX_CONFIG_BYTES as usize + 1];
    fs::write(&path, &oversized).unwrap();
    assert!(
        store
            .load_readonly()
            .unwrap_err()
            .contains("слишком большой")
    );
    assert_eq!(fs::read(&path).unwrap(), oversized);
    assert!(!store.paths.data_dir.join("config.corrupted.json").exists());

    fs::remove_file(&path).unwrap();
    fs::create_dir(&path).unwrap();
    assert!(store.load_readonly().is_err());
    assert!(path.is_dir());
}

#[test]
fn normalized_settings_recover_from_an_invalid_saved_window_and_server() {
    let mut config = AppConfig {
        games_path: "  ".into(),
        api_base_url: "http://example.com".into(),
        window_width: f64::INFINITY,
        window_height: -1.,
        download_threads: 0,
        speed_limit_mbps: 3000,
        ..AppConfig::default()
    };
    config.normalize();
    assert_eq!(config.games_path, AppConfig::default().games_path);
    assert_eq!(config.api_base_url, DEFAULT_API_BASE_URL);
    assert_eq!((config.window_width, config.window_height), (0., 0.));
    assert_eq!(
        (config.download_threads, config.speed_limit_mbps),
        (2, 1000)
    );
}

#[test]
fn unsuccessful_settings_write_keeps_the_preexisting_path() {
    let temp = tempfile::tempdir().unwrap();
    let store = ConfigStore::new(AppPaths::isolated(temp.path()));
    store.save(&AppConfig::default()).unwrap();
    let path = store.paths.config_path();
    let previous = fs::read(&path).unwrap();
    let mut huge = store.load_readonly().unwrap();
    huge.extra.insert(
        "LargeUnknownValue".into(),
        "x".repeat(MAX_CONFIG_BYTES as usize).into(),
    );
    assert!(store.save(&huge).unwrap_err().contains("слишком большой"));
    assert_eq!(fs::read(&path).unwrap(), previous);

    fs::remove_file(&path).unwrap();
    fs::create_dir(&path).unwrap();
    fs::write(path.join("personal.txt"), "keep").unwrap();
    assert!(
        store
            .save(&AppConfig::default())
            .unwrap_err()
            .contains("сохранить настройки")
    );
    assert_eq!(fs::read(path.join("personal.txt")).unwrap(), b"keep");
    assert_eq!(fs::read_dir(&store.paths.data_dir).unwrap().count(), 1);
}

#[test]
fn validating_a_games_directory_cleans_its_write_probe_and_preserves_existing_data() {
    let temp = tempfile::tempdir().unwrap();
    let games = temp.path().join("Games");
    games_path_is_usable(&games).unwrap();
    let save = games.join("save");
    fs::write(&save, "keep").unwrap();
    games_path_is_usable(&games).unwrap();
    assert_eq!(fs::read_dir(&games).unwrap().count(), 1);
    assert!(games_path_is_usable(&save).is_err());
    assert_eq!(fs::read(save).unwrap(), b"keep");
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
