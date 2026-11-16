use super::*;
fn fixture() -> (tempfile::TempDir, AppPaths, GameRecord, SteamGame) {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let mut game = GameRecord {
        game_id: "peak".into(),
        latest_version: "1".into(),
        exe_relative_path: "PEAK.exe".into(),
        ..Default::default()
    };
    game.mods = Some(ModsRecord {
        has_latest: true,
        version: "pack1".into(),
        revision: "abc".into(),
        steam_app_id: "3527290".into(),
        ..Default::default()
    });
    let steam_root = temp.path().join("steam");
    fs::create_dir_all(&steam_root).unwrap();
    fs::write(steam_root.join("steam.exe"), b"fixture only").unwrap();
    let steam_game = steam_root.join("game");
    for root in [paths.game_root("peak").unwrap(), steam_game.clone()] {
        fs::create_dir_all(&root).unwrap();
        for (name, contents) in [
            ("PEAK.exe", "fixture only"),
            (".version", "1"),
            (".mods.version", "pack1"),
            (".mods.revision", "abc"),
            ("winhttp.dll", "fixture"),
            ("doorstop_config.ini", "[General]\nenabled = false\n"),
        ] {
            fs::write(root.join(name), contents).unwrap();
        }
    }
    let steam = SteamGame {
        outcome: SteamLookup::Found,
        game_dir: steam_game,
        steam_exe: steam_root.join("steam.exe"),
        trace: vec![],
    };
    (temp, paths, game, steam)
}
#[test]
fn four_plans_are_read_only_and_pick_correct_executables() {
    let (_temp, paths, game, steam) = fixture();
    let service = LaunchService::new(paths);
    let before = fs::read(steam.game_dir.join("doorstop_config.ini")).unwrap();
    for target in [
        LaunchTarget::SteamModded,
        LaunchTarget::SteamVanilla,
        LaunchTarget::LocalModded,
        LaunchTarget::LocalVanilla,
    ] {
        let plan = service
            .plan_with_steam(&game, target, Some(&steam))
            .unwrap();
        assert_eq!(
            plan.executable.file_name().unwrap(),
            if target.via_steam() {
                "steam.exe"
            } else {
                "PEAK.exe"
            }
        );
        assert_eq!(
            plan.arguments,
            if target.via_steam() {
                vec!["-applaunch", "3527290"]
            } else {
                vec![]
            }
        );
    }
    assert_eq!(
        fs::read(steam.game_dir.join("doorstop_config.ini")).unwrap(),
        before
    );
    assert!(!service.paths.config_path().exists());
}
#[test]
fn absent_exe_and_unfinished_update_cannot_be_launched() {
    let (_temp, paths, game, steam) = fixture();
    let local = paths.game_root("peak").unwrap();
    let service = LaunchService::new(paths);
    fs::remove_file(local.join("PEAK.exe")).unwrap();
    assert!(
        service
            .plan_with_steam(&game, LaunchTarget::LocalVanilla, Some(&steam))
            .is_err()
    );
    fs::write(steam.game_dir.join(".updating"), "1").unwrap();
    assert!(
        service
            .plan_with_steam(&game, LaunchTarget::SteamVanilla, Some(&steam))
            .is_err()
    );
}
#[test]
fn mods_only_and_plain_only_have_correct_supported_targets() {
    let (_temp, paths, mut game, steam) = fixture();
    game.latest_version.clear();
    let opts = options_with_steam(&game, &paths, Some(&steam)).unwrap();
    assert_eq!(opts.len(), 2);
    assert!(opts.iter().all(|o| o.target.via_steam()));
    game.latest_version = "1".into();
    game.mods = None;
    let opts = options_with_steam(&game, &paths, None).unwrap();
    assert_eq!(opts.len(), 1);
    assert_eq!(opts[0].target, LaunchTarget::LocalVanilla);
}
#[test]
fn rebuilt_revision_requires_update_and_broken_loader_repair() {
    let (_temp, paths, mut game, steam) = fixture();
    game.mods.as_mut().unwrap().revision = "new".into();
    assert_eq!(
        options_with_steam(&game, &paths, Some(&steam)).unwrap()[0].action,
        LaunchAction::UpdateMods
    );
    game.mods.as_mut().unwrap().revision = "abc".into();
    fs::remove_file(steam.game_dir.join("winhttp.dll")).unwrap();
    assert_eq!(
        options_with_steam(&game, &paths, Some(&steam)).unwrap()[0].action,
        LaunchAction::RepairMods
    );
}
#[test]
fn doorstop_preserves_comments_and_rejects_missing_key() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("doorstop_config.ini");
    fs::write(
        &path,
        "[General]\r\n#enabled=false\r\nenabled = true ; comment\r\nother=1\r\n",
    )
    .unwrap();
    set_doorstop_enabled(temp.path(), false).unwrap();
    assert_eq!(
        fs::read_to_string(&path).unwrap(),
        "[General]\r\n#enabled=false\r\nenabled = false ; comment\r\nother=1\r\n"
    );
    fs::write(&path, "[General]\nother=1").unwrap();
    assert!(set_doorstop_enabled(temp.path(), true).is_err());
}
#[test]
fn steam_discovers_legacy_and_modern_libraries_and_nested_game() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("Steam");
    let library = temp.path().join("Библиотека");
    fs::create_dir_all(root.join("steamapps")).unwrap();
    fs::create_dir_all(library.join("steamapps/common/Outer/Inner")).unwrap();
    fs::write(root.join("steam.exe"), "fixture").unwrap();
    fs::write(
        library.join("steamapps/appmanifest_123.acf"),
        "\"AppState\" { \"installdir\" \"Outer\" }",
    )
    .unwrap();
    let escaped = library.to_string_lossy().replace('\\', "\\\\");
    for body in [
        format!("\"LibraryFolders\" {{ \"1\" \"{escaped}\" }}"),
        format!("\"libraryfolders\" {{ \"1\" {{ \"path\" \"{escaped}\" }} }}"),
    ] {
        fs::write(root.join("steamapps/libraryfolders.vdf"), body).unwrap();
        let found = locate_steam_at(Some(&root), "123", "Outer/Inner");
        assert!(found.available());
        assert!(found.game_dir.ends_with("Outer/Inner"));
    }
    assert_eq!(
        locate_steam_at(Some(&root), "../evil", "").outcome,
        SteamLookup::NoAppId
    );
}
#[test]
fn shortcut_rejects_path_escape_and_uri_command_injection() {
    let (_temp, paths, _game, steam) = fixture();
    let request = ShortcutRequest {
        game_id: "peak".into(),
        exe_path: Some(steam.steam_exe),
        ..Default::default()
    };
    assert!(request.allowed_fallback(&paths).is_err());
    assert!(ShortcutRequest::parse_uri("chillhub://game/peak?exe=C:/evil.exe").is_err());
    assert_eq!(
        ShortcutRequest::parse_uri("chillhub://game/peak?target=SteamVanilla")
            .unwrap()
            .target,
        Some(LaunchTarget::SteamVanilla)
    );
    assert!(ShortcutRequest::parse_args(&["--game".into(), "../evil".into()]).is_err());
    assert_eq!(quote_windows_argument("a\"b\\"), "\"a\\\"b\\\\\"");
}
#[test]
fn shortcut_legacy_keys_do_not_consume_the_next_option() {
    let request = ShortcutRequest::parse_args(&[
        "--TITLE".into(),
        "--GAME".into(),
        "peak".into(),
        "--EXE=C:/Games/peak/game.exe".into(),
    ])
    .unwrap()
    .unwrap();
    assert_eq!(request.game_id, "peak");
    assert!(request.title.is_empty());
    assert_eq!(
        request.exe_path,
        Some(PathBuf::from("C:/Games/peak/game.exe"))
    );
    assert!(
        ShortcutRequest::parse_args(&["--game".into()])
            .unwrap()
            .is_none()
    );
    assert!(ShortcutRequest::parse_args(&["--GAME=../escape".into()]).is_err());
}
#[test]
fn malformed_vdf_is_bounded_and_rejected() {
    assert!(parse_vdf("\"a\" { \"b\"").is_err());
    assert!(parse_vdf(&format!("{}{}", "\"a\" {".repeat(40), "}".repeat(40))).is_err());
}

