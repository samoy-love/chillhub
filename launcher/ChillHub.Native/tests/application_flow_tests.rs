use chillhub_native::{
    config::{AppConfig, ConfigStore, LaunchTarget},
    feedback::{FeedbackDraft, FeedbackService, SendResult},
    launch::{self, LaunchAction},
    manifest::{Manifest, ManifestFile},
    model::{GameRecord, LibraryResponse, ModsRecord},
    net::ApiClient,
    paths::AppPaths,
    queue::QueueState,
    service::Services,
};
use sha2::{Digest, Sha256};
use std::{
    collections::HashMap,
    fs,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

type RecordedRequests = Arc<Mutex<Vec<(String, Vec<u8>)>>>;
type Routes = Arc<Mutex<HashMap<String, (u16, Vec<u8>)>>>;
struct Fixture {
    base: String,
    stop: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
    requests: RecordedRequests,
    routes: Routes,
}
impl Fixture {
    fn new(routes: HashMap<String, (u16, Vec<u8>)>) -> Self {
        let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let base = format!("http://{}", server.server_addr());
        let stop = Arc::new(AtomicBool::new(false));
        let stopped = stop.clone();
        let requests = Arc::new(Mutex::new(Vec::new()));
        let captured = requests.clone();
        let routes = Arc::new(Mutex::new(routes));
        let responses = routes.clone();
        let worker = thread::spawn(move || {
            while !stopped.load(Ordering::Relaxed) {
                if let Some(mut request) = server.recv_timeout(Duration::from_millis(20)).unwrap() {
                    let path = request.url().to_string();
                    let mut body = Vec::new();
                    request.as_reader().read_to_end(&mut body).unwrap();
                    captured.lock().unwrap().push((path.clone(), body));
                    let (status, body) = responses
                        .lock()
                        .unwrap()
                        .get(&path)
                        .cloned()
                        .unwrap_or((404, Vec::new()));
                    let _ = request
                        .respond(tiny_http::Response::from_data(body).with_status_code(status));
                }
            }
        });
        Self {
            base,
            stop,
            worker: Some(worker),
            requests,
            routes,
        }
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
fn file(path: &str, data: &[u8]) -> ManifestFile {
    ManifestFile {
        path: path.into(),
        size: data.len() as u64,
        blake3: blake3::hash(data).to_hex().to_string(),
        sha256: Some(hex::encode(Sha256::digest(data))),
        ..Default::default()
    }
}
fn fixture_game() -> (Fixture, GameRecord) {
    let exe = b"test-only file, never executed";
    let loader = b"test-only loader";
    let config = b"[General]\nenabled=true\n";
    let base = Manifest {
        game_id: "fixture".into(),
        version: "1.0.0".into(),
        files: vec![file("Game.exe", exe)],
        ..Default::default()
    };
    let mods = Manifest {
        game_id: "fixture".into(),
        version: "pack-1.0.0".into(),
        files: vec![
            file("winhttp.dll", loader),
            file("doorstop_config.ini", config),
        ],
        ..Default::default()
    };
    let mut sorted = mods.files.iter().collect::<Vec<_>>();
    sorted.sort_by(|a, b| a.path.cmp(&b.path));
    let mut digest = Sha256::new();
    for f in sorted {
        digest.update(format!("{}\n{}\n", f.path, f.blake3));
    }
    let revision = hex::encode(&digest.finalize()[..16]);
    let game = GameRecord {
        game_id: "fixture".into(),
        title: "Проверочная игра".into(),
        has_latest: true,
        latest_version: base.version.clone(),
        manifest_url: "/manifests/fixture/1.0.0.json".into(),
        exe_relative_path: "Game.exe".into(),
        mods: Some(ModsRecord {
            has_latest: true,
            version: mods.version.clone(),
            revision,
            manifest_url: "/manifests/_mods/fixture/pack-1.0.0.json".into(),
            content_base_url: "/content/_mods/fixture/pack-1.0.0/files".into(),
            steam_app_id: "999999999".into(),
            exe_names: vec!["Game.exe".into()],
            ..Default::default()
        }),
        ..Default::default()
    };
    let routes = HashMap::from([
        (
            "/api/maintenance".into(),
            (
                200,
                br#"{"enabled":false,"blocks":{"install":false,"update":false,"launch":false}}"#
                    .to_vec(),
            ),
        ),
        (
            "/api/games".into(),
            (
                200,
                serde_json::to_vec(&LibraryResponse {
                    items: vec![game.clone()],
                })
                .unwrap(),
            ),
        ),
        (
            "/api/games/fixture/builds".into(),
            (200, br#"{"items":["1.0.0"]}"#.to_vec()),
        ),
        (
            game.manifest_url.clone(),
            (200, serde_json::to_vec(&base).unwrap()),
        ),
        (
            game.mods.as_ref().unwrap().manifest_url.clone(),
            (200, serde_json::to_vec(&mods).unwrap()),
        ),
        (
            "/content/fixture/1.0.0/files/Game.exe".into(),
            (200, exe.to_vec()),
        ),
        (
            "/content/_mods/fixture/pack-1.0.0/files/winhttp.dll".into(),
            (200, loader.to_vec()),
        ),
        (
            "/content/_mods/fixture/pack-1.0.0/files/doorstop_config.ini".into(),
            (200, config.to_vec()),
        ),
    ]);
    (Fixture::new(routes), game)
}
fn services(paths: &AppPaths, base: &str) -> Services {
    ConfigStore::new(paths.clone())
        .save(&AppConfig {
            games_path: paths.games_dir.to_string_lossy().into_owned(),
            api_base_url: base.into(),
            ..Default::default()
        })
        .unwrap();
    Services::new(paths.clone()).unwrap().0
}
fn wait_completed(services: &Services) {
    let until = Instant::now() + Duration::from_secs(8);
    loop {
        let tasks = services.queue.snapshot();
        assert!(
            !tasks
                .iter()
                .any(|t| matches!(t.state, QueueState::Failed | QueueState::NeedsConfirmation)),
            "{tasks:?}"
        );
        if tasks.iter().all(|t| t.state == QueueState::Completed) {
            break;
        }
        assert!(Instant::now() < until, "{tasks:?}");
        thread::sleep(Duration::from_millis(20));
    }
}

#[test]
fn verification_of_uninstalled_game_and_modpack_never_becomes_an_install() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let (server, mut game) = fixture_game();
    game.mods.as_mut().unwrap().steam_app_id.clear();
    let services = services(&paths, &server.base);
    assert!(
        services
            .enqueue_game(&game, LaunchTarget::LocalVanilla, None, true, false)
            .unwrap_err()
            .contains("ещё не установлена")
    );
    assert!(
        services
            .enqueue_game(&game, LaunchTarget::LocalModded, None, true, true)
            .unwrap_err()
            .contains("ещё не установлен")
    );
    assert!(services.queue.snapshot().is_empty());
    assert!(server.requests.lock().unwrap().is_empty());
    assert!(!paths.game_root("fixture").unwrap().exists());
}

#[test]
fn broken_modpack_ownership_is_rebuilt_without_deleting_unknown_files() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let (server, mut game) = fixture_game();
    game.mods.as_mut().unwrap().steam_app_id.clear();
    let root = paths.game_root("fixture").unwrap();
    fs::create_dir_all(&root).unwrap();
    fs::write(root.join("Game.exe"), b"test-only file, never executed").unwrap();
    fs::write(root.join(".version"), b"1.0.0").unwrap();
    fs::write(root.join(".mods.manifest.json"), b"{broken ownership").unwrap();
    fs::write(root.join("player-save.dat"), b"user data").unwrap();
    let services = services(&paths, &server.base);
    services
        .enqueue_game(&game, LaunchTarget::LocalModded, None, true, true)
        .unwrap();
    wait_completed(&services);
    assert_eq!(launch::installed_modpack_paths(&root).unwrap().len(), 2);
    assert_eq!(
        fs::read(root.join("player-save.dat")).unwrap(),
        b"user data"
    );
    assert_eq!(
        services
            .option(&game, LaunchTarget::LocalModded)
            .unwrap()
            .action,
        LaunchAction::Play
    );
}

#[test]
fn preflight_estimate_uses_published_files_without_installing_or_enqueuing() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let (server, mut game) = fixture_game();
    game.mods.as_mut().unwrap().steam_app_id.clear();
    let services = services(&paths, &server.base);
    let estimate = services
        .estimate_download(&game, LaunchTarget::LocalModded)
        .unwrap();
    let total = b"test-only file, never executed".len()
        + b"test-only loader".len()
        + b"[General]\nenabled=true\n".len();
    assert_eq!(estimate.bytes_to_prepare, total as u64);
    assert!(estimate.required_free_bytes >= total as u64);
    assert!(estimate.available_bytes.is_some());
    assert!(services.queue.snapshot().is_empty());
    assert!(!paths.game_root("fixture").unwrap().exists());
    assert!(
        server
            .requests
            .lock()
            .unwrap()
            .iter()
            .all(|(path, _)| path.starts_with("/manifests/"))
    );
    services
        .enqueue_game(&game, LaunchTarget::LocalModded, None, false, false)
        .unwrap();
    wait_completed(&services);
    let ready = services
        .estimate_download(&game, LaunchTarget::LocalModded)
        .unwrap();
    assert_eq!(ready.bytes_to_prepare, 0);
    assert_eq!(ready.required_free_bytes, 0);
}

#[test]
fn legacy_embedded_modpack_is_adopted_without_deletion_or_redownload() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let (server, mut game) = fixture_game();
    game.mods.as_mut().unwrap().steam_app_id.clear();
    let root = paths.game_root("fixture").unwrap();
    fs::create_dir_all(&root).unwrap();
    fs::write(root.join("Game.exe"), b"test-only file, never executed").unwrap();
    fs::write(root.join("winhttp.dll"), b"test-only loader").unwrap();
    fs::write(
        root.join("doorstop_config.ini"),
        b"[General]\nenabled=true\n",
    )
    .unwrap();
    fs::write(root.join(".version"), b"legacy").unwrap();
    let services = services(&paths, &server.base);
    let ids = services
        .enqueue_game(&game, LaunchTarget::LocalModded, None, false, false)
        .unwrap();
    assert_eq!(ids.len(), 2);
    wait_completed(&services);
    assert!(root.join(".mods.manifest.json").is_file());
    assert_eq!(
        services
            .option(&game, LaunchTarget::LocalModded)
            .unwrap()
            .action,
        LaunchAction::Play
    );
    assert!(
        server
            .requests
            .lock()
            .unwrap()
            .iter()
            .all(|(path, _)| !path.starts_with("/content/")),
        "already bundled bytes must be claimed without being deleted and downloaded again"
    );
}

