use super::*;
#[test]
fn available_space_uses_existing_games_root_ancestor() {
    let temp = tempfile::tempdir().unwrap();
    let games = temp.path().join("games");
    std::fs::create_dir_all(&games).unwrap();
    assert!(available_space(&games.join("not-installed-yet")).is_some());
}
#[test]
fn maintenance_blocks_only_named_operations() {
    let m = Maintenance {
        enabled: true,
        blocks: crate::net::MaintenanceBlocks {
            install: true,
            update: false,
            launch: false,
        },
        ..Default::default()
    };
    assert!(blocked(&m, &LaunchAction::InstallGame));
    assert!(!blocked(&m, &LaunchAction::Play));
    assert!(!blocked(&m, &LaunchAction::UpdateMods));
    assert!(!blocked(&m, &LaunchAction::InstallMods));
    let updating = Maintenance {
        enabled: true,
        blocks: crate::net::MaintenanceBlocks {
            install: false,
            update: true,
            launch: false,
        },
        ..Default::default()
    };
    assert!(blocked(&updating, &LaunchAction::InstallMods));
    let off = Maintenance {
        enabled: false,
        ..m
    };
    assert!(!blocked(&off, &LaunchAction::InstallGame));
}

#[test]
fn confirmed_launch_restriction_survives_failed_checks_until_server_clears_it() {
    let temp = tempfile::tempdir().unwrap();
    let mut services = Services::new(AppPaths::isolated(temp.path())).unwrap().0;
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    services.api = ApiClient::new(&format!("http://{}", server.server_addr())).unwrap();
    let polling_api = services.api.clone();
    let serve = std::thread::spawn(move || {
        for (status, body) in [
            (503, "{}"),
            (
                200,
                r#"{"enabled":true,"message":"Обновляем игры","blocks":{"launch":true}}"#,
            ),
            (503, "{}"),
            (200, "{}"),
            (200, r#"{"enabled":false}"#),
            (503, "{}"),
            (503, "{}"),
        ] {
            let request = server
                .recv_timeout(std::time::Duration::from_secs(4))
                .unwrap()
                .expect("maintenance request");
            assert_eq!(request.url(), "/api/maintenance");
            request
                .respond(tiny_http::Response::from_string(body).with_status_code(status))
                .unwrap();
        }
    });
    assert!(services.maintenance_check(&LaunchAction::Play).is_ok());
    assert!(polling_api.maintenance().unwrap().blocks.launch);
    assert_eq!(
        services.cached_launch_maintenance_check().unwrap_err(),
        "Обновляем игры"
    );
    assert_eq!(
        services.maintenance_check(&LaunchAction::Play).unwrap_err(),
        "Обновляем игры"
    );
    assert_eq!(
        services.maintenance_check(&LaunchAction::Play).unwrap_err(),
        "Обновляем игры"
    );
    assert!(services.maintenance_check(&LaunchAction::Play).is_ok());
    assert!(services.cached_launch_maintenance_check().is_ok());
    assert!(services.maintenance_check(&LaunchAction::Play).is_ok());
    assert!(
        services
            .maintenance_check(&LaunchAction::InstallGame)
            .is_err()
    );
    serve.join().unwrap();
    services.queue.shutdown();
}
#[test]
fn delete_removes_only_the_confirmed_game() {
    let temp = tempfile::tempdir().unwrap();
    let library = temp.path().join("games");
    let game = library.join("peak");
    let other = library.join("repo");
    std::fs::create_dir_all(&game).unwrap();
    std::fs::create_dir_all(&other).unwrap();
    std::fs::write(game.join("game.exe"), b"game").unwrap();
    std::fs::write(other.join("keep"), b"other").unwrap();
    delete_game_directory(&library, &game, "peak").unwrap();
    assert!(!game.exists());
    assert_eq!(std::fs::read(other.join("keep")).unwrap(), b"other");
}
#[test]
fn delete_handles_every_utf16_path_alignment() {
    let temp = tempfile::tempdir().unwrap();
    for extra in 0..8 {
        let library = temp
            .path()
            .join(format!("Библиотека {} конец", "x".repeat(extra)));
        let game = library.join("old-one");
        let other = library.join("keep");
        std::fs::create_dir_all(&game).unwrap();
        std::fs::create_dir_all(&other).unwrap();
        std::fs::write(game.join("fixture"), b"remove").unwrap();
        std::fs::write(other.join("fixture"), b"keep").unwrap();
        delete_game_directory(&library, &game, "old-one")
            .unwrap_or_else(|e| panic!("path alignment {extra}: {e}"));
        assert!(!game.exists());
        assert_eq!(std::fs::read(other.join("fixture")).unwrap(), b"keep");
        assert_eq!(std::fs::read_dir(&library).unwrap().count(), 1);
    }
}

#[test]
fn delete_removes_readonly_game_files() {
    let temp = tempfile::tempdir().unwrap();
    let library = temp.path().join("games");
    let game = library.join("peak");
    std::fs::create_dir_all(&game).unwrap();
    let file = game.join("game.exe");
    std::fs::write(&file, b"game").unwrap();
    let mut permissions = std::fs::metadata(&file).unwrap().permissions();
    permissions.set_readonly(true);
    std::fs::set_permissions(&file, permissions).unwrap();
    delete_game_directory(&library, &game, "peak").unwrap();
    assert!(!game.exists());
    assert_eq!(std::fs::read_dir(&library).unwrap().count(), 0);
}
#[test]
fn delete_refuses_an_active_transfer_and_outside_root() {
    let temp = tempfile::tempdir().unwrap();
    let library = temp.path().join("games");
    let game = library.join("peak");
    std::fs::create_dir_all(&game).unwrap();
    let lock =
        crate::manifest::open_regular(&game.join(".chillhub.lock"), true, true, false).unwrap();
    fs2::FileExt::try_lock_exclusive(&lock).unwrap();
    assert!(delete_game_directory(&library, &game, "peak").is_err());
    assert!(game.exists());
    drop(lock);
    let outside = temp.path().join("outside");
    std::fs::create_dir_all(&outside).unwrap();
    assert!(delete_game_directory(&library, &outside, "outside").is_err());
    assert!(outside.exists());
}

#[cfg(windows)]
#[test]
fn paused_alias_task_blocks_delete_enqueue_estimate_and_integrity_checks() {
    let temp = tempfile::tempdir().unwrap();
    let mut paths = AppPaths::isolated(temp.path());
    let library = temp.path().join("Long game library directory");
    let root = library.join("fixture");
    std::fs::create_dir_all(&root).unwrap();
    std::fs::create_dir_all(&paths.data_dir).unwrap();
    std::fs::write(root.join("Game.exe"), b"inert fixture, not an executable").unwrap();
    paths.games_dir = std::fs::canonicalize(&library).unwrap();
    let prefs = AppConfig {
        games_path: paths.games_dir.to_string_lossy().into_owned(),
        ..Default::default()
    };
    ConfigStore::new(paths.clone()).save(&prefs).unwrap();
    let item = crate::queue::QueueItem {
        id: "paused".into(),
        request: TransferRequest {
            game_id: "fixture".into(),
            root: crate::paths::short_test_path(&library).join("fixture"),
            ..Default::default()
        },
        state: QueueState::Paused,
        progress: None,
        outcome: None,
        error: None,
        cleanup_files: vec![],
        report: Default::default(),
    };
    std::fs::write(
        paths.data_dir.join("download-queue.json"),
        serde_json::to_vec(&vec![item]).unwrap(),
    )
    .unwrap();
    let (services, _) = Services::new(paths).unwrap();
    let game = GameRecord {
        game_id: "fixture".into(),
        has_latest: true,
        latest_version: "1.0.0".into(),
        exe_relative_path: "Game.exe".into(),
        ..Default::default()
    };
    assert!(
        services
            .delete_local(&game)
            .unwrap_err()
            .contains("отмените")
    );
    assert!(
        services
            .enqueue_game(&game, LaunchTarget::LocalVanilla, None, false, false)
            .unwrap_err()
            .contains("уже есть загрузка")
    );
    assert!(
        services
            .estimate_download(&game, LaunchTarget::LocalVanilla)
            .unwrap_err()
            .contains("после текущей загрузки")
    );
    assert!(!services.verify_game_status(&game).unwrap());
    assert!(root.join("Game.exe").is_file());
    assert_eq!(services.queue.snapshot()[0].state, QueueState::Paused);
    services.queue.shutdown();
}
