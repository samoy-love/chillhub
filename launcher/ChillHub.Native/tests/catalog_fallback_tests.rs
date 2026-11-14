use chillhub_native::{
    model::{GameRecord, LibraryResponse},
    net::ApiClient,
};
use std::{
    collections::HashMap,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::Duration,
};

type Routes = Arc<Mutex<HashMap<String, (u16, Vec<u8>)>>>;
struct Fixture {
    api: ApiClient,
    routes: Routes,
    requests: Arc<Mutex<Vec<String>>>,
    stop: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
}
impl Fixture {
    fn new(games: Vec<GameRecord>) -> Self {
        let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let api = ApiClient::new(&format!("http://{}", server.server_addr())).unwrap();
        let routes = Arc::new(Mutex::new(HashMap::<String, (u16, Vec<u8>)>::from([(
            "/api/games".into(),
            (
                200,
                serde_json::to_vec(&LibraryResponse { items: games }).unwrap(),
            ),
        )])));
        let requests = Arc::new(Mutex::new(Vec::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let (responses, captured, stopped) = (routes.clone(), requests.clone(), stop.clone());
        let worker = thread::spawn(move || {
            while !stopped.load(Ordering::Relaxed) {
                if let Some(request) = server.recv_timeout(Duration::from_millis(15)).unwrap() {
                    let path = request.url().to_owned();
                    captured.lock().unwrap().push(path.clone());
                    let (status, bytes) = responses
                        .lock()
                        .unwrap()
                        .get(&path)
                        .cloned()
                        .unwrap_or((404, vec![]));
                    let _ = request
                        .respond(tiny_http::Response::from_data(bytes).with_status_code(status));
                }
            }
        });
        Self {
            api,
            routes,
            requests,
            stop,
            worker: Some(worker),
        }
    }
    fn builds(&self, id: &str, values: serde_json::Value) {
        self.routes.lock().unwrap().insert(
            format!("/api/games/{id}/builds"),
            (200, serde_json::to_vec(&values).unwrap()),
        );
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(worker) = self.worker.take() {
            worker.join().unwrap();
        }
    }
}
fn game(id: &str, local: bool, latest: &str) -> GameRecord {
    GameRecord {
        game_id: id.into(),
        title: id.into(),
        has_latest: local,
        latest_version: latest.into(),
        ..Default::default()
    }
}

#[test]
fn missing_latest_uses_highest_safe_build_and_caches_normalized_metadata() {
    let server = Fixture::new(vec![
        game("fixture", true, ""),
        game("mods-only", false, ""),
        game("explicit", true, "1.0.0"),
    ]);
    server.builds(
        "fixture",
        serde_json::json!({"items":["1.9.0","1.2"," 1.10.0 ","../2.0","C:bad","","1.10.0"]}),
    );
    let temp = tempfile::tempdir().unwrap();
    let fresh = server.api.catalog(temp.path()).unwrap();
    assert!(!fresh.offline);
    assert!(fresh.warning.is_none());
    assert_eq!(fresh.library.items[0].latest_version, "1.10.0");
    assert_eq!(
        fresh.library.items[0].manifest_url,
        "/manifests/fixture/1.10.0.json"
    );
    assert!(!fresh.library.items[1].has_latest);
    assert!(fresh.library.items[1].latest_version.is_empty());
    assert_eq!(fresh.library.items[2].latest_version, "1.0.0");
    assert_eq!(
        &*server.requests.lock().unwrap(),
        &["/api/games", "/api/games/fixture/builds"]
    );
    let cached = server.api.cached_catalog(temp.path()).unwrap();
    assert!(cached.offline);
    assert_eq!(cached.library.items[0].latest_version, "1.10.0");
    assert_eq!(
        cached.library.items[0].manifest_url,
        fresh.library.items[0].manifest_url
    );
    assert_eq!(
        server.requests.lock().unwrap().len(),
        2,
        "offline read must not fetch builds"
    );
}

#[test]
fn unresolved_build_fallback_keeps_previous_offline_cache_and_other_games() {
    let server = Fixture::new(vec![game("fixture", true, "1.0.0")]);
    let temp = tempfile::tempdir().unwrap();
    server.api.catalog(temp.path()).unwrap();
    let cache_path = server.api.cache_path(temp.path(), "catalog");
    let before = std::fs::read(&cache_path).unwrap();
    server.routes.lock().unwrap().insert(
        "/api/games".into(),
        (
            200,
            serde_json::to_vec(&LibraryResponse {
                items: vec![game("fixture", true, ""), game("other", true, "2.0")],
            })
            .unwrap(),
        ),
    );
    // 404, invalid entries and an empty list are all unresolved, never invented versions.
    for values in [
        None,
        Some(serde_json::json!({"items":["../escape", ""]})),
        Some(serde_json::json!({"items":[]})),
    ] {
        if let Some(values) = values {
            server.builds("fixture", values);
        }
        let result = server.api.catalog(temp.path()).unwrap();
        assert_eq!(result.library.items[1].latest_version, "2.0");
        assert!(result.library.items[0].latest_version.is_empty());
        assert!(result.warning.is_some());
        assert_eq!(std::fs::read(&cache_path).unwrap(), before);
    }
    assert_eq!(
        server
            .api
            .cached_catalog(temp.path())
            .unwrap()
            .library
            .items[0]
            .latest_version,
        "1.0.0"
    );
}

#[test]
fn build_filename_reserved_url_characters_are_encoded_as_path_data() {
    let server = Fixture::new(vec![game("fixture", true, "")]);
    server.builds("fixture", serde_json::json!({"items":["build#1"]}));
    let temp = tempfile::tempdir().unwrap();
    let snapshot = server.api.catalog(temp.path()).unwrap();
    let game = &snapshot.library.items[0];
    assert_eq!(game.latest_version, "build#1");
    assert_eq!(game.manifest_url, "/manifests/fixture/build%231.json");
    assert!(
        server
            .api
            .resolve(&game.manifest_url)
            .unwrap()
            .fragment()
            .is_none()
    );
}