#[test]
fn doorstop_accepts_utf16_boms_and_numeric_states_without_losing_formatting() {
    let (_temp, paths, game, steam) = fixture();
    let path = steam.game_dir.join("doorstop_config.ini");
    let template = "# описание\r\n[UnityDoorstop]\n\tEnAbLeD = 1\t # оставить  \r\nother=true";
    for (bom, encode) in [
        ([0xff, 0xfe], u16::to_le_bytes as fn(u16) -> [u8; 2]),
        ([0xfe, 0xff], u16::to_be_bytes as fn(u16) -> [u8; 2]),
    ] {
        let encoded = |text: &str| {
            bom.into_iter()
                .chain(text.encode_utf16().flat_map(encode))
                .collect::<Vec<_>>()
        };
        fs::write(&path, encoded(template)).unwrap();
        let options = options_with_steam(&game, &paths, Some(&steam)).unwrap();
        assert!(
            options
                .iter()
                .find(|option| option.target == LaunchTarget::SteamModded)
                .unwrap()
                .ready()
        );
        set_doorstop_enabled(&steam.game_dir, true).unwrap();
        assert_eq!(fs::read(&path).unwrap(), encoded(template));
        set_doorstop_enabled(&steam.game_dir, false).unwrap();
        assert_eq!(
            fs::read(&path).unwrap(),
            encoded(&template.replace(" = 1", " = false"))
        );
        fs::write(&path, encoded(&template.replace(" = 1", " = 0"))).unwrap();
        assert!(doorstop_config_valid(&steam.game_dir));
        set_doorstop_enabled(&steam.game_dir, false).unwrap();
        assert_eq!(
            fs::read(&path).unwrap(),
            encoded(&template.replace(" = 1", " = 0"))
        );
    }
    fs::write(&path, [0xff, 0xfe, b'e']).unwrap();
    assert!(!doorstop_config_valid(&steam.game_dir));
    assert!(set_doorstop_enabled(&steam.game_dir, false).is_err());
    assert_eq!(fs::read(path).unwrap(), [0xff, 0xfe, b'e']);
}