#[test]
fn background_integrity_detects_missing_dll_offline_and_preserves_user_files() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let (server, mut game) = fixture_game();
    game.mods = None;
    let dll = b"verified Unity runtime fixture";
    {
        let mut routes = server.routes.lock().unwrap();
        let mut manifest: Manifest = serde_json::from_slice(&routes[&game.manifest_url].1).unwrap();
        manifest.files.push(file("UnityPlayer.dll", dll));
        routes.insert(
            game.manifest_url.clone(),
            (200, serde_json::to_vec(&manifest).unwrap()),
        );
        routes.insert(
            "/content/fixture/1.0.0/files/UnityPlayer.dll".into(),
            (200, dll.to_vec()),
        );
    }
    let services = services(&paths, &server.base);
    services
        .enqueue_game(&game, LaunchTarget::LocalVanilla, None, false, false)
        .unwrap();
    wait_completed(&services);
    let root = paths.game_root("fixture").unwrap();
    assert!(!services.verify_game_status(&game).unwrap());
    assert!(!services.verify_game_status(&game).unwrap());
    let requests_before = server.requests.lock().unwrap().len();
    server
        .routes
        .lock()
        .unwrap()
        .insert(game.manifest_url.clone(), (503, vec![]));
    fs::remove_file(root.join("UnityPlayer.dll")).unwrap();
    fs::write(root.join("my-save.dat"), b"never remove me").unwrap();
    assert!(services.verify_game_status(&game).unwrap());
    assert_eq!(
        services
            .option(&game, LaunchTarget::LocalVanilla)
            .unwrap()
            .action,
        LaunchAction::UpdateGame
    );
    assert_eq!(
        server.requests.lock().unwrap().len(),
        requests_before,
        "cached manifest permits offline checks without another network request"
    );
    #[cfg(windows)]
    {
        use std::os::windows::ffi::{OsStrExt, OsStringExt};
        let input: Vec<u16> = paths
            .games_dir
            .as_os_str()
            .encode_wide()
            .chain([0])
            .collect();
        let mut output = vec![0; 32768];
        let length = unsafe {
            windows_sys::Win32::Storage::FileSystem::GetShortPathNameW(
                input.as_ptr(),
                output.as_mut_ptr(),
                output.len() as u32,
            )
        } as usize;
        assert!(length > 0 && length < output.len());
        let short = std::path::PathBuf::from(std::ffi::OsString::from_wide(&output[..length]));
        let mut aliased = services.clone();
        aliased.paths.games_dir = short;
        assert_eq!(
            aliased
                .option(&game, LaunchTarget::LocalVanilla)
                .unwrap()
                .action,
            LaunchAction::UpdateGame,
            "the same broken game remains broken through its short path",
        );
        let aliased_root = aliased.paths.game_root("fixture").unwrap();
        chillhub_native::integrity::invalidate(&aliased_root);
        assert!(!chillhub_native::integrity::needs_repair(&root, "1.0.0"));
        assert!(aliased.verify_game_status(&game).unwrap());
        assert!(chillhub_native::integrity::needs_repair(&root, "1.0.0"));
        assert_eq!(
            server.requests.lock().unwrap().len(),
            requests_before,
            "changing only the directory spelling reuses the offline manifest"
        );
    }
    chillhub_native::integrity::invalidate(&root); // Simulate an application restart.
    assert!(services.verify_game_status(&game).unwrap());
    fs::write(root.join("UnityPlayer.dll"), dll).unwrap();
    assert!(!services.verify_game_status(&game).unwrap());
    assert_eq!(
        services
            .option(&game, LaunchTarget::LocalVanilla)
            .unwrap()
            .action,
        LaunchAction::Play
    );
    assert_eq!(
        fs::read(root.join("my-save.dat")).unwrap(),
        b"never remove me"
    );
}

