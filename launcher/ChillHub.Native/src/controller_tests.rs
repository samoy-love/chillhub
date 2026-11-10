//! Controller integration tests use the headless testing backend and loopback fixtures.
use super::*;
use chillhub_native::{
    config::ConfigStore,
    manifest::{Manifest, ManifestFile},
    model::{LibraryResponse, ModsRecord},
    paths::AppPaths,
    queue::QueueState,
};
use sha2::{Digest, Sha256};
use slint::Model;
use std::{
    fs,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::Instant,
};

struct Loopback {
    base: String,
    stop: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
    requests: Arc<Mutex<Vec<String>>>,
    hold_content: Arc<AtomicBool>,
}
impl Loopback {
    fn new() -> Self {
        fn file(path: &str, bytes: &[u8]) -> ManifestFile {
            ManifestFile {
                path: path.into(),
                size: bytes.len() as u64,
                blake3: blake3::hash(bytes).to_hex().to_string(),
                sha256: Some(hex::encode(Sha256::digest(bytes))),
                ..Default::default()
            }
        }
        // Deliberately not a PE executable. The only Play callback below installs;
        // a second accidental launch cannot start any Windows process.
        let exe = b"controller fixture; this file cannot execute";
        let loader = b"fixture loader; not a DLL";
        let ini = b"[General]\nenabled=true\n";
        let game_manifest = Manifest {
            game_id: "fixture".into(),
            version: "1.0.0".into(),
            files: vec![file("Game.exe", exe)],
            ..Default::default()
        };
        let old_manifest = Manifest {
            version: "0.9.0".into(),
            ..game_manifest.clone()
        };
        let mods_manifest = Manifest {
            game_id: "fixture".into(),
            version: "pack-1.0.0".into(),
            files: vec![
                file("doorstop_config.ini", ini),
                file("winhttp.dll", loader),
            ],
            ..Default::default()
        };
        let mut revision = Sha256::new();
        for f in &mods_manifest.files {
            revision.update(format!("{}\n{}\n", f.path, f.blake3));
        }
        let records = vec![
            GameRecord {
                game_id: "plain".into(),
                title: "Без дополнений".into(),
                has_latest: true,
                latest_version: "1.0.0".into(),
                exe_relative_path: "Plain.exe".into(),
                manifest_url: "/manifests/plain/1.0.0.json".into(),
                ..Default::default()
            },
            GameRecord {
                game_id: "fixture".into(),
                title: "Проверочная игра".into(),
                has_latest: true,
                latest_version: "1.0.0".into(),
                exe_relative_path: "Game.exe".into(),
                manifest_url: "/manifests/fixture/1.0.0.json".into(),
                mods: Some(ModsRecord {
                    has_latest: true,
                    version: "pack-1.0.0".into(),
                    revision: hex::encode(&revision.finalize()[..16]),
                    manifest_url: "/manifests/_mods/fixture/pack-1.0.0.json".into(),
                    content_base_url: "/content/_mods/fixture/pack-1.0.0/files".into(),
                    // No Steam AppID: the fixture never probes an installed game.
                    exe_names: vec!["Game.exe".into()],
                    ..Default::default()
                }),
                ..Default::default()
            },
        ];
        let routes: HashMap<String, Vec<u8>> = HashMap::from([
            (
                "/api/games".into(),
                serde_json::to_vec(&LibraryResponse { items: records }).unwrap(),
            ),
            (
                "/api/maintenance".into(),
                br#"{"enabled":false,"blocks":{"install":false,"update":false,"launch":false}}"#
                    .to_vec(),
            ),
            (
                "/manifests/fixture/1.0.0.json".into(),
                serde_json::to_vec(&game_manifest).unwrap(),
            ),
            (
                "/manifests/fixture/0.9.0.json".into(),
                serde_json::to_vec(&old_manifest).unwrap(),
            ),
            (
                "/api/games/fixture/builds".into(),
                br#"{"items":["1.0.0","0.9.0"]}"#.to_vec(),
            ),
            (
                "/manifests/_mods/fixture/pack-1.0.0.json".into(),
                serde_json::to_vec(&mods_manifest).unwrap(),
            ),
            ("/content/fixture/1.0.0/files/Game.exe".into(), exe.to_vec()),
            ("/content/fixture/0.9.0/files/Game.exe".into(), exe.to_vec()),
            (
                "/content/_mods/fixture/pack-1.0.0/files/winhttp.dll".into(),
                loader.to_vec(),
            ),
            (
                "/content/_mods/fixture/pack-1.0.0/files/doorstop_config.ini".into(),
                ini.to_vec(),
            ),
            ("/metrics/report".into(), b"{}".to_vec()),
        ]);
        let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let base = format!("http://{}", server.server_addr());
        let stop = Arc::new(AtomicBool::new(false));
        let stopped = stop.clone();
        let requests = Arc::new(Mutex::new(Vec::new()));
        let captured = requests.clone();
        let hold_content = Arc::new(AtomicBool::new(false));
        let held = hold_content.clone();
        let worker = thread::spawn(move || {
            while !stopped.load(Ordering::Relaxed) {
                if let Some(request) = server.recv_timeout(Duration::from_millis(15)).unwrap() {
                    let path = request.url().to_owned();
                    captured.lock().unwrap().push(path.clone());
                    let body = routes.get(&path).cloned();
                    let status = if body.is_some() { 200 } else { 404 };
                    while path.starts_with("/content/")
                        && held.load(Ordering::Relaxed)
                        && !stopped.load(Ordering::Relaxed)
                    {
                        thread::sleep(Duration::from_millis(10));
                    }
                    let _ = request.respond(
                        tiny_http::Response::from_data(body.unwrap_or_default())
                            .with_status_code(status),
                    );
                }
            }
        });
        Self {
            base,
            stop,
            worker: Some(worker),
            requests,
            hold_content,
        }
    }
}
impl Drop for Loopback {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(worker) = self.worker.take() {
            worker.join().unwrap();
        }
    }
}