#[test]
fn steam_executable_basename_is_case_insensitive_including_unicode() {
    let expected = Path::new("Game/ИГРА.exe");
    assert!(steam_handoff::same_executable_name(
        Path::new("Other/игра.EXE"),
        expected
    ));
    assert!(!steam_handoff::same_executable_name(
        Path::new("Other/игра-другая.exe"),
        expected
    ));
}

#[cfg(windows)]
fn system_executable(name: &str) -> PathBuf {
    PathBuf::from(std::env::var_os("SystemRoot").unwrap())
        .join("System32")
        .join(name)
}

#[cfg(windows)]
fn wait_until(description: &str, mut ready: impl FnMut() -> bool) {
    let deadline = Instant::now() + Duration::from_secs(30);
    while !ready() {
        assert!(
            Instant::now() < deadline,
            "fixture timed out while waiting for {description}"
        );
        std::thread::sleep(Duration::from_millis(30));
    }
}

#[cfg(windows)]
fn write_game_wait_script(root: &Path) {
    fs::write(
        root.join("game.cmd"),
        concat!(
            "@echo off\r\n",
            "set attempts=40\r\n",
            ":wait\r\n",
            "if exist stop-game exit /b 0\r\n",
            "set /a attempts-=1\r\n",
            "if %attempts% LEQ 0 exit /b 1\r\n",
            "\"%SystemRoot%\\System32\\ping.exe\" -n 2 127.0.0.1 >nul\r\n",
            "goto wait\r\n",
        ),
    )
    .unwrap();
}

#[cfg(windows)]
#[test]
fn unavailable_preferences_and_corrupt_statistics_do_not_stop_vanilla_launch() {
    let mut launched = Vec::new();
    for corrupt in [None, Some("playtime.sessions.json"), Some("playtime.json")] {
        let temp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(temp.path());
        let root = paths.game_root("optional-state-fixture").unwrap();
        fs::create_dir_all(&root).unwrap();
        let executable = root.join("Game.exe");
        fs::copy(system_executable("ping.exe"), &executable).unwrap();
        fs::create_dir_all(&paths.data_dir).unwrap();
        if let Some(name) = corrupt {
            fs::write(paths.data_dir.join(name), b"{invalid json").unwrap();
        } else {
            fs::create_dir(paths.data_dir.join("config.lock")).unwrap();
        }
        let doorstop = root.join("doorstop_config.ini");
        fs::write(&doorstop, "enabled=1").unwrap();
        let service = LaunchService::new(paths);
        let plan = LaunchPlan {
            game_id: "optional-state-fixture".into(),
            target: LaunchTarget::LocalVanilla,
            game_executable: executable.clone(),
            executable,
            arguments: vec!["-n".into(), "4".into(), "127.0.0.1".into()],
            working_directory: root.clone(),
            game_directory: root,
        };

        let LaunchOutcome::Started(session) =
            service.execute_plan(plan, SteamWait::default()).unwrap()
        else {
            panic!("a local game had no tracked process");
        };
        assert!(session.process.is_running());
        assert_eq!(session.warning.is_some(), corrupt.is_some());
        assert_eq!(fs::read_to_string(doorstop).unwrap(), "enabled=false");
        launched.push((temp, service, session));
    }
    for (_temp, service, session) in launched {
        let root = session.process.exe_path.parent().unwrap();
        wait_until("the local process and its lock to close", || {
            !session.process.is_running()
                && LaunchLocks::acquire(&service.paths.data_dir, root, true).is_ok()
        });
    }
}

