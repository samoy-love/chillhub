//! Controller integration tests use the headless testing backend and loopback fixtures.
#[path = "controller_ui_action_tests.rs"]
mod ui_action_tests;

use super::*;
use crate::catalog::Media;
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

#[test]
fn saving_window_geometry_preserves_bounds_for_fullscreen_close() {
    let mut config = chillhub_native::config::AppConfig {
        window_width: 1360.0,
        window_height: 820.0,
        window_maximized: false,
        ..Default::default()
    };

    super::controller::apply_window_geometry(&mut config, 1400.0, 900.0, false, false);
    assert_eq!((config.window_width, config.window_height), (1400.0, 900.0));

    super::controller::apply_window_geometry(&mut config, 2560.0, 1440.0, true, false);
    assert_eq!((config.window_width, config.window_height), (1400.0, 900.0));
    assert!(config.window_maximized);

    config.window_maximized = false;
    super::controller::apply_window_geometry(&mut config, 2560.0, 1440.0, false, true);

    assert_eq!((config.window_width, config.window_height), (1400.0, 900.0));
    assert!(!config.window_maximized);

    config.window_maximized = true;
    super::controller::apply_window_geometry(&mut config, 2560.0, 1440.0, false, true);
    assert_eq!((config.window_width, config.window_height), (1400.0, 900.0));
    assert!(config.window_maximized);
}

struct Loopback {
    base: String,
    stop: Arc<AtomicBool>,
    worker: Option<thread::JoinHandle<()>>,
    requests: Arc<Mutex<Vec<String>>>,
    manual_feedback: Arc<AtomicBool>,
    hold_content: Arc<AtomicBool>,
    reject_catalog: Arc<AtomicBool>,
}
impl Loopback {
    fn new() -> Self {
        Self::with_title("Проверочная игра")
    }