struct Cleanup(State);
impl Drop for Cleanup {
    fn drop(&mut self) {
        ACTIVE_STATE.with(|slot| *slot.borrow_mut() = None);
        let s = self.0.borrow();
        s.maintenance_timer.stop();
        s.update_timer.stop();
        s.toast_timer.stop();
        s.services.queue.shutdown();
    }
}

#[test]
fn controller_installs_real_queue_preserves_draft_and_saves_isolated_settings() {
    // Do not let a developer's games-path override redirect a fixture operation.
    assert!(
        std::env::var_os("CHILLHUB_GAMES_DIR").is_none(),
        "unset CHILLHUB_GAMES_DIR for isolated controller tests"
    );
    i_slint_backend_testing::init_integration_test_with_system_time();
    let temporary = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temporary.path());
    let server = Loopback::new();
    let mut prefs = AppConfig {
        games_path: paths.games_dir.to_string_lossy().into_owned(),
        api_base_url: server.base.clone(),
        last_game_id: "fixture".into(),
        ..Default::default()
    };
    prefs.remember("fixture", LaunchTarget::LocalModded);
    prefs
        .selected_versions
        .insert("fixture".into(), "0.9.0".into());
    prefs.last_seen_changelog_version = env!("CARGO_PKG_VERSION").into();
    ConfigStore::new(paths.clone()).save(&prefs).unwrap();
    let (services, prefs) = Services::new(paths.clone()).unwrap();
    assert_eq!(services.paths.games_dir, paths.games_dir);
    let state = Rc::new(RefCell::new(AppState {
        catalog: vec![],
        records: vec![],
        seed: vec![],
        data_root: temporary.path().into(),
        art_root: temporary.path().join("art"),
        selected: 0,
        visible: vec![],
        icons: vec![],
        choices: HashMap::new(),
        launching: Default::default(),
        query: String::new(),
        only_mods: false,
        mod_query: String::new(),
        mod_category: "Все".into(),
        generation: 0,
        catalog_generation: 0,
        gallery_generation: 0,
        gallery_index: 0,
        video: None,
        web_video: None,
        native_hwnd: 0,
        video_timer: Timer::default(),
        toast_timer: Timer::default(),
        maintenance_timer: Timer::default(),
        update_timer: Timer::default(),
        poll_inflight: false,
        maintenance: Default::default(),
        catalog_poll_inflight: false,
        catalog_checked_at: std::time::Instant::now(),
        last_activation: None,
        // Self-update has dedicated transaction tests. It is intentionally held
        // here so initialize cannot inspect real installation/recovery folders.
        update_inflight: true,
        security_offers: Default::default(),
        orphan_notice: Default::default(),
        estimate_inflight: false,
        services,
        prefs,
        option: None,
        choice_generation: 0,
        choice_epoch: Arc::new(std::sync::atomic::AtomicU64::new(0)),
        tasks: vec![],
        queue_speeds: Default::default(),
        library_status: Default::default(),
        pending: None,
        shortcut: None,
        update: None,
        offline: false,
    }));
    let cleanup = Cleanup(state.clone());
    ACTIVE_STATE.with(|slot| *slot.borrow_mut() = Some(state.clone()));
    let ui = AppWindow::new().unwrap();
    bind(&ui, &state);
    // This is the testing backend's virtual window, never winit/Win32.
    ui.show().unwrap();
    controller::initialize(&ui, &state);
    let outcome = Rc::new(RefCell::new(None::<Result<(), String>>));
    let captured_outcome = outcome.clone();
    let stage = Rc::new(RefCell::new((0_u8, 0_u64)));
    let tick = Timer::default();
    let deadline = Instant::now() + Duration::from_secs(15);
    let weak = ui.as_weak();
    let captured = state.clone();
    let base = server.base.clone();
    let hold_content = server.hold_content.clone();
    let original_games = paths.games_dir.to_string_lossy().into_owned();
    let draft = temporary
        .path()
        .join("unsaved-games")
        .to_string_lossy()
        .into_owned();
    tick.start(TimerMode::Repeated, Duration::from_millis(25), move || {
        let ui = weak.upgrade().unwrap();
        let result = (|| -> Result<bool, String> {
            if Instant::now() >= deadline {
                return Err(format!("phase {:?} timed out: loading={} busy={} label={} status={} tasks={:?}",
                    *stage.borrow(), ui.get_loading(), ui.get_busy(), ui.get_play_label(), ui.get_status(), captured.borrow().tasks));
            }
            let failed = captured.borrow().tasks.iter().find(|t| matches!(t.state, QueueState::Failed | QueueState::NeedsConfirmation)).cloned();
            if let Some(task) = failed { return Err(format!("unexpected failed/confirmation task: {task:?}")); }
            let (phase, expected_generation) = *stage.borrow();
            match phase {
                0 if !ui.get_loading() && !ui.get_busy() && ui.get_can_play() => {
                    if ui.get_games().row_count() != 2 || ui.get_play_label() != "Установить" || ui.get_source() != "local" {
                        return Err("initial catalog did not offer installation of remembered local modded game".into());
                    }
                    ui.invoke_search_edited("Без дополнений".into());
                    if ui.get_games().row_count() != 1 { return Err("plain game search failed".into()); }
                    ui.invoke_mods_filter_toggled(true);
                    if ui.get_games().row_count() != 0 { return Err("no-results filter failed".into()); }
                    ui.invoke_search_edited("".into());
                    if ui.get_games().row_count() != 1 { return Err("mods-only filter failed".into()); }
                    ui.invoke_mods_filter_toggled(false);
                    hold_content.store(true,Ordering::Relaxed);
                    *stage.borrow_mut() = (10, 0);
                    ui.invoke_play_requested(); // exactly once; action is InstallGame
                }
                10 if !ui.get_operation_busy() && captured.borrow().tasks.iter().any(|t|t.state==QueueState::Running) => {
                    ui.invoke_panel_closed();ui.invoke_panel_requested("settings".into());
                    ui.set_settings_path("relative-games-folder".into());ui.invoke_settings_leave("".into());
                    *stage.borrow_mut()=(11,0);
                }
                11 if !ui.get_operation_busy() => {
                    if ui.get_panel_kind()!="settings" || !ui.get_settings_error().contains("после завершения") {return Err("active queue allowed path change or discarded draft".into());}
                    ui.set_settings_path(original_games.clone().into());ui.set_settings_speed(3);ui.set_settings_threads(6);ui.set_settings_tray(false);
                    ui.invoke_settings_leave("".into());*stage.borrow_mut()=(12,0);
                }
                12 if !ui.get_operation_busy() => {
                    let saved=captured.borrow().services.config.load()?;
                    if !ui.get_panel_kind().is_empty()||saved.speed_limit_mbps!=3||saved.download_threads!=6||saved.minimize_to_tray {return Err("active queue blocked live-only settings save".into());}
                    hold_content.store(false,Ordering::Relaxed);*stage.borrow_mut()=(1,0);
                }
                1 if !ui.get_busy() && ui.get_can_play() && ui.get_play_label() == "Играть" => {
                    let tasks = captured.borrow().tasks.clone();
                    if tasks.len() != 2 || !tasks.iter().all(|t| t.state == QueueState::Completed) { return Ok(false); }
                    captured.borrow_mut().launching.insert(("fixture".into(), false));
                    controller::refresh_choice(&ui, &captured);
                    *stage.borrow_mut() = (13, 0);
                }
                13 if !ui.get_busy() && ui.get_play_label() == "Запускаем…" => {
                    if ui.get_can_play() || ui.get_operation_busy() { return Err("pending launch did not isolate its copy".into()); }
                    ui.invoke_play_requested();
                    if captured.borrow().launching.len() != 1 { return Err("duplicate pending launch was accepted".into()); }
                    ui.invoke_panel_closed();
                    ui.invoke_panel_requested("settings".into());
                    ui.set_settings_path(draft.clone().into());
                    ui.invoke_settings_leave("".into());
                    if !ui.get_settings_error().contains("Дождитесь запуска") { return Err("pending launch allowed library replacement".into()); }
                    ui.set_settings_path(original_games.clone().into());
                    ui.invoke_settings_leave("".into());
                    ui.invoke_game_selected(0);
                    *stage.borrow_mut() = (14, 0);
                }
                14 if !ui.get_busy() => {
                    if !ui.get_can_play() || ui.get_operation_busy() || ui.get_game_title() != "Без дополнений" { return Err("another game was blocked by pending launch".into()); }
                    ui.invoke_game_selected(1);
                    *stage.borrow_mut() = (15, 0);
                }
                15 if !ui.get_busy() => {
                    if ui.get_can_play() || ui.get_play_label() != "Запускаем…" { return Err("returning to pending copy allowed a duplicate launch".into()); }
                    captured.borrow_mut().launching.clear();
                    controller::refresh_choice(&ui, &captured);
                    *stage.borrow_mut() = (16, 0);
                }
                16 if !ui.get_busy() && ui.get_can_play() && ui.get_play_label() == "Играть" => {
                    let tasks = captured.borrow().tasks.clone();
                    ui.invoke_panel_closed();
                    ui.invoke_panel_requested("settings".into());
                    ui.set_settings_path(draft.clone().into());
                    ui.set_settings_threads(11);
                    let completed_base = tasks.iter().find(|t| t.request.scope == chillhub_native::transfer::SyncScope::WholeRoot).ok_or("missing completed base task")?;
                    *stage.borrow_mut() = (2, 0);
                    ui.invoke_queue_action(completed_base.id.clone().into(), "remove".into());
                }
                2 if !ui.get_operation_busy() && !ui.get_busy() && captured.borrow().tasks.len() == 1 => {
                    if ui.get_panel_kind() != "settings" || ui.get_settings_path() != draft || ui.get_settings_threads() != 11 {
                        return Err("queue state change discarded the open settings draft".into());
                    }
                    ui.invoke_action_requested("retry-catalog".into());
                    ui.invoke_action_requested("retry-catalog".into());
                    *stage.borrow_mut() = (3, captured.borrow().catalog_generation);
                }
                3 if !ui.get_loading() && !ui.get_busy() && !captured.borrow().offline && captured.borrow().catalog_generation == expected_generation => {
                    if ui.get_game_title() != "Проверочная игра" || ui.get_panel_kind() != "settings" || ui.get_settings_path() != draft || ui.get_settings_threads() != 11 {
                        return Err("reload discarded selection or settings draft".into());
                    }
                    ui.set_settings_path("relative-games-folder".into());
                    ui.invoke_panel_closed();
                    *stage.borrow_mut() = (30, 0);
                }
                30 if !ui.get_operation_busy() => {
                    if ui.get_panel_kind() != "settings" || ui.get_settings_path() != "relative-games-folder" || ui.get_settings_error().is_empty() {
                        return Err("invalid settings close lost its draft or failed to show a permanent error".into());
                    }
                    ui.set_settings_path(original_games.clone().into());
                    ui.set_settings_threads(5);ui.set_settings_speed(2);ui.set_settings_api(base.clone().into());ui.set_settings_tray(false);ui.set_reduced_motion(true);
                    *stage.borrow_mut() = (4, 0);
                    ui.invoke_settings_leave("".into());
                }
                4 if !ui.get_operation_busy() && !ui.get_loading() && !ui.get_busy() => {
                    let saved = captured.borrow().services.config.load()?;
                    if saved.download_threads != 5 || saved.speed_limit_mbps != 2 || saved.minimize_to_tray || saved.extra.get("ReducedMotion") != Some(&serde_json::Value::Bool(true)) {
                        return Err("settings callback did not persist isolated preferences".into());
                    }
                    if saved.api_base_url != base || saved.games_path != original_games || saved.last_game_id != "fixture" {
                        return Err("saved API/path/selection changed unexpectedly".into());
                    }
                    if !ui.get_panel_kind().is_empty() { return Err("successful settings save did not close panel".into()); }
                    if saved.selected_version("fixture") != Some("0.9.0") { return Err("settings save lost the selected older build".into()); }
                    *stage.borrow_mut() = (5, 0);
                    ui.invoke_panel_requested("manage".into());
                }
                5 if !ui.get_operation_busy() && ui.get_versions().row_count() == 2 => {
                    if ui.get_selected_version() != "0.9.0" { return Err("manage panel replaced pinned version with newest build".into()); }
                    ui.invoke_panel_closed();
                    ui.invoke_game_selected(0);
                    *stage.borrow_mut() = (6, 0);
                }
                6 if !ui.get_busy() => {
                    if ui.get_game_title() != "Без дополнений" || ui.get_has_mods() || ui.get_mode() != "plain" || ui.get_supports_steam() {
                        return Err("plain-only game exposes unavailable launch modes".into());
                    }
                    ui.hide().map_err(|e| e.to_string())?;
                    *stage.borrow_mut() = (7, 0);
                }
                7 => {
                    // The event loop must survive a hidden last window (tray use).
                    ui.show().map_err(|e| e.to_string())?;
                    if ui.get_game_title() != "Без дополнений" { return Err("virtual window lost state while hidden".into()); }
                    controller::test_background_failure(&ui);
                    *stage.borrow_mut() = (8, 0);
                }
                8 if !ui.get_operation_busy() => {
                    ui.invoke_search_edited("Без дополнений".into());
                    if ui.get_games().row_count() != 1 { return Err("UI callbacks stopped after a background failure".into()); }
                    for id in ["old-one","old-two"] {
                        let path=captured.borrow().services.paths.game_root(id)?;
                        fs::create_dir_all(&path).map_err(|e|e.to_string())?;fs::write(path.join(".version"),"1").map_err(|e|e.to_string())?;
                    }
                    ui.invoke_panel_requested("orphans".into());*stage.borrow_mut()=(81,0);
                }
                81 if ui.get_orphans().row_count()==2 => {
                    ui.invoke_orphan_action("old-two".into(),"toggle".into());
                    if ui.get_orphan_selection_count()!=1{return Err("orphan selection did not update".into());}
                    ui.invoke_action_requested("delete-selected-orphans".into());
                    if ui.get_panel_kind()!="confirm"||!ui.get_panel_body().contains("old-one")||ui.get_panel_body().contains("old-two"){return Err("bulk cleanup confirmation includes unselected folder".into());}
                    ui.invoke_action_requested("confirm-action".into());*stage.borrow_mut()=(82,0);
                }
                82 if !ui.get_operation_busy() => {
                    let paths=captured.borrow().services.paths.clone();
                    if paths.game_root("old-one")?.exists()||!paths.game_root("old-two")?.join(".version").exists(){return Err("bulk cleanup changed an unselected folder or left selected game".into());}
                    if !ui.get_orphans().iter().any(|r|r.result=="Удалено"){return Err("bulk cleanup omitted per-folder result".into());}
                    ui.invoke_panel_closed();
                    // Restore paused intent using the canonical Windows spelling while
                    // the selected library retains its ordinary path spelling.
                    let alias_root=fs::canonicalize(&paths.games_dir).map_err(|e|e.to_string())?.join("plain");
                    let paused=chillhub_native::queue::QueueItem {
                        id:"alias-paused".into(),
                        request:chillhub_native::transfer::TransferRequest {game_id:"plain".into(),root:alias_root,..Default::default()},
                        state:QueueState::Paused,progress:None,outcome:None,error:None,cleanup_paths:vec![],report:Default::default(),
                    };
                    let queue_file=paths.data_dir.join("alias-controller-queue.json");
                    fs::write(&queue_file,serde_json::to_vec(&vec![paused]).map_err(|e|e.to_string())?).map_err(|e|e.to_string())?;
                    captured.borrow().services.queue.shutdown();
                    captured.borrow_mut().services.queue=Arc::new(chillhub_native::queue::DownloadQueue::new(queue_file).map_err(|e|e.to_string())?);
                    controller::refresh_choice(&ui,&captured);
                    *stage.borrow_mut()=(83,0);
                }
                83 if !ui.get_busy() => {
                    if ui.get_can_play()||ui.get_play_label()!="В очереди" {
                        return Err("paused queue task became invisible through a filesystem alias".into());
                    }
                    ui.set_panel_kind("gallery".into());
                    controller::test_empty_catalog(&ui, &captured);
                    if !ui.get_panel_kind().is_empty() || !ui.get_game_title().is_empty() || ui.get_can_play() {
                        return Err("empty live catalog retained a stale game or gallery".into());
                    }
                    ui.invoke_gallery_step(1);
                    ui.invoke_media_selected(0);
                    if ui.get_media().row_count() != 0 || ui.get_hero().size().width != 0 {
                        return Err("empty library retained media".into());
                    }
                    return Ok(true);
                }
                _ => {}
            }
            Ok(false)
        })();
        match result {
            Ok(false) => {}
            result => { hold_content.store(false,Ordering::Relaxed);*captured_outcome.borrow_mut() = Some(result.map(|_| ())); let _ = slint::quit_event_loop(); }
        }
    });
    let loop_result = slint::run_event_loop_until_quit();
    tick.stop();
    drop(cleanup);
    loop_result.unwrap();
    outcome
        .borrow_mut()
        .take()
        .expect("event loop exited without outcome")
        .unwrap();
    let game = paths.game_root("fixture").unwrap();
    assert_eq!(
        fs::read(game.join("Game.exe")).unwrap(),
        b"controller fixture; this file cannot execute"
    );
    assert!(
        fs::read_to_string(game.join("doorstop_config.ini"))
            .unwrap()
            .contains("enabled=false")
    );
    assert!(!game.join(".updating").exists());
    assert_eq!(
        fs::read_to_string(game.join(".version")).unwrap().trim(),
        "0.9.0"
    );
    assert!(
        state
            .borrow()
            .services
            .playtime
            .pending()
            .unwrap()
            .is_empty()
    );
    assert!(!paths.games_dir.join("plain").exists());
    let requests = server.requests.lock().unwrap();
    assert!(
        requests
            .iter()
            .filter(|p| p.as_str() == "/api/games")
            .count()
            >= 3
    );
    assert!(
        !requests
            .iter()
            .any(|p| p.contains("launcher/latest") || p.contains("feedback/submit"))
    );
}

#[test]
fn image_with_valid_header_and_corrupt_pixels_is_refetched_once() {
    let temp = tempfile::tempdir().unwrap();
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let api =
        chillhub_native::net::ApiClient::new(&format!("http://{}", server.server_addr())).unwrap();
    let bytes = include_bytes!("../tests/fixtures/how-to-fish-icon.png");
    let mut corrupt = bytes.to_vec();
    corrupt[50] ^= 0xff;
    let url = api.base.join("/fish.png").unwrap();
    let path = temp.path().join(format!(
        "{}.img",
        blake3::hash(url.as_str().as_bytes()).to_hex()
    ));
    fs::write(&path, corrupt).unwrap();
    let worker = thread::spawn(move || {
        let request = server
            .recv_timeout(Duration::from_secs(5))
            .unwrap()
            .expect("corrupt cache was not refetched");
        request
            .respond(tiny_http::Response::from_data(bytes.to_vec()))
            .unwrap();
    });
    let pixels =
        source_image_pixels("/fish.png", temp.path(), temp.path(), &api, 64, 64, None).unwrap();
    assert_eq!((pixels.width(), pixels.height()), (64, 64));
    worker.join().unwrap();
    assert_eq!(fs::read(path).unwrap(), bytes);
}