#[cfg(windows)]
#[test]
fn stale_loader_recovery_failure_is_still_fatal_for_vanilla() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let root = paths.game_root("recovery-fixture").unwrap();
    fs::create_dir_all(&root).unwrap();
    fs::create_dir_all(&paths.data_dir).unwrap();
    let executable = root.join("Game.exe");
    fs::copy(system_executable("ping.exe"), &executable).unwrap();
    let commit = paths.data_dir.join("playtime.commit.json");
    fs::write(&commit, b"{broken loader journal").unwrap();
    let doorstop = root.join("doorstop_config.ini");
    fs::write(&doorstop, b"enabled=true").unwrap();
    let service = LaunchService::new(paths);
    let request = ShortcutRequest {
        game_id: "recovery-fixture".into(),
        exe_path: Some(executable),
        ..Default::default()
    };
    assert!(service.launch_shortcut(&request).is_err());
    assert!(processes_in(&root).unwrap().is_empty());
    assert_eq!(fs::read_to_string(&doorstop).unwrap(), "enabled=true");

    fs::remove_file(commit).unwrap();
    fs::write(&doorstop, b"[General]\nother=true").unwrap();
    let error = service.launch_shortcut(&request).unwrap_err();
    assert!(error.contains("enabled"), "{error}");
    assert!(processes_in(&root).unwrap().is_empty());
}

#[cfg(windows)]
fn simulated_steam(script: &str) -> (tempfile::TempDir, LaunchService, LaunchPlan) {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let root = paths.game_root("steam-handoff-fixture").unwrap();
    let expected = root.join("expected");
    let actual = root.join("actual");
    fs::create_dir_all(&expected).unwrap();
    fs::create_dir_all(&actual).unwrap();
    fs::copy(system_executable("cmd.exe"), actual.join("GAME.exe")).unwrap();
    let expected_executable = expected.join("Game.exe");
    fs::copy(system_executable("ping.exe"), &expected_executable).unwrap();
    fs::write(root.join("mock-steam.cmd"), script).unwrap();
    fs::write(root.join("doorstop_config.ini"), b"enabled=false").unwrap();
    let plan = LaunchPlan {
        game_id: "steam-handoff-fixture".into(),
        target: LaunchTarget::SteamModded,
        game_executable: expected_executable,
        executable: system_executable("cmd.exe"),
        arguments: vec!["/D".into(), "/C".into(), "mock-steam.cmd".into()],
        working_directory: root.clone(),
        game_directory: root,
    };
    (temp, LaunchService::new(paths), plan)
}

#[cfg(windows)]
#[test]
fn steam_finds_a_late_nested_process_and_restores_loader_only_after_it_exits() {
    let (_temp, service, plan) = simulated_steam(concat!(
        "@echo off\r\n",
        "set attempts=40\r\n",
        ":wait\r\n",
        "if exist start-game goto start\r\n",
        "set /a attempts-=1\r\n",
        "if %attempts% LEQ 0 exit /b 1\r\n",
        "\"%SystemRoot%\\System32\\ping.exe\" -n 2 127.0.0.1 >nul\r\n",
        "goto wait\r\n",
        ":start\r\n",
        "actual\\GAME.exe /D /C game.cmd\r\n",
    ));
    let game_root = plan.game_directory.clone();
    write_game_wait_script(&game_root);
    let doorstop = game_root.join("doorstop_config.ini");
    let outcome = service
        .execute_plan(
            plan,
            SteamWait {
                report_after: Duration::from_millis(100),
                watch_after: Duration::from_secs(35),
                interval: Duration::from_millis(30),
            },
        )
        .unwrap();
    let LaunchOutcome::SteamPending { late_session, .. } = outcome else {
        panic!("the game started before the simulated Steam handoff was released");
    };
    assert_eq!(fs::read_to_string(&doorstop).unwrap(), "enabled=true");
    assert!(LaunchLocks::acquire(&service.paths.data_dir, &game_root, false).is_err());
    fs::write(game_root.join("start-game"), []).unwrap();

    let process = late_session
        .recv_timeout(Duration::from_secs(30))
        .expect("the nested Steam game started but was not reported")
        .process;
    assert!(process.exe_path.ends_with("actual/GAME.exe"));
    wait_until("the nested process to be recorded in playtime", || {
        service
            .playtime
            .pending()
            .unwrap()
            .contains_key(&process.pid.to_string())
    });
    assert_eq!(fs::read_to_string(&doorstop).unwrap(), "enabled=true");
    service.recover_loader_sessions().unwrap();
    assert_eq!(fs::read_to_string(&doorstop).unwrap(), "enabled=true");

    fs::write(game_root.join("stop-game"), []).unwrap();
    wait_until("the loader to be restored after the process exits", || {
        fs::read_to_string(&doorstop).unwrap() == "enabled=false"
    });
    assert!(!process.is_running());
}