    fn with_title(fixture_title: &str) -> Self {
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
                title: fixture_title.into(),
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
        let manual_feedback = Arc::new(AtomicBool::new(false));
        let feedback_received = manual_feedback.clone();
        let hold_content = Arc::new(AtomicBool::new(false));
        let held = hold_content.clone();
        let reject_catalog = Arc::new(AtomicBool::new(false));
        let unavailable = reject_catalog.clone();
        let worker = thread::spawn(move || {
            while !stopped.load(Ordering::Relaxed) {
                if let Some(mut request) = server.recv_timeout(Duration::from_millis(15)).unwrap() {
                    let path = request.url().to_owned();
                    captured.lock().unwrap().push(path.clone());
                    if path.ends_with("/feedback/submit") {
                        // The controlled worker panic can enqueue an automatic report
                        // while this test is checking rejection of the manual form.
                        let automatic =
                            serde_json::from_reader::<_, serde_json::Value>(request.as_reader())
                                .is_ok_and(|body| {
                                    body.pointer("/system/auto").and_then(|v| v.as_str())
                                        == Some("1")
                                });
                        if !automatic {
                            feedback_received.store(true, Ordering::Relaxed);
                        }
                    }
                    let body = routes.get(&path).cloned();
                    let status = if path == "/api/games" && unavailable.load(Ordering::Relaxed) {
                        503
                    } else if body.is_some() {
                        200
                    } else {
                        404
                    };
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
            manual_feedback,
            hold_content,
            reject_catalog,
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
    let replacement_server = Loopback::with_title("Игра с нового сервера");
    crate::main_runtime_tests::run(server.base.as_str());
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
    // A completed short session must not look like a game that was never played.
    fs::write(
        paths.data_dir.join("playtime.json"),
        r#"{"fixture#LocalModded":{"TotalSeconds":50,"LastSessionSeconds":50}}"#,
    )
    .unwrap();
    let (services, prefs) = Services::new(paths.clone()).unwrap();
    assert_eq!(services.paths.games_dir, paths.games_dir);
    let state = Rc::new(RefCell::new(AppState {
        catalog: vec![],
        records: vec![],
        seed: vec![],
        data_root: temporary.path().into(),
        art_root: temporary.path().join("art"),
        selected: 0,
        displayed_game_id: None,
        visible: vec![],
        icons: vec![],
        choices: HashMap::new(),
        launching: Default::default(),
        query: String::new(),
        mod_query: String::new(),
        mod_category: "Все".into(),
        generation: 0,
        catalog_generation: 0,
        gallery_generation: 0,
        gallery_index: 0,
        video: None,
        native_hwnd: 0,
        viewer_restore: None,
        viewer_previous_volume: None,
        video_timer: Timer::default(),
        toast_timer: Timer::default(),
        report_notices: Default::default(),
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
        defer_startup_changelog: false,
        pending_shortcuts: Default::default(),
        shortcut_installs: Default::default(),
        update: None,
        offline: false,
    }));
    let cleanup = Cleanup(state.clone());
    ACTIVE_STATE.with(|slot| *slot.borrow_mut() = Some(state.clone()));
    let ui = AppWindow::new().unwrap();
    bind(&ui, &state);
    ui_action_tests::check_unavailable_actions(&ui, &state);
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
    let replacement_base = replacement_server.base.clone();
    let replacement_games = temporary.path().join("replacement-games");
    let hold_content = server.hold_content.clone();
    let action_queue_ids = RefCell::new(Vec::new());
    let hold_shortcut_confirmation_until = std::cell::Cell::new(None::<Instant>);
    let reject_catalog = server.reject_catalog.clone();
    let cleanup_root = paths.games_dir.join("confirmation-fixture");
    let cleanup_save = cleanup_root.join("save.dat");
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
            let (phase, expected_generation) = *stage.borrow();
            if phase < 94 {
                let failed = captured.borrow().tasks.iter().find(|t| matches!(t.state, QueueState::Failed | QueueState::NeedsConfirmation)).cloned();
                if let Some(task) = failed { return Err(format!("unexpected failed/confirmation task: {task:?}")); }
            }
            match phase {
                0 if !ui.get_loading() && !ui.get_busy() && ui.get_can_play() => {
                    if ui.get_playtime_label() != "Сыграно меньше минуты" {
                        return Err("completed short session was shown as never played".into());
                    }
                    if ui.get_games().row_count() != 2 || ui.get_play_label() != "Установить" || ui.get_source() != "local" {
                        return Err("initial catalog did not offer installation of remembered local modded game".into());
                    }
                    ui.invoke_search_edited("Без дополнений".into());
                    if ui.get_games().row_count() != 1 { return Err("plain game search failed".into()); }
                    ui.invoke_search_edited("Несуществующая игра".into());
                    if ui.get_games().row_count() != 0 { return Err("no-results search failed".into()); }
                    ui.invoke_search_edited("".into());
                    if ui.get_games().row_count() != 2 { return Err("clearing search did not restore all games".into()); }
                    hold_content.store(true,Ordering::Relaxed);
                    *stage.borrow_mut() = (10, 0);
                    ui.invoke_play_requested(); // exactly once; action is InstallGame
                }
                10 if !ui.get_operation_busy() && captured.borrow().tasks.iter().any(|t|t.state==QueueState::Running) => {
                    if !ui.get_panel_kind().is_empty() || ui.get_downloads_active() <= 0 {
                        return Err("install must affect the downloads count without blocking the library".into());
                    }
                    ui.invoke_panel_requested("downloads".into());
                    if ui.get_panel_kind() != "downloads" || ui.get_queue_active().id.is_empty() {
                        return Err("the downloads action must open the active installation".into());
                    }
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
                    let selected = captured.borrow().selected;
                    // The fixture advertises Steam in the UI, but has no AppID
                    // and cannot inspect a Steam installation on the test host.
                    captured.borrow_mut().catalog[selected].steam_supported = true;
                    ui.invoke_source_selected("steam".into());
                    if !ui.get_busy() || ui.get_play_label() != "Проверяем…" {
                        return Err("the copy probe retained the previous play action".into());
                    }
                    *stage.borrow_mut() = (19, 0);
                }
                19 if !ui.get_busy() => {
                    let ready_library_row = ui.get_games().row_data(1).is_some_and(|row| {
                        row.id == "fixture" && row.status == "Установлена" && row.status_kind == "ready"
                    });
                    if ui.get_source() != "steam"
                        || ui.get_play_label() != "Недоступно"
                        || ui.get_can_play()
                        || ui.get_has_game_files()
                        || ui.get_can_verify_game()
                        || ui.get_can_verify_mods()
                        || ui.get_can_create_shortcut()
                        || !ready_library_row
                    {
                        return Err("missing Steam copy changed local library readiness or allowed file actions".into());
                    }
                    let selected = captured.borrow().selected;
                    captured.borrow_mut().catalog[selected].steam_supported = false;
                    ui.invoke_source_selected("local".into());
                    ui.invoke_mode_selected("mods".into());
                    *stage.borrow_mut() = (20, 0);
                }
                20 if !ui.get_busy() && ui.get_can_play() && ui.get_play_label() == "Играть" => {
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
                    ui_action_tests::start_failed_launch(&ui, &captured)?;
                    *stage.borrow_mut() = (17, 0);
                }
                17 if !ui.get_busy() && captured.borrow().launching.is_empty() => {
                    ui_action_tests::check_failed_launch(&ui, &captured)?;
                    ui_action_tests::start_shortcut_during_catalog_refresh(&ui, &captured)?;
                    *stage.borrow_mut() = (18, 0);
                }
                18 if !ui.get_operation_busy() && !ui.get_busy() && ui.get_can_play() => {
                    ui_action_tests::check_shortcut_launch_finished(&ui, &captured)?;
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
                    ui.set_settings_threads(5);ui.set_settings_speed(2);ui.set_settings_api(base.clone().into());ui.set_settings_tray(false);ui.set_reduced_motion(true);ui.set_karaoke_enabled(true);
                    *stage.borrow_mut() = (4, 0);
                    ui.invoke_settings_leave("".into());
                }
                4 if !ui.get_operation_busy() && !ui.get_loading() && !ui.get_busy() => {
                    let saved = captured.borrow().services.config.load()?;
                    if saved.download_threads != 5 || saved.speed_limit_mbps != 2 || saved.minimize_to_tray || saved.extra.get("ReducedMotion") != Some(&serde_json::Value::Bool(true)) || saved.extra.get("KaraokeEnabled") != Some(&serde_json::Value::Bool(true)) {
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
                    *action_queue_ids.borrow_mut() = ui_action_tests::start_local_verification(&ui, &captured)?;
                    *stage.borrow_mut() = (50, 0);
                }
                50 if {
                    let previous_ids = action_queue_ids.borrow();
                    ui_action_tests::check_local_queue(&ui, &captured, &previous_ids, true)?
                } => {
                    *action_queue_ids.borrow_mut() = ui_action_tests::confirm_requested_version(&ui, &captured)?;
                    *stage.borrow_mut() = (51, 0);
                }
                51 if ui_action_tests::check_local_queue(&ui, &captured, &action_queue_ids.borrow(), false)? => {
                    *action_queue_ids.borrow_mut() = ui_action_tests::start_modpack_verification(&ui, &captured)?;
                    *stage.borrow_mut() = (52, 0);
                }
                52 if ui_action_tests::check_modpack_verification(&ui, &captured, &action_queue_ids.borrow())? => {
                    captured.borrow_mut().choices.insert("fixture".into(), ("local".into(), "mods".into()));
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
                    test_background_failure(&ui);
                    *stage.borrow_mut() = (8, 0);
                }
                8 if !ui.get_operation_busy() => {
                    let original = ui.get_toast();
                    if original.is_empty() { return Err("worker error was not shown".into()); }
                    controller::queue_report_notice(&ui, &captured, chillhub_native::diagnostic_notifications::ReportNotice::Sent);
                    if ui.get_toast() != original { return Err("delivery notice hid the original error".into()); }
                    *stage.borrow_mut() = (80, 0);
                }
                80 if ui.get_toast() == "Отчёт об ошибке автоматически отправлен." => {
                    if !captured.borrow().report_notices.is_empty() { return Err("delivery notice queue did not drain".into()); }
                    ui.invoke_search_edited("Без дополнений".into());
                    if ui.get_games().row_count() != 1 { return Err("UI callbacks stopped after a background failure".into()); }
                    ui.invoke_game_selected(0);
                    if captured.borrow().catalog[captured.borrow().selected].has_mods { return Err("plain search did not select the plain game".into()); }
                    ui.invoke_search_edited("".into());
                    ui.invoke_search_edited("Без дополнений".into());
                    ui.invoke_game_selected(0);
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
                    if paths.game_root("old-one")?.exists()||!paths.game_root("old-two")?.join(".version").exists(){
                        let results:Vec<_>=ui.get_orphans().iter().map(|r|format!("{}: {}",r.id,r.result)).collect();
                        return Err(format!("bulk cleanup changed an unselected folder or left selected game; results={results:?}; panel={}; toast={}",ui.get_panel_body(),ui.get_toast()));
                    }
                    if !ui.get_orphans().iter().any(|r|r.result=="Удалено"){return Err("bulk cleanup omitted per-folder result".into());}
                    ui.invoke_panel_closed();
                    // Restore paused intent using the canonical Windows spelling while
                    // the selected library retains its ordinary path spelling.
                    let alias_root=fs::canonicalize(&paths.games_dir).map_err(|e|e.to_string())?.join("plain");
                    let paused=chillhub_native::queue::QueueItem {
                        id:"alias-paused".into(),
                        request:chillhub_native::transfer::TransferRequest {game_id:"plain".into(),root:alias_root,..Default::default()},
                        state:QueueState::Paused,progress:None,outcome:None,error:None,cleanup_files:vec![],report:Default::default(),
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
                    ui.invoke_action_requested("delete-game".into());
                    if ui.get_panel_kind() != "confirm" || !ui.get_panel_body().contains("plain") {
                        return Err("delete action did not name the selected local copy".into());
                    }
                    ui.invoke_action_requested("cancel-action".into());
                    if captured.borrow().pending.is_some() || !ui.get_panel_kind().is_empty() {
                        return Err("cancelling deletion retained a pending action".into());
                    }
                    ui.invoke_action_requested("confirm-action".into());
                    ui.invoke_action_requested("removed-action".into());
                    let tasks = captured.borrow().services.queue.snapshot();
                    if tasks.len() != 1 || tasks[0].id != "alias-paused" || tasks[0].state != QueueState::Paused
                        || ui.get_operation_busy() || !ui.get_toast().contains("недоступно") {
                        return Err("cancelled or unsupported action changed the paused queue".into());
                    }
                    ui.set_panel_kind("gallery".into());
                    test_empty_catalog(&ui, &captured);
                    if !ui.get_panel_kind().is_empty() || !ui.get_game_title().is_empty() || ui.get_can_play() {
                        return Err("empty live catalog retained a stale game or gallery".into());
                    }
                    ui.invoke_gallery_step(1);
                    ui.invoke_media_selected(0);
                    if ui.get_media().row_count() != 0 || ui.get_hero().size().width != 0 {
                        return Err("empty library retained media".into());
                    }
                    ui.invoke_action_requested("security-helper".into());
                    if !ui.get_toast().contains("выберите игру") || ui.get_operation_busy() {
                        return Err("empty library security action must explain the missing selection".into());
                    }
                    // A failed refresh must invalidate a previously advertised release.
                    captured.borrow_mut().update=Some(chillhub_native::update::UpdateRelease {
                        version:"9.0.0".into(), manifest_url:"/stale.json".into(), content_base_url:"/stale/".into(),
                    });
                    ui.set_update_available(true);ui.set_update_target("9.0.0".into());
                    ui.invoke_action_requested("update-check".into());
                    if captured.borrow().update.is_some() || ui.get_update_available() || !ui.get_operation_busy() {
                        return Err("update refresh retained stale installation intent".into());
                    }
                    *stage.borrow_mut()=(84,0);
                }
                84 if !ui.get_operation_busy() => {
                    if !ui.get_update_checked() || !ui.get_update_error() || ui.get_update_available()
                        || captured.borrow().update.is_some() || !ui.get_update_target().is_empty() {
                        return Err("failed update check retained a usable stale release or hid the failure".into());
                    }
                    let bundle = captured.borrow().data_root.clone();
                    let assets = bundle.join("Assets");
                    fs::create_dir_all(&assets).map_err(|error| error.to_string())?;
                    image::RgbaImage::from_pixel(3, 2, image::Rgba([30, 120, 210, 255]))
                        .save(assets.join("gallery-fixture.png"))
                        .map_err(|error| error.to_string())?;
                    let variants = [1080, 720, 480]
                        .into_iter()
                        .map(|height| chillhub_native::model::VideoVariant {
                            url: format!("/assets/gameplay-{height}.mp4"),
                            width: height * 16 / 9,
                            height,
                            bytes: u64::from(height) * 10,
                        })
                        .collect();
                    captured.borrow_mut().catalog = vec![Game {
                        id: "gallery-fixture".into(),
                        title: "Галерея проверки".into(),
                        media: vec![
                            Media {
                                kind: "video".into(),
                                provider: "file".into(),
                                caption: "Трейлер".into(),
                                url: "/assets/trailer.mp4".into(),
                                variants: vec![chillhub_native::model::VideoVariant {
                                    url: "/assets/trailer-1080.mp4".into(),
                                    width: 1920,
                                    height: 1080,
                                    bytes: 100,
                                }],
                                ..Default::default()
                            },
                            Media {
                                kind: "video".into(),
                                provider: "file".into(),
                                caption: "Совместная игра".into(),
                                url: "/assets/gameplay-1080.mp4".into(),
                                variants,
                                ..Default::default()
                            },
                            Media {
                                kind: "image".into(),
                                path: "Assets/gallery-fixture.png".into(),
                                caption: "Кадр игры".into(),
                                ..Default::default()
                            },
                        ],
                        ..Default::default()
                    }];
                    captured.borrow_mut().selected = 0;
                    open_media(&ui, &captured, 1);
                    if !ui.get_selected_media_is_video()
                        || ui.get_gallery_index() != 1
                        || ui.get_gallery_caption() != "Совместная игра"
                        || ui.get_video_qualities().row_count() != 3
                        || ui.get_video_quality() != 1080
                    {
                        return Err("the second gallery item did not open as the gameplay video with quality choices".into());
                    }
                    open_media(&ui, &captured, 2);
                    *stage.borrow_mut() = (85, 0);
                }
                85 if ui.get_gallery_index() == 2 && ui.get_gallery_image().size().width > 0 => {
                    if ui.get_selected_media_is_video()
                        || ui.get_gallery_caption() != "Кадр игры"
                        || ui.get_gallery_image().size().height == 0
                    {
                        return Err("the gallery did not return from the second video to the image".into());
                    }
                    captured.borrow_mut().launching.insert(("fixture".into(), false));
                    for action in ["update-install", "update-verify"] {
                        ui.invoke_action_requested(action.into());
                        if ui.get_operation_busy() || !ui.get_toast().contains("Дождитесь запуска") {
                            return Err("an update could interrupt a pending game launch".into());
                        }
                    }
                    captured.borrow_mut().launching.clear();
                    ui.invoke_action_requested("update-verify".into());
                    if ui.get_operation_busy() || !ui.get_toast().contains("Сначала проверьте") {
                        return Err("verification accepted a missing release".into());
                    }
                    captured.borrow_mut().update = Some(chillhub_native::update::UpdateRelease {
                        version: "99.0.0".into(),
                        manifest_url: "http://127.0.0.1:1/invalid.json".into(),
                        content_base_url: "http://127.0.0.1:1/files/".into(),
                    });
                    ui.invoke_action_requested("update-verify".into());
                    *stage.borrow_mut() = (86, 0);
                }
                86 if !ui.get_operation_busy() => {
                    if !ui.get_update_error() || ui.get_task_status().is_empty() {
                        return Err("verification did not surface an unsafe update origin".into());
                    }
                    ui.invoke_action_requested("update-install".into());
                    *stage.borrow_mut() = (87, 0);
                }
                87 if !ui.get_operation_busy() => {
                    if !ui.get_update_error() || !ui.get_task_status().contains("Проверить установку") {
                        return Err("failed update did not offer an installation check".into());
                    }
                    ui.invoke_panel_closed();
                    ui.invoke_panel_requested("settings".into());
                    ui.set_settings_speed(4);
                    ui.invoke_settings_live_changed();
                    *stage.borrow_mut() = (88, 0);
                }
                88 if ui.get_settings_save_status() == "Сохранено" => {
                    let saved = captured.borrow().services.config.load()?;
                    if saved.speed_limit_mbps != 4 || saved.download_threads != 5 || saved.games_path != original_games {
                        return Err("saving a live setting changed an unrelated preference".into());
                    }
                    ui.invoke_settings_leave("feedback".into());
                    if ui.get_panel_kind() != "feedback" {
                        return Err("saving settings did not open the requested panel".into());
                    }
                    ui.invoke_feedback_send("".into(), "".into(), "bug".into(), "".into(), false);
                    *stage.borrow_mut() = (89, 0);
                }
                89 if !ui.get_operation_busy() => {
                    if ui.get_panel_kind() != "feedback" || !ui.get_toast().contains("Добавьте описание") {
                        return Err("invalid feedback cleared the user's form or hid the error".into());
                    }
                    let now = Instant::now();
                    captured.borrow_mut().catalog_checked_at = now - Duration::from_secs(301);
                    controller::refresh_focused(&ui, &captured, now);
                    if captured.borrow().catalog_poll_inflight {
                        return Err("background refresh could replace a visible draft".into());
                    }
                    ui.invoke_panel_closed();
                    controller::refresh_focused(&ui, &captured, now + Duration::from_secs(1));
                    if captured.borrow().catalog_poll_inflight {
                        return Err("repeated focus event bypassed the refresh interval".into());
                    }
                    controller::refresh_focused(&ui, &captured, now + Duration::from_secs(11));
                    if !captured.borrow().catalog_poll_inflight {
                        return Err("focus did not refresh the stale catalog".into());
                    }
                    *stage.borrow_mut() = (90, 0);
                }
                90 if !captured.borrow().catalog_poll_inflight => {
                    if captured.borrow().records.len() != 2 || captured.borrow().offline {
                        return Err("quiet refresh did not apply the new catalog".into());
                    }
                    reject_catalog.store(true, Ordering::Relaxed);
                    refocus_stale_catalog(&ui, &captured);
                    if !captured.borrow().catalog_poll_inflight {
                        return Err("stale catalog refresh did not begin".into());
                    }
                    *stage.borrow_mut() = (92, 0);
                }
                92 if !captured.borrow().catalog_poll_inflight => {
                    if !captured.borrow().offline || captured.borrow().records.len() != 2
                        || !ui.get_connection_note().contains("сохранённая библиотека")
                        || controller::selected(&captured).is_none()
                        || captured.borrow().services.queue.snapshot().first().map(|task| task.state) != Some(QueueState::Paused) {
                        return Err("failed catalog refresh lost the cached library, selection, or paused download".into());
                    }
                    reject_catalog.store(false, Ordering::Relaxed);
                    refocus_stale_catalog(&ui, &captured);
                    *stage.borrow_mut() = (93, 0);
                }
                93 if !captured.borrow().catalog_poll_inflight => {
                    if captured.borrow().offline || !ui.get_connection_note().is_empty()
                        || controller::selected(&captured).is_none() {
                        return Err("catalog recovery retained the offline warning or lost the selection".into());
                    }
                    let queue_path = captured.borrow().services.paths.data_dir.join("controller-recovery.json");
                    let corrupt = b"{fixture unreadable queue";
                    fs::write(&queue_path, corrupt).map_err(|error| error.to_string())?;
                    let broken = Arc::new(chillhub_native::queue::DownloadQueue::new(queue_path.clone()).map_err(|error| error.to_string())?);
                    let previous = std::mem::replace(&mut captured.borrow_mut().services.queue, broken);
                    previous.shutdown();
                    ui.invoke_panel_requested("downloads".into());
                    ui.invoke_queue_action("".into(), "reset".into());
                    if ui.get_panel_kind() != "confirm" || !ui.get_panel_title().contains("очередь") {
                        return Err("resetting an unreadable queue did not ask for confirmation".into());
                    }
                    ui.invoke_action_requested("cancel-action".into());
                    if fs::read(&queue_path).map_err(|error| error.to_string())? != corrupt {
                        return Err("cancelled reset modified the saved queue".into());
                    }
                    ui.invoke_queue_action("".into(), "reset".into());
                    ui.invoke_action_requested("confirm-action".into());
                    *stage.borrow_mut() = (91, 0);
                }
                91 if !ui.get_operation_busy() => {
                    let queue = captured.borrow().services.queue.clone();
                    if queue.recovery_error().is_some() || !queue.snapshot().is_empty()
                        || ui.get_toast() != "Очередь загрузок восстановлена" {
                        return Err("confirmed queue reset did not recover an empty queue".into());
                    }
                    fs::create_dir_all(&cleanup_root).map_err(|e| e.to_string())?;
                    fs::write(&cleanup_save, b"old save").map_err(|e| e.to_string())?;
                    queue.enqueue(chillhub_native::transfer::TransferRequest {
                        game_id: "fixture".into(),
                        version: "1.0.0".into(),
                        root: cleanup_root.clone(),
                        manifest_url: format!("{base}/manifests/fixture/1.0.0.json"),
                        content_base_url: format!("{base}/content/fixture/1.0.0/files"),
                        ..Default::default()
                    }).map_err(|e| e.to_string())?;
                    *stage.borrow_mut() = (94, 0);
                }
                94 => {
                    let queue = captured.borrow().services.queue.clone();
                    if let Some(id) = show_cleanup_task(&captured)? {
                        ui.invoke_queue_action(id.clone().into(), "confirm".into());
                        if ui.get_panel_kind() != "confirm" || !ui.get_panel_body().contains("save.dat") {
                            return Err("cleanup prompt did not name the file to remove".into());
                        }
                        fs::write(&cleanup_save, b"new save").map_err(|e| e.to_string())?;
                        // A new attempt replans after the preview was presented.
                        queue.cancel(&id).map_err(|e| e.to_string())?;
                        queue.resume(&id).map_err(|e| e.to_string())?;
                        *stage.borrow_mut() = (95, 0);
                    }
                }
                95 => {
                    if show_cleanup_task(&captured)?.is_some() {
                        ui.invoke_action_requested("confirm-action".into());
                        *stage.borrow_mut() = (96, 0);
                    }
                }
                96 if !ui.get_operation_busy() => {
                    let id = show_cleanup_task(&captured)?.ok_or("stale cleanup dialog resumed the queue")?;
                    if !ui.get_toast().contains("Список файлов изменился")
                        || fs::read(&cleanup_save).map_err(|e| e.to_string())? != b"new save" {
                        return Err("stale cleanup dialog did not protect the new save".into());
                    }
                    ui.invoke_queue_action(id.into(), "confirm".into());
                    ui.invoke_action_requested("confirm-action".into());
                    *stage.borrow_mut() = (97, 0);
                }
                97 if !ui.get_operation_busy() => {
                    let tasks = captured.borrow().services.queue.snapshot();
                    let task = tasks.first().ok_or("cleanup task disappeared")?;
                    match task.state {
                        QueueState::Completed if !cleanup_save.exists() && cleanup_root.join(".version").is_file() => {
                            ui.invoke_panel_closed();
                            controller::update_tests::start_automatic_update(&ui, &captured)?;
                            *stage.borrow_mut() = (98, 0);
                        }
                        QueueState::Waiting | QueueState::Running => {},
                        _ => return Err(format!("fresh cleanup confirmation did not finish the expected install: {task:?}")),
                    }
                }
                98 if !ui.get_operation_busy() => {
                    controller::update_tests::check_automatic_update_failure(&ui, &captured)?;
                    ui_action_tests::revert_live_setting(&ui)?;
                    *stage.borrow_mut() = (99, 0);
                }
                99 if ui.get_settings_save_status() == "Сохранено" => {
                    ui_action_tests::check_reverted_live_setting(&ui, &captured)?;
                    ui_action_tests::request_shortcut_batch(&ui, &captured)?;
                    ui_action_tests::check_catalog_does_not_preempt_shortcut_batch(&ui, &captured)?;
                    *stage.borrow_mut() = (100, 0);
                }
                100 if !ui.get_loading() && ui.get_panel_kind() == "confirm" => {
                    if captured.borrow().pending_shortcuts.len() != 1 || !ui.get_panel_body().contains("Shortcut.exe") {
                        return Err("the second activation superseded the first shortcut".into());
                    }
                    hold_shortcut_confirmation_until.set(Some(Instant::now() + Duration::from_millis(350)));
                    *stage.borrow_mut() = (101, captured.borrow().catalog_generation);
                }
                101 if hold_shortcut_confirmation_until.get().is_some_and(|until| Instant::now() >= until) => {
                    if ui.get_loading() || ui.get_panel_kind() != "confirm"
                        || captured.borrow().catalog_generation != expected_generation {
                        return Err("queued shortcut replaced an open confirmation".into());
                    }
                    ui_action_tests::confirm_stale_shortcut(&ui, &captured)?;
                    *stage.borrow_mut() = (102, 0);
                }
                102 if !ui.get_operation_busy() => {
                    ui_action_tests::check_stale_shortcut(&ui, &captured)?;
                    *stage.borrow_mut() = (106, 0);
                }
                106 if !ui.get_loading() && ui.get_toast().contains("Не удалось открыть игру из ярлыка") => {
                    if captured.borrow().pending.is_some() || ui.get_panel_kind() == "confirm" || !captured.borrow().pending_shortcuts.is_empty() {
                        return Err("shortcut to a file outside the game folder prompted a launch or stayed queued".into());
                    }
                    ui_action_tests::delete_shortcut_fixture(&ui, &captured)?;
                    *stage.borrow_mut() = (103, 0);
                }
                103 if !ui.get_operation_busy() => {
                    ui_action_tests::check_shortcut_fixture_deleted(&ui, &captured)?;
                    ui_action_tests::submit_library_switch(&ui, &replacement_games, "file:///invalid-api")?;
                    *stage.borrow_mut() = (104, 0);
                }
                104 if !ui.get_operation_busy() => {
                    ui_action_tests::check_library_switch_rejected(&ui, &captured, &original_games, &base, &replacement_games)?;
                    ui.set_settings_api(replacement_base.clone().into());
                    ui.invoke_settings_leave("".into());
                    *stage.borrow_mut() = (105, 0);
                }
                105 if !ui.get_operation_busy() && !ui.get_loading() && !ui.get_busy() => {
                    if !captured.borrow().records.iter().any(|game| game.title == "Игра с нового сервера") {
                        return Ok(false);
                    }
                    ui_action_tests::check_library_switch_saved(&ui, &captured, &replacement_games, &replacement_base, &original_games)?;
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
    // Loopback is deliberately not a trusted update origin: rejection must happen
    // before any HTTP request, while still clearing the stale release in the UI.
    assert!(!requests.iter().any(|p| p.contains("launcher/latest")));
    assert!(!server.manual_feedback.load(Ordering::Relaxed));
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

fn refocus_stale_catalog(ui: &AppWindow, state: &State) {
    let now = Instant::now();
    {
        let mut current = state.borrow_mut();
        current.catalog_checked_at = now - Duration::from_secs(301);
        current.last_activation = None;
    }
    controller::refresh_focused(ui, state, now);
}

fn show_cleanup_task(state: &State) -> Result<Option<String>, String> {
    let tasks = state.borrow().services.queue.snapshot();
    let task = tasks.first().ok_or("cleanup task disappeared")?;
    match task.state {
        QueueState::NeedsConfirmation => {
            let id = task.id.clone();
            // This fixture replaced the original queue during reset recovery.
            // Deliver the replacement queue's current snapshot to the dialog.
            state.borrow_mut().tasks = tasks;
            Ok(Some(id))
        }
        QueueState::Waiting | QueueState::Running => Ok(None),
        _ => Err(format!("unexpected cleanup task outcome: {task:?}")),
    }
}

fn test_background_failure(ui: &AppWindow) {
    ui.set_operation_busy(true);
    controller::background_for(
        ui,
        controller::Activity::Operation,
        || panic!("controlled worker failure"),
        |_, _, _: ()| unreachable!(),
    );
}
fn test_empty_catalog(ui: &AppWindow, state: &State) {
    let generation = state.borrow().catalog_generation;
    controller::apply_catalog(
        ui,
        state,
        generation,
        Ok((
            chillhub_native::net::CatalogSnapshot {
                library: Default::default(),
                offline: false,
                all_entries_valid: true,
                fetched_at: String::new(),
                warning: None,
            },
            vec![],
        )),
    );
}
