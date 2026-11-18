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

fn with_http_responses(
    responses: Vec<(&'static str, tiny_http::Response<std::io::Cursor<Vec<u8>>>)>,
    check: impl FnOnce(&ApiClient),
) {
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let base = format!("http://{}", server.server_addr());
    let api = ApiClient::new(&base).unwrap();
    std::thread::scope(|scope| {
        scope.spawn(move || {
            for (path, response) in responses {
                let request = server
                    .recv_timeout(Duration::from_secs(8))
                    .unwrap()
                    .unwrap_or_else(|| panic!("expected request for {path}"));
                assert_eq!(request.url(), path);
                let id = request
                    .headers()
                    .iter()
                    .find(|header| header.field.equiv("X-Request-ID"))
                    .unwrap();
                assert!(uuid::Uuid::parse_str(id.value.as_str()).is_ok());
                let _ = request.respond(response);
            }
        });
        check(&api);
    });
}

#[test]
fn invalid_maintenance_responses_keep_the_last_confirmed_service_status() {
    let route = "/api/maintenance";
    with_http_responses(
        vec![
            (
                route,
                tiny_http::Response::from_string(
                    r#"{"enabled":true,"reason":"Плановые работы!","blocks":{"launch":true}}"#,
                ),
            ),
            (
                route,
                tiny_http::Response::from_string(r#"{"enabled":"false"}"#),
            ),
            (
                route,
                tiny_http::Response::from_string(r#"{"enabled":false,"blocks":{"launch":"no"}}"#),
            ),
            (
                route,
                tiny_http::Response::from_string(r#"{"enabled":false,"blocks":"wrong"}"#),
            ),
            (
                route,
                tiny_http::Response::from_string(r#"{"enabled":false}"#),
            ),
        ],
        |api| {
            assert!(api.confirmed_maintenance().is_none());
            let state = api.maintenance().unwrap();
            assert!(state.enabled && state.blocks.launch);
            assert_eq!(state.banner(), "Плановые работы!");
            for expected in [
                "неполный статус",
                "повреждённый статус",
                "повреждённый статус",
            ] {
                let error = api.maintenance().unwrap_err();
                assert!(
                    matches!(&error, ApiError::Invalid(text) if text.contains(expected)),
                    "{error:?}"
                );
                let last = api.confirmed_maintenance().unwrap();
                assert!(last.enabled && last.blocks.launch);
            }
            assert!(!api.maintenance().unwrap().enabled);
            assert!(!api.confirmed_maintenance().unwrap().enabled);
        },
    );
}

#[test]
fn json_api_bounds_response_and_keeps_server_details_out_of_player_errors() {
    let route = "/api/probe";
    with_http_responses(
        vec![
            (
                route,
                tiny_http::Response::from_string("server diagnostic".repeat(60))
                    .with_status_code(429),
            ),
            (route, tiny_http::Response::from_string("not JSON")),
            (
                route,
                tiny_http::Response::from_data(vec![b' '; MAX_JSON as usize + 1]),
            ),
        ],
        |api| {
            let busy = api.json::<serde_json::Value>(route).unwrap_err();
            assert!(matches!(&busy, ApiError::Http(429, detail) if detail.len() == 512));
            assert_eq!(
                busy.to_string(),
                "Слишком много запросов. Попробуйте чуть позже."
            );
            let damaged = api.json::<serde_json::Value>(route).unwrap_err();
            assert!(
                matches!(&damaged, ApiError::Invalid(text) if text.contains("повреждённые данные"))
            );
            let oversized = api.json::<serde_json::Value>(route).unwrap_err();
            assert!(
                matches!(&oversized, ApiError::Invalid(text) if text.contains("слишком большой"))
            );
        },
    );
}

#[test]
fn api_follows_same_origin_redirects_with_a_finite_budget() {
    fn redirect(to: &str) -> tiny_http::Response<std::io::Cursor<Vec<u8>>> {
        tiny_http::Response::from_data(Vec::new())
            .with_status_code(302)
            .with_header(tiny_http::Header::from_bytes("Location", to).unwrap())
    }

    with_http_responses(
        vec![
            ("/start", redirect("/json")),
            ("/json", tiny_http::Response::from_string("42")),
            ("/loop", redirect("/loop")),
            ("/loop", redirect("/loop")),
            ("/loop", redirect("/loop")),
            ("/loop", redirect("/loop")),
        ],
        |api| {
            assert_eq!(api.json::<u32>("/start").unwrap(), 42);
            let error = api.json::<u32>("/loop").unwrap_err();
            assert!(
                matches!(&error, ApiError::Network(detail) if detail.contains("Слишком много перенаправлений"))
            );
            assert_eq!(
                error.to_string(),
                "Не удалось связаться с сервером. Попробуйте ещё раз."
            );
        },
    );
}

#[test]
fn api_rejects_redirects_to_another_origin_and_to_urls_with_credentials() {
    let other = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let host = server.server_addr();
    let api = ApiClient::new(&format!("http://{host}")).unwrap();
    std::thread::scope(|scope| {
        scope.spawn(|| {
            for location in [
                format!("http://{}/outside", other.server_addr()),
                format!("http://username:password@{host}/private"),
            ] {
                let request = server
                    .recv_timeout(Duration::from_secs(8))
                    .unwrap()
                    .unwrap();
                assert_eq!(request.url(), "/redirect");
                request
                    .respond(
                        tiny_http::Response::empty(302).with_header(
                            tiny_http::Header::from_bytes("Location", location).unwrap(),
                        ),
                    )
                    .unwrap();
            }
        });
        for _ in 0..2 {
            let error = api
                .json_with_timeout::<u32>("/redirect", Duration::from_secs(5))
                .unwrap_err();
            assert!(
                matches!(&error, ApiError::Network(detail) if detail.contains("на другой адрес") && !detail.contains("password")),
                "{error:?}"
            );
        }
    });
    assert!(other.try_recv().unwrap().is_none());
    assert!(server.try_recv().unwrap().is_none());
}