#[cfg(windows)]
#[test]
fn restart_with_running_game_defers_restore_and_retries_failed_cleanup() {
    let (_temp, service, plan) = simulated_steam("@echo off\r\nexit /b 0\r\n");
    let root = plan.game_directory;
    let doorstop = root.join("doorstop_config.ini");
    let locks = LaunchLocks::acquire(&service.paths.data_dir, &root, false).unwrap();
    locks.prepare_loader(true, None).unwrap();
    drop(locks);
    write_game_wait_script(&root);

    let mut child = Command::new(root.join("actual/GAME.exe"))
        .current_dir(&root)
        .args(["/D", "/C", "game.cmd"])
        .spawn()
        .unwrap();
    let journal = loader_journal(&service.paths);
    service.recover_loader_sessions().unwrap();
    assert_eq!(fs::read_to_string(&doorstop).unwrap(), "enabled=true");
    assert!(LaunchLocks::acquire(&service.paths.data_dir, &root, false).is_err());

    fs::write(&doorstop, b"other=true").unwrap();
    fs::write(root.join("stop-game"), []).unwrap();
    assert!(child.wait().unwrap().success());
    wait_until("failed cleanup to release the game lock", || {
        LaunchLocks::acquire(&service.paths.data_dir, &root, false).is_ok()
    });
    assert!(journal.is_file());
    assert_eq!(fs::read_to_string(&doorstop).unwrap(), "other=true");
    fs::write(&doorstop, b"enabled=true").unwrap();
    service.recover_loader_sessions().unwrap();
    assert_eq!(fs::read_to_string(doorstop).unwrap(), "enabled=false");
    assert!(!journal.exists());
}

#[cfg(windows)]
#[test]
fn restart_during_pending_steam_handoff_waits_before_restoring_loader() {
    let (_temp, service, plan) = simulated_steam("@echo off\r\nexit /b 0\r\n");
    let root = plan.game_directory;
    let doorstop = root.join("doorstop_config.ini");
    let locks = LaunchLocks::acquire(&service.paths.data_dir, &root, false).unwrap();
    locks
        .prepare_loader(true, Some(chrono::Utc::now() + Duration::from_secs(3)))
        .unwrap();
    drop(locks);

    service.recover_loader_sessions().unwrap();
    assert_eq!(fs::read_to_string(&doorstop).unwrap(), "enabled=true");
    assert!(LaunchLocks::acquire(&service.paths.data_dir, &root, false).is_err());
    wait_until(
        "the resumed Steam deadline before restoring the loader",
        || fs::read_to_string(&doorstop).unwrap() == "enabled=false",
    );
}

#[cfg(windows)]
#[test]
fn unresolved_steam_handoff_can_be_recovered_by_the_next_launcher_start() {
    let (_temp, service, plan) = simulated_steam("@echo off\r\nexit /b 0\r\n");
    let root = plan.game_directory.clone();
    let doorstop = root.join("doorstop_config.ini");
    let result = service
        .execute_plan(
            plan,
            SteamWait {
                report_after: Duration::from_millis(50),
                watch_after: Duration::from_millis(150),
                interval: Duration::from_millis(30),
            },
        )
        .unwrap();
    assert!(matches!(result, LaunchOutcome::SteamPending { .. }));
    wait_until("the unresolved handoff to release its locks", || {
        LaunchLocks::acquire(&service.paths.data_dir, &root, true).is_ok()
    });
    assert_eq!(fs::read_to_string(&doorstop).unwrap(), "enabled=true");
    LaunchService::new(service.paths.clone())
        .recover_loader_sessions()
        .unwrap();
    assert_eq!(fs::read_to_string(&doorstop).unwrap(), "enabled=false");
    assert!(
        !fs::read_dir(service.paths.data_dir.join("launch-locks"))
            .unwrap()
            .any(|entry| entry
                .unwrap()
                .file_name()
                .to_string_lossy()
                .ends_with(".loader.json"))
    );
}

fn loader_journal(paths: &AppPaths) -> PathBuf {
    fs::read_dir(paths.data_dir.join("launch-locks"))
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .find(|path| {
            path.file_name()
                .is_some_and(|name| name.to_string_lossy().ends_with(".loader.json"))
        })
        .expect("expected one loader journal")
}

#[test]
fn loader_recovery_rejects_malformed_oversized_and_hardlinked_journals() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let root = paths.game_root("loader-journal-fixture").unwrap();
    fs::create_dir_all(&root).unwrap();
    let doorstop = root.join("doorstop_config.ini");
    fs::write(&doorstop, "enabled=false").unwrap();
    let locks = LaunchLocks::acquire(&paths.data_dir, &root, false).unwrap();
    locks.prepare_loader(true, None).unwrap();
    drop(locks);
    let journal = loader_journal(&paths);
    let valid = fs::read(&journal).unwrap();
    let service = LaunchService::new(paths);

    fs::write(&journal, b"{malformed").unwrap();
    assert!(service.recover_loader_sessions().is_err());
    fs::write(&journal, vec![b' '; 512 * 1024]).unwrap();
    let error = service.recover_loader_sessions().unwrap_err();
    assert!(error.contains("слишком большой"), "{error}");

    fs::remove_file(&journal).unwrap();
    let donor = temp.path().join("donor.json");
    fs::write(&donor, &valid).unwrap();
    fs::hard_link(&donor, &journal).unwrap();
    assert!(service.recover_loader_sessions().is_err());
    assert_eq!(fs::read(&donor).unwrap(), valid);
    assert_eq!(fs::read_to_string(doorstop).unwrap(), "enabled=true");
}

