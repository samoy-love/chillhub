use super::*;
#[test]
fn maintenance_defaults_and_server_clock_are_safe() {
    for raw in [
        r#"{"enabled":true}"#,
        r#"{"enabled":true,"blocks":null}"#,
        r#"{"enabled":true,"blocks":{}}"#,
    ] {
        let state: Maintenance = serde_json::from_str(raw).unwrap();
        assert!(state.blocks.install && state.blocks.update && !state.blocks.launch);
    }
    let m:Maintenance=serde_json::from_str(r#"{"enabled":true,"reason":"Меняем диск","serverTime":"2001-01-01T10:00:00Z","endsAt":"2001-01-01T11:00:00Z","blocks":{"install":false,"update":false}}"#).unwrap();
    assert!(m.banner().starts_with("Меняем диск. Ожидаемое окончание"));
    assert!(!m.blocks.install && !m.blocks.update);
    let expired = Maintenance {
        server_time: "2001-01-01T12:00:00Z".into(),
        ..m
    };
    assert!(expired.banner().contains("затянулись"));
    assert!(
        !ApiError::Http(503, String::new())
            .to_string()
            .contains("подключение")
    );
}
#[test]
fn game_build_order_matches_legacy_numeric_segments() {
    use std::cmp::Ordering::*;
    assert_eq!(compare_version_segments("1.10.0", "1.9.9"), Greater);
    assert_eq!(compare_version_segments("1.2", "1.2.0"), Equal);
    assert_eq!(compare_version_segments("1.2", "1.2-rc"), Greater);
    assert_eq!(compare_version_segments("v1-RC", "v1-rc"), Equal);
    assert_eq!(compare_version_segments("2.0", "release"), Greater);
}
#[test]
fn payload_urls_cannot_change_origin_or_include_credentials() {
    let client = ApiClient::new("https://launcher.samoy.love").unwrap();
    assert!(client.resolve("/manifests/peak/1.json").is_ok());
    for bad in [
        "https://other.example/file",
        "//other.example/file",
        "https://user@launcher.samoy.love/file",
        "file:///C:/temp",
    ] {
        assert!(client.resolve(bad).is_err(), "{bad}");
    }
    assert!(ApiClient::new("http://example.org").is_err());
    assert!(ApiClient::new("http://127.0.0.1:12345").is_ok());
}
#[test]
fn catalogs_from_different_servers_never_share_cache() {
    let a = ApiClient::new("https://one.example").unwrap();
    let b = ApiClient::new("https://two.example").unwrap();
    assert_ne!(
        a.cache_path(Path::new("cache"), "catalog"),
        b.cache_path(Path::new("cache"), "catalog")
    );
}
#[test]
fn invalid_catalog_ids_never_become_paths() {
    for bad in ["", "..", "../game", "C:game", "_registry", "game/a", "a\\b"] {
        assert!(!safe_id(bad));
    }
    assert!(safe_id("lethal-company"));
}

#[test]
fn partial_catalog_preserves_visible_games_without_marking_entries_complete() {
    let valid = serde_json::json!({"gameId":"known", "title":"Known Game"});
    let (catalog, all_entries_valid) = validate_catalog(
        serde_json::json!({"items":[valid.clone(), {"title":"Unidentified Game"}]}),
    )
    .unwrap();
    assert_eq!(catalog.items.len(), 1);
    assert_eq!(catalog.items[0].game_id, "known");
    assert!(!all_entries_valid);
    assert!(
        validate_catalog(serde_json::json!({"items":[valid]}))
            .unwrap()
            .1
    );
}