#[test]
fn published_game_and_modpack_install_become_real_launch_options() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let (server, game) = fixture_game();
    let services = services(&paths, &server.base);
    assert_eq!(
        services
            .option(&game, LaunchTarget::LocalModded)
            .unwrap()
            .action,
        LaunchAction::InstallGame
    );
    let ids = services
        .enqueue_game(&game, LaunchTarget::LocalModded, None, false, false)
        .unwrap();
    assert_eq!(ids.len(), 2);
    wait_completed(&services);
    assert_eq!(
        services
            .option(&game, LaunchTarget::LocalVanilla)
            .unwrap()
            .action,
        LaunchAction::Play
    );
    assert_eq!(
        services
            .option(&game, LaunchTarget::LocalModded)
            .unwrap()
            .action,
        LaunchAction::Play
    );
    let root = paths.game_root("fixture").unwrap();
    assert!(!root.join(".updating").exists());
    assert!(
        fs::read_to_string(root.join("doorstop_config.ini"))
            .unwrap()
            .contains("enabled=false")
    );
    assert!(
        launch::LaunchService::new(paths.clone())
            .plan(&game, LaunchTarget::LocalModded)
            .is_ok()
    );
    // A valid flag altered by the launcher is preserved despite its changed hash.
    fs::write(
        root.join("doorstop_config.ini"),
        "[General]\nenabled=false\n; player setting\n",
    )
    .unwrap();
    services
        .enqueue_game(&game, LaunchTarget::LocalModded, None, true, true)
        .unwrap();
    wait_completed(&services);
    assert!(
        fs::read_to_string(root.join("doorstop_config.ini"))
            .unwrap()
            .contains("player setting")
    );
    let ini_route = "/content/_mods/fixture/pack-1.0.0/files/doorstop_config.ini";
    assert_eq!(
        server
            .requests
            .lock()
            .unwrap()
            .iter()
            .filter(|(p, _)| p == ini_route)
            .count(),
        1
    );
    // An unusable INI must not be preserved forever; explicit repair restores it.
    fs::write(
        root.join("doorstop_config.ini"),
        "broken loader configuration",
    )
    .unwrap();
    assert!(
        !services
            .option(&game, LaunchTarget::LocalModded)
            .unwrap()
            .ready()
    );
    services
        .enqueue_game(&game, LaunchTarget::LocalModded, None, true, true)
        .unwrap();
    wait_completed(&services);
    assert!(
        services
            .option(&game, LaunchTarget::LocalModded)
            .unwrap()
            .ready()
    );
    assert_eq!(
        server
            .requests
            .lock()
            .unwrap()
            .iter()
            .filter(|(p, _)| p == ini_route)
            .count(),
        2
    );
    // Verification reuses the exact published files and never starts the executable.
    assert!(!paths.data_dir.join("playtime.sessions.json").exists());
    services.queue.shutdown();
}