#[test]
fn loader_recovery_checks_the_exact_copy_and_retains_record_when_game_is_unavailable() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let service = LaunchService::new(paths.clone());
    service.recover_loader_sessions().unwrap();
    assert!(!paths.data_dir.exists());
    let root = paths.game_root("original").unwrap();
    let other = paths.game_root("other-copy").unwrap();
    fs::create_dir_all(&root).unwrap();
    fs::create_dir_all(&other).unwrap();
    fs::write(root.join("doorstop_config.ini"), "enabled=false").unwrap();
    fs::write(other.join("doorstop_config.ini"), "enabled=true").unwrap();
    let locks = LaunchLocks::acquire(&paths.data_dir, &root, false).unwrap();
    locks.prepare_loader(true, None).unwrap();
    drop(locks);

    let journal = loader_journal(&paths);
    let original_journal = fs::read(&journal).unwrap();
    let unavailable = paths.game_root("temporarily-offline").unwrap();
    fs::rename(&root, &unavailable).unwrap();
    service.recover_loader_sessions().unwrap();
    assert!(journal.is_file());
    assert_eq!(
        fs::read_to_string(unavailable.join("doorstop_config.ini")).unwrap(),
        "enabled=true"
    );

    let mut redirected: serde_json::Value = serde_json::from_slice(&original_journal).unwrap();
    redirected["game_root"] = serde_json::json!(other);
    fs::write(&journal, serde_json::to_vec(&redirected).unwrap()).unwrap();
    assert!(
        service
            .recover_loader_sessions()
            .unwrap_err()
            .contains("Некорректный")
    );
    assert_eq!(
        fs::read_to_string(other.join("doorstop_config.ini")).unwrap(),
        "enabled=true"
    );

    fs::rename(unavailable, &root).unwrap();
    fs::write(&journal, original_journal).unwrap();
    fs::write(
        paths.data_dir.join("launch-locks/unrelated.loader.json"),
        b"unrelated",
    )
    .unwrap();
    service.recover_loader_sessions().unwrap();
    assert!(!journal.exists());
    assert_eq!(
        fs::read_to_string(root.join("doorstop_config.ini")).unwrap(),
        "enabled=false"
    );
    assert_eq!(
        fs::read_to_string(other.join("doorstop_config.ini")).unwrap(),
        "enabled=true"
    );
}

#[cfg(windows)]
#[test]
fn launch_respects_transfer_lock_and_rolls_back_loader_if_executable_cannot_spawn() {
    let (_temp, paths, game, steam) = fixture();
    let service = LaunchService::new(paths.clone());
    let root = paths.game_root("peak").unwrap();
    let doorstop = root.join("doorstop_config.ini");
    let plan = service
        .plan_with_steam(&game, LaunchTarget::LocalModded, Some(&steam))
        .unwrap();
    let lock =
        crate::manifest::open_regular(&root.join(".chillhub.lock"), true, true, false).unwrap();
    fs2::FileExt::try_lock_exclusive(&lock).unwrap();
    let before = fs::read(&doorstop).unwrap();
    let error = service
        .execute_plan(plan.clone(), SteamWait::default())
        .unwrap_err();
    assert!(
        error.contains("защитить") || error.contains("обновляются"),
        "{error}"
    );
    assert_eq!(fs::read(&doorstop).unwrap(), before);
    drop(lock);

    fs::write(root.join(".updating"), b"interrupted").unwrap();
    assert!(
        service
            .execute_plan(plan.clone(), SteamWait::default())
            .unwrap_err()
            .contains("не завершено")
    );
    fs::remove_file(root.join(".updating")).unwrap();
    let error = service
        .execute_plan(plan, SteamWait::default())
        .unwrap_err();
    assert!(error.contains("Не удалось запустить игру"), "{error}");
    assert_eq!(fs::read(doorstop).unwrap(), before);
    assert!(
        fs::read_dir(paths.data_dir.join("launch-locks"))
            .unwrap()
            .all(|entry| !entry
                .unwrap()
                .file_name()
                .to_string_lossy()
                .ends_with(".loader.json"))
    );
}

#[test]
fn steam_discovery_distinguishes_missing_library_manifest_and_installation() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path();
    let apps = root.join("steamapps");
    let manifest = apps.join("appmanifest_123.acf");
    let locate = || locate_steam_at(Some(root), "123", "Other/Inner");
    assert_eq!(
        locate_steam_at(None, "123", "").outcome,
        SteamLookup::SteamNotInstalled
    );
    fs::write(root.join("steam.exe"), b"fixture").unwrap();
    assert_eq!(locate().outcome, SteamLookup::NoLibraries);
    fs::create_dir(&apps).unwrap();
    assert_eq!(locate().outcome, SteamLookup::GameNotInstalled);
    fs::write(&manifest, br#""AppState" { "installdir" "../elsewhere" }"#).unwrap();
    assert_eq!(locate().outcome, SteamLookup::GameNotInstalled);
    fs::write(&manifest, br#""AppState" { "installdir" "Here" }"#).unwrap();
    assert_eq!(locate().outcome, SteamLookup::FolderMissing);
    let expected = apps.join("common/Here");
    fs::create_dir_all(&expected).unwrap();
    let found = locate();
    assert!(found.available() && found.reason().is_empty());
    assert_eq!(found.game_dir, expected);
    fs::remove_file(root.join("steam.exe")).unwrap();
    let found = locate();
    assert!(!found.available() && !found.reason().is_empty());
}

#[test]
fn executable_fallback_prefers_a_catalog_name_and_rejects_ambiguity_or_escape() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path();
    let bins = root.join("bin");
    fs::create_dir(&bins).unwrap();
    let preferred = bins.join("Alternative.EXE");
    fs::write(&preferred, b"fixture").unwrap();
    let game = root.join("Game.exe");
    fs::write(&game, b"fixture").unwrap();
    fs::write(root.join("UnityCrashHandler64.exe"), b"fixture").unwrap();

    assert_eq!(
        resolve_game_exe(root, "missing.exe", &["bin/Alternative.EXE".into()]).unwrap(),
        preferred
    );
    assert_eq!(resolve_game_exe(root, "", &[]).unwrap(), game);
    fs::write(root.join("Launcher.exe"), b"fixture").unwrap();
    assert!(resolve_game_exe(root, "", &[]).is_err());
    assert!(resolve_game_exe(root, "../Escape.exe", &["Game.exe".into()]).is_err());
}

#[test]
fn modpack_install_publishes_only_verified_inventory_and_keeps_game_plain() {
    let (_temp, paths, game, steam) = fixture();
    let root = &steam.game_dir;
    let doorstop = root.join("doorstop_config.ini");
    let mods = game.mods.as_ref().unwrap();
    let mut new = mods.clone();
    new.version = "pack2".into();
    new.revision = "next".into();
    fs::write(&doorstop, "enabled=1").unwrap();
    let inventory = serde_json::json!({"Files":[{"Path":"BepInEx/plugin.dll"}]});
    assert!(record_modpack_install(root, &new, &inventory).is_err());
    assert_eq!(read_marker(root, ".mods.version"), mods.version);
    assert!(!root.join(".mods.manifest.json").is_file());
    assert_eq!(fs::read_to_string(&doorstop).unwrap(), "enabled=1");

    let plugin = root.join("BepInEx/plugin.dll");
    fs::create_dir(plugin.parent().unwrap()).unwrap();
    fs::write(&plugin, b"verified fixture payload").unwrap();
    record_modpack_install(root, &new, &inventory).unwrap();
    assert_eq!(
        installed_modpack_paths(root).unwrap(),
        vec![PathBuf::from("BepInEx").join("plugin.dll")]
    );
    assert_eq!(read_marker(root, ".mods.version"), "pack2");
    assert_eq!(read_marker(root, ".mods.revision"), "next");
    assert_eq!(fs::read_to_string(doorstop).unwrap(), "enabled=false");

    fs::remove_file(plugin).unwrap();
    let mut catalog = game.clone();
    catalog.mods = Some(new);
    assert_eq!(
        options_with_steam(&catalog, &paths, Some(&steam)).unwrap()[0].action,
        LaunchAction::RepairMods
    );
}

#[test]
fn utf8_bom_and_cr_line_endings_survive_loader_changes() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("doorstop_config.ini");
    let ini = "\u{feff}[General]\rEnabled = TRue  ; comment\rother=1";
    fs::write(&path, ini).unwrap();
    assert!(doorstop_config_valid(temp.path()));
    set_doorstop_enabled(temp.path(), false).unwrap();
    assert_eq!(
        fs::read_to_string(&path).unwrap(),
        ini.replace("TRue", "false")
    );

    fs::write(&path, b"enabled=true\n# malformed: \xff").unwrap();
    assert!(!doorstop_config_valid(temp.path()));
    assert!(set_doorstop_enabled(temp.path(), false).is_err());
    assert_eq!(fs::read(&path).unwrap(), b"enabled=true\n# malformed: \xff");

    fs::remove_file(path).unwrap();
    set_doorstop_enabled(temp.path(), false).unwrap();
    assert!(set_doorstop_enabled(temp.path(), true).is_err());
}