#[test]
fn library_is_cached_per_origin_and_still_available_during_outage() {
    let temp = tempfile::tempdir().unwrap();
    let (server, game) = fixture_game();
    let api = ApiClient::new(&server.base).unwrap();
    let snapshot = api.catalog(temp.path()).unwrap();
    assert!(!snapshot.offline);
    assert_eq!(snapshot.library.items[0].game_id, game.game_id);
    server
        .routes
        .lock()
        .unwrap()
        .insert("/api/games".into(), (503, Vec::new()));
    let before = server.requests.lock().unwrap().len();
    let cached = api.cached_catalog(temp.path()).unwrap();
    assert!(cached.offline);
    assert_eq!(server.requests.lock().unwrap().len(), before);
    let offline = api.catalog(temp.path()).unwrap();
    assert!(offline.offline);
    assert_eq!(offline.library.items[0].latest_version, "1.0.0");
    let other = ApiClient::new("http://127.0.0.1:9").unwrap();
    assert!(other.cached_catalog(temp.path()).is_none());
}

#[test]
fn maintenance_blocks_install_before_queue_or_game_files_change() {
    let server=Fixture::new(HashMap::from([("/api/maintenance".into(),(200,br#"{"enabled":true,"reason":"Maintenance","blocks":{"install":true,"update":false,"launch":false}}"#.to_vec()))]));
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let services = services(&paths, &server.base);
    let game = GameRecord {
        game_id: "fixture".into(),
        has_latest: true,
        latest_version: "1.0.0".into(),
        exe_relative_path: "Game.exe".into(),
        ..Default::default()
    };
    assert!(
        services
            .enqueue_game(&game, LaunchTarget::LocalVanilla, None, false, false)
            .is_err()
    );
    assert!(services.queue.snapshot().is_empty());
    assert!(!paths.games_dir.exists());
    services.queue.shutdown();
}

#[test]
fn feedback_distinguishes_rejection_rate_limit_and_saved_offline_queue() {
    for (status, expected) in [
        (200, "sent"),
        (400, "rejected"),
        (429, "limited"),
        (503, "queued"),
    ] {
        let server = Fixture::new(HashMap::from([(
            "/feedback/submit".into(),
            (status, b"{}".to_vec()),
        )]));
        let temp = tempfile::tempdir().unwrap();
        let feedback =
            FeedbackService::new(ApiClient::new(&server.base).unwrap(), temp.path().into())
                .with_local_admin_port(None);
        let result = feedback.submit(FeedbackDraft {
            comment: "Тест на локальном сервере".into(),
            attach_logs: false,
            ..Default::default()
        });
        match expected {
            "sent" => assert_eq!(result, SendResult::Sent),
            "rejected" => assert!(matches!(result, SendResult::Rejected(_))),
            "limited" => assert!(matches!(result, SendResult::RateLimited(_))),
            _ => assert_eq!(result, SendResult::QueuedPersisted),
        }
        assert_eq!(feedback.pending().unwrap(), usize::from(status == 503));
        let body: serde_json::Value =
            serde_json::from_slice(&server.requests.lock().unwrap()[0].1).unwrap();
        assert_eq!(body["attachLogs"], false);
        assert_eq!(body["logs"], "");
        if status == 503 {
            server
                .routes
                .lock()
                .unwrap()
                .insert("/feedback/submit".into(), (200, b"{}".to_vec()));
            assert_eq!(feedback.flush().unwrap(), 1);
            assert_eq!(feedback.pending().unwrap(), 0);
            let requests = server.requests.lock().unwrap();
            assert_eq!(requests.len(), 2);
            assert_eq!(requests[0].1, requests[1].1);
        }
    }
}

#[test]
fn broken_catalog_never_replaces_known_games_and_cache_has_the_same_validation() {
    let temp = tempfile::tempdir().unwrap();
    let (server, game) = fixture_game();
    let api = ApiClient::new(&server.base).unwrap();
    api.catalog(temp.path()).unwrap();
    let cache = api.cache_path(temp.path(), "catalog");
    let original = fs::read(&cache).unwrap();
    server
        .routes
        .lock()
        .unwrap()
        .insert("/api/games".into(), (200, b"{}".to_vec()));
    let fallback = api.catalog(temp.path()).unwrap();
    assert!(fallback.offline);
    assert_eq!(fallback.library.items[0].game_id, game.game_id);
    assert_eq!(fs::read(&cache).unwrap(), original);
    let mut payload: serde_json::Value = serde_json::from_slice(&original).unwrap();
    let mut unsafe_game = serde_json::to_value(&game).unwrap();
    unsafe_game["gameId"] = "../outside".into();
    payload["response"]["items"]
        .as_array_mut()
        .unwrap()
        .extend([serde_json::to_value(&game).unwrap(), unsafe_game]);
    fs::write(&cache, serde_json::to_vec(&payload).unwrap()).unwrap();
    assert_eq!(
        api.cached_catalog(temp.path()).unwrap().library.items.len(),
        1
    );
    fs::OpenOptions::new()
        .write(true)
        .open(&cache)
        .unwrap()
        .set_len(16 * 1024 * 1024 + 1)
        .unwrap();
    assert!(api.cached_catalog(temp.path()).is_none());
    fs::remove_file(cache).unwrap();
    assert!(api.catalog(temp.path()).is_err());
}

#[test]
fn unavailable_modpack_or_unsafe_url_never_enqueues_partial_base_install() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let (server, mut game) = fixture_game();
    let services = services(&paths, &server.base);
    game.mods.as_mut().unwrap().has_latest = false;
    assert!(
        services
            .enqueue_game(&game, LaunchTarget::LocalModded, None, false, false)
            .is_err()
    );
    game.mods.as_mut().unwrap().has_latest = true;
    game.mods.as_mut().unwrap().manifest_url = "https://outside.invalid/manifest.json".into();
    assert!(
        services
            .enqueue_game(&game, LaunchTarget::LocalModded, None, false, false)
            .is_err()
    );
    assert!(services.queue.snapshot().is_empty());
    assert!(!paths.games_dir.exists());
    services.queue.shutdown();
}

#[test]
fn first_modpack_install_obeys_update_block_for_an_existing_game() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let (server, game) = fixture_game();
    let services = services(&paths, &server.base);
    let root = paths.game_root("fixture").unwrap();
    fs::create_dir_all(&root).unwrap();
    fs::write(root.join(".version"), "1.0.0").unwrap();
    fs::write(root.join("Game.exe"), b"not executable").unwrap();
    server.routes.lock().unwrap().insert("/api/maintenance".into(),(200,br#"{"enabled":true,"message":"Update blocked","blocks":{"install":false,"update":true}}"#.to_vec()));
    assert!(
        services
            .enqueue_game(&game, LaunchTarget::LocalModded, None, false, true)
            .unwrap_err()
            .contains("Update blocked")
    );
    assert!(services.queue.snapshot().is_empty());
    services.queue.shutdown();
}

#[cfg(windows)]
#[test]
fn legacy_shortcut_launches_nested_fixture_and_tracks_exact_process_identity() {
    // This test never launches ChillHub or a real game. It compiles a tiny,
    // windowless TEMP fixture which writes its working directory and exits.
    use chillhub_native::{
        launch::{LaunchService, ShortcutRequest},
        playtime::{PlaytimeStore, ProcessIdentity},
    };
    use std::process::Command;
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let game_root = paths.game_root("fixture").unwrap();
    let nested = game_root.join("bin");
    fs::create_dir_all(&nested).unwrap();
    let source = temp.path().join("fixture.rs");
    let executable = nested.join("Fixture.exe");
    fs::write(&source,r##"#![windows_subsystem="windows"]
fn main(){std::fs::write("started.txt",std::env::current_dir().unwrap().to_string_lossy().as_bytes()).unwrap();let end=std::time::Instant::now()+std::time::Duration::from_secs(10);while !std::path::Path::new("stop").exists()&&std::time::Instant::now()<end{std::thread::sleep(std::time::Duration::from_millis(20));}}"##).unwrap();
    let mut compiler = Command::new(std::env::var_os("RUSTC").unwrap_or_else(|| "rustc".into()));
    compiler
        .arg(&source)
        .arg("--crate-name=launch_fixture")
        .arg("-o")
        .arg(&executable);
    if let Some(linker) = std::env::var_os("CARGO_TARGET_X86_64_PC_WINDOWS_GNU_LINKER") {
        compiler
            .arg("-C")
            .arg(format!("linker={}", linker.to_string_lossy()))
            .args(["-C", "linker-flavor=ld", "-C", "link-self-contained=yes"]);
        let dlltool = std::path::Path::new(&linker)
            .parent()
            .unwrap()
            .join("llvm-dlltool.exe");
        compiler
            .arg("-C")
            .arg(format!("dlltool={}", dlltool.display()));
    }
    let output = compiler.output().unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    struct StopFixture(std::path::PathBuf);
    impl Drop for StopFixture {
        fn drop(&mut self) {
            let _ = fs::write(&self.0, b"stop");
        }
    }
    let stop = StopFixture(nested.join("stop"));
    let request = ShortcutRequest {
        game_id: "fixture".into(),
        title: "Offline legacy fixture".into(),
        exe_path: Some(executable.clone()),
        target: Some(LaunchTarget::LocalVanilla),
    };
    let launcher = LaunchService::new(paths.clone());
    let session = launcher.launch_shortcut(&request).unwrap();
    assert!(session.warning.is_none());
    assert!(session.process.is_running());
    assert_eq!(
        ProcessIdentity::capture(session.process.pid).unwrap(),
        session.process
    );
    let mismatched = ProcessIdentity {
        creation_file_time: session.process.creation_file_time + 1,
        ..session.process.clone()
    };
    assert!(!mismatched.is_running());
    let until = Instant::now() + Duration::from_secs(3);
    while !nested.join("started.txt").exists() {
        assert!(Instant::now() < until);
        thread::sleep(Duration::from_millis(20));
    }
    assert_eq!(
        fs::canonicalize(fs::read_to_string(nested.join("started.txt")).unwrap()).unwrap(),
        fs::canonicalize(&nested).unwrap()
    );
    let store = PlaytimeStore::new(&paths);
    let pending = store.pending().unwrap();
    let saved = pending.get(&session.process.pid.to_string()).unwrap();
    assert_eq!(
        saved.process_creation_file_time,
        Some(session.process.creation_file_time)
    );
    assert_eq!(saved.game_id, "fixture");
    assert!(launcher.launch_shortcut(&request).is_err());
    drop(stop);
    let until = Instant::now() + Duration::from_secs(5);
    while session.process.is_running() || !store.pending().unwrap().is_empty() {
        assert!(Instant::now() < until);
        thread::sleep(Duration::from_millis(30));
    }
    assert!(
        store
            .entries()
            .unwrap()
            .contains_key("fixture#LocalVanilla")
    );
}

#[test]
fn explicitly_selected_older_version_stays_playable_and_verification_keeps_the_pin() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let (server, game) = fixture_game();
    let services = services(&paths, &server.base);
    let old_bytes = b"older published executable fixture";
    let old = Manifest {
        game_id: game.game_id.clone(),
        version: "0.9.0".into(),
        files: vec![file("Game.exe", old_bytes)],
        ..Default::default()
    };
    server.routes.lock().unwrap().extend([
        (
            "/api/games/fixture/builds".into(),
            (200, br#"{"items":["1.0.0","0.9.0"]}"#.to_vec()),
        ),
        (
            "/manifests/fixture/0.9.0.json".into(),
            (200, serde_json::to_vec(&old).unwrap()),
        ),
        (
            "/content/fixture/0.9.0/files/Game.exe".into(),
            (200, old_bytes.to_vec()),
        ),
    ]);
    services
        .enqueue_game(
            &game,
            LaunchTarget::LocalVanilla,
            Some("0.9.0"),
            false,
            false,
        )
        .unwrap();
    wait_completed(&services);
    assert_eq!(
        services.config.load().unwrap().selected_version("FIXTURE"),
        Some("0.9.0")
    );
    assert!(
        services
            .option(&game, LaunchTarget::LocalVanilla)
            .unwrap()
            .ready()
    );
    assert!(
        launch::LaunchService::new(paths.clone())
            .plan(&game, LaunchTarget::LocalVanilla)
            .is_ok()
    );
    services
        .enqueue_game(&game, LaunchTarget::LocalVanilla, None, true, false)
        .unwrap();
    wait_completed(&services);
    let root = paths.game_root(&game.game_id).unwrap();
    assert_eq!(fs::read(root.join("Game.exe")).unwrap(), old_bytes);
    // No network is needed to plan a previously installed selected version.
    server
        .routes
        .lock()
        .unwrap()
        .insert("/api/games".into(), (503, Vec::new()));
    assert!(
        launch::LaunchService::new(paths.clone())
            .plan(&game, LaunchTarget::LocalVanilla)
            .is_ok()
    );
    services
        .enqueue_game(
            &game,
            LaunchTarget::LocalVanilla,
            Some("1.0.0"),
            false,
            false,
        )
        .unwrap();
    wait_completed(&services);
    assert_eq!(
        services
            .config
            .load()
            .unwrap()
            .selected_version(&game.game_id),
        None
    );
    assert_eq!(launch::read_marker(&root, ".version"), "1.0.0");
    assert!(
        services
            .option(&game, LaunchTarget::LocalVanilla)
            .unwrap()
            .ready()
    );
    services.queue.shutdown();
}

#[test]
fn removing_completed_dependency_keeps_mods_runnable_but_removing_failed_one_does_not() {
    use chillhub_native::{
        queue::{DownloadQueue, QueueItem},
        transfer::{SyncScope, TransferRequest},
    };
    for completed in [true, false] {
        let temp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(temp.path());
        let (server, game) = fixture_game();
        let root = paths.game_root(&game.game_id).unwrap();
        fs::create_dir_all(&root).unwrap();
        fs::write(root.join("Game.exe"), b"previously installed fixture").unwrap();
        let queue_path = paths.data_dir.join("download-queue.json");
        fs::create_dir_all(&paths.data_dir).unwrap();
        let mods = game.mods.as_ref().unwrap();
        let items = vec![
            QueueItem {
                id: "base".into(),
                request: TransferRequest::default(),
                state: if completed {
                    QueueState::Completed
                } else {
                    QueueState::Failed
                },
                progress: None,
                outcome: None,
                error: None,
                cleanup_paths: vec![],
                report: Default::default(),
            },
            QueueItem {
                id: "mods".into(),
                request: TransferRequest {
                    game_id: game.game_id.clone(),
                    version: mods.version.clone(),
                    root: root.clone(),
                    manifest_url: format!("{}{}", server.base, mods.manifest_url),
                    content_base_url: format!("{}{}", server.base, mods.content_base_url),
                    scope: SyncScope::OwnFilesOnly,
                    depends_on: Some("base".into()),
                    ..Default::default()
                },
                state: QueueState::Paused,
                progress: None,
                outcome: None,
                error: None,
                cleanup_paths: vec![],
                report: Default::default(),
            },
        ];
        fs::write(&queue_path, serde_json::to_vec(&items).unwrap()).unwrap();
        let queue = DownloadQueue::new(&queue_path).unwrap();
        queue.remove("base").unwrap();
        queue.resume("mods").unwrap();
        let until = Instant::now() + Duration::from_secs(8);
        loop {
            let item = queue.snapshot().remove(0);
            if completed && item.state == QueueState::Completed {
                break;
            }
            if !completed && item.state == QueueState::Paused && item.error.is_some() {
                break;
            }
            assert!(Instant::now() < until, "{item:?}");
            assert_ne!(item.state, QueueState::Failed, "{item:?}");
            thread::sleep(Duration::from_millis(20));
        }
        assert_eq!(root.join("winhttp.dll").exists(), completed);
        if !completed {
            assert!(server.requests.lock().unwrap().is_empty());
        }
        queue.shutdown();
    }
}