#[test]
fn game_shortcut_rejects_bad_paths_or_game_identity_without_writing() {
    let temp = tempfile::tempdir().unwrap();
    let launcher = temp.path().join("ChillHub.exe");
    let wrong_format = temp.path().join("readme.txt");
    let output = temp.path().join("game.lnk");
    let request = ShortcutRequest {
        game_id: "peak".into(),
        ..Default::default()
    };
    assert!(create_game_shortcut(&launcher, &output, &request).is_err());

    fs::write(&launcher, b"fixture").unwrap();
    fs::write(&wrong_format, b"fixture").unwrap();
    assert!(create_game_shortcut(&wrong_format, &output, &request).is_err());
    assert!(create_game_shortcut(&launcher, &temp.path().join("output.txt"), &request).is_err());
    let invalid = ShortcutRequest {
        game_id: "../peak".into(),
        ..request
    };
    assert!(create_game_shortcut(&launcher, &output, &invalid).is_err());
    assert!(!output.exists());
}

#[test]
fn shortcut_activation_accepts_target_only_on_the_expected_uri() {
    let request = ShortcutRequest::parse_args(&[
        "--ignored".into(),
        "CHILLHUB://game/peak?target=%4cocalModded".into(),
    ])
    .unwrap()
    .unwrap();
    assert_eq!(request.game_id, "peak");
    assert_eq!(request.target, Some(LaunchTarget::LocalModded));
    assert_eq!(request.exe_path, None);
    for uri in [
        "https://game/peak",
        "chillhub://another/peak",
        "chillhub://user@game/peak",
        "chillhub://:password@game/peak",
        "chillhub://game:1/peak",
        "chillhub://game/peak#fragment",
        "chillhub://game/peak%2fother",
        "chillhub://game/peak?target=unknown",
        "chillhub://game/peak?exec=program",
    ] {
        assert!(ShortcutRequest::parse_uri(uri).is_err(), "{uri}");
    }
    assert!(
        ShortcutRequest::parse_args(&["--render".into()])
            .unwrap()
            .is_none()
    );
    assert!(
        ShortcutRequest::parse_args(&["--game=peak".into(), "--target=unknown".into()]).is_err()
    );
}

#[test]
fn shortcut_fallback_is_available_only_for_an_executable_in_ready_game_copy() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let root = paths.game_root("peak").unwrap();
    let nested = root.join("bin");
    fs::create_dir_all(&nested).unwrap();
    let game = nested.join("Game.exe");
    let non_exe = nested.join("readme.txt");
    let fake_exe_dir = nested.join("folder.exe");
    fs::write(&game, b"fixture").unwrap();
    fs::write(&non_exe, b"fixture").unwrap();
    fs::create_dir(&fake_exe_dir).unwrap();
    let mut request = ShortcutRequest {
        game_id: "peak".into(),
        ..Default::default()
    };
    assert!(request.allowed_fallback(&paths).is_err());
    for bad_path in [non_exe, fake_exe_dir, nested.join("missing.exe")] {
        request.exe_path = Some(bad_path);
        assert!(request.allowed_fallback(&paths).is_err());
    }
    request.exe_path = Some(game.clone());
    assert_eq!(
        request.allowed_fallback(&paths).unwrap(),
        fs::canonicalize(game).unwrap()
    );
    fs::write(root.join(".updating"), b"interrupted").unwrap();
    assert!(request.allowed_fallback(&paths).is_err());
}

#[test]
fn steam_vdf_handles_comments_bom_and_escaped_values_with_bounded_reads() {
    let raw = concat!(
        "\u{feff}// Steam configuration\r\n",
        r#"
        AppState {
            "installdir" "Игра \"test\""
            "path" "C:\\Steam Library"
            "metadata" { token value }
        }
    "#
    );
    let parsed = parse_vdf(raw).unwrap();
    let app_state = child(&parsed, "APPSTATE").unwrap();
    assert_eq!(text_value(app_state, "installdir"), Some("Игра \"test\""));
    assert_eq!(text_value(app_state, "PATH"), Some("C:\\Steam Library"));
    assert_eq!(text_value(app_state, "metadata"), None);
    assert!(child(app_state, "installdir").is_none());
    assert_eq!(
        text_value(child(app_state, "metadata").unwrap(), "token"),
        Some("value")
    );
    for malformed in ["\"unfinished", "key", "}", "{ key value }", "key }"] {
        assert!(parse_vdf(malformed).is_err(), "{malformed}");
    }

    let temp = tempfile::tempdir().unwrap();
    let file = temp.path().join("libraryfolders.vdf");
    fs::write(&file, raw).unwrap();
    assert!(read_vdf(&file).is_ok());
    fs::OpenOptions::new()
        .write(true)
        .open(&file)
        .unwrap()
        .set_len(2 * 1024 * 1024 + 1)
        .unwrap();
    assert!(read_vdf(&file).unwrap_err().contains("слишком большой"));
}
