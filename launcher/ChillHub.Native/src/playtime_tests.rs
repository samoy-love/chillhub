use super::*;

fn completion_events(store: &PlaytimeStore) -> std::sync::mpsc::Receiver<CompletedSession> {
    let (sender, receiver) = std::sync::mpsc::channel();
    store.set_completion_handler(move |session| sender.send(session).unwrap());
    receiver
}

#[test]
fn preserves_legacy_and_variant_buckets() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    fs::create_dir_all(&paths.data_dir).unwrap();
    fs::write(paths.data_dir.join("playtime.json"), r#"{"peak":{"TotalSeconds":20},"peak#SteamVanilla":{"TotalSeconds":10},"peak-other":{"TotalSeconds":999}}"#).unwrap();
    assert_eq!(
        PlaytimeStore::new(&paths)
            .totals("PEAK")
            .unwrap()
            .total_seconds,
        30
    );
}
#[test]
fn pid_reuse_does_not_match() {
    let p = ProcessIdentity {
        pid: 5,
        creation_file_time: 20,
        legacy_start_ticks: 100,
        exe_path: "x.exe".into(),
    };
    let mut s = PendingSession {
        game_id: "peak".into(),
        target: None,
        process_id: 5,
        process_start_time_ticks: 100,
        session_start_utc: Utc::now(),
        modded_dir: None,
        process_creation_file_time: Some(19),
        last_observed_utc: None,
    };
    assert!(!matches_process(&s, &p));
    s.process_creation_file_time = None;
    assert!(matches_process(&s, &p));
}
#[test]
fn finish_is_idempotent_and_disables_mods() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let store = PlaytimeStore::new(&paths);
    let completion = completion_events(&store);
    fs::create_dir_all(&paths.data_dir).unwrap();
    let game = paths.game_root("peak").unwrap();
    fs::create_dir_all(&game).unwrap();
    fs::write(
        game.join("doorstop_config.ini"),
        "[General]\nenabled = true\n",
    )
    .unwrap();
    let start = Utc::now();
    let session = PendingSession {
        game_id: "peak".into(),
        target: Some("SteamModded".into()),
        process_id: 999,
        process_start_time_ticks: 1,
        session_start_utc: start,
        modded_dir: Some(game.to_string_lossy().into_owned()),
        process_creation_file_time: Some(1),
        last_observed_utc: None,
    };
    write_json(
        &paths.data_dir.join("playtime.sessions.json"),
        &BTreeMap::from([("999".to_string(), session)]),
    )
    .unwrap();
    store
        .finish(999, Some(1), start + chrono::Duration::seconds(40))
        .unwrap();
    store.finish(999, Some(1), Utc::now()).unwrap();
    assert_eq!(store.totals("peak").unwrap().total_seconds, 40);
    assert_eq!(
        completion.try_recv().unwrap(),
        CompletedSession {
            game_id: "peak".into(),
            duration_ms: 40_000
        }
    );
    assert!(completion.try_recv().is_err());
    assert!(
        fs::read_to_string(game.join("doorstop_config.ini"))
            .unwrap()
            .contains("false")
    );
}

#[test]
fn interrupted_commit_restores_loader_before_forgetting_the_session() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let store = PlaytimeStore::new(&paths);
    let completion = completion_events(&store);
    let game = paths.game_root("peak").unwrap();
    fs::create_dir_all(&game).unwrap();
    fs::write(
        game.join("doorstop_config.ini"),
        "[General]\nenabled=true\n",
    )
    .unwrap();
    let journal = serde_json::json!({
        "Playtime":{"peak#SteamModded":{"TotalSeconds":40,"LastSessionSeconds":40}},
        "Pending":{},"RestoreModdedDirs":[game.to_string_lossy()],
        "CompletedSession":{"game_id":"peak","duration_ms":40000},
    });
    write_json(&paths.data_dir.join("playtime.commit.json"), &journal).unwrap();
    store.recover_commit().unwrap();
    store.recover_commit().unwrap();
    assert_eq!(store.totals("peak").unwrap().total_seconds, 40);
    assert_eq!(
        completion.try_recv().unwrap(),
        CompletedSession {
            game_id: "peak".into(),
            duration_ms: 40_000
        }
    );
    assert!(completion.try_recv().is_err());
    assert!(!paths.data_dir.join("playtime.commit.json").exists());
    assert!(
        fs::read_to_string(game.join("doorstop_config.ini"))
            .unwrap()
            .contains("false")
    );
}

#[test]
fn ended_session_recovered_after_restart_reports_persisted_playtime_once() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let store = PlaytimeStore::new(&paths);
    let completion = completion_events(&store);
    let start = Utc::now() - chrono::Duration::minutes(10);
    let session = PendingSession {
        game_id: "peak".into(),
        target: Some("LocalVanilla".into()),
        process_id: u32::MAX,
        process_start_time_ticks: 1,
        session_start_utc: start,
        modded_dir: None,
        process_creation_file_time: Some(1),
        last_observed_utc: Some(start + chrono::Duration::seconds(183)),
    };
    write_json(
        &paths.data_dir.join("playtime.sessions.json"),
        &BTreeMap::from([(session.process_id.to_string(), session)]),
    )
    .unwrap();
    assert!(store.reconcile().unwrap().is_empty());
    assert!(store.reconcile().unwrap().is_empty());
    assert_eq!(store.totals("peak").unwrap().total_seconds, 183);
    assert_eq!(
        completion.try_recv().unwrap(),
        CompletedSession {
            game_id: "peak".into(),
            duration_ms: 183_000
        }
    );
    assert!(completion.try_recv().is_err());
}

#[test]
fn broken_legacy_session_with_a_loader_obligation_blocks_launch() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    fs::create_dir_all(&paths.data_dir).unwrap();
    fs::write(
        paths.data_dir.join("playtime.sessions.json"),
        r#"{"7":{"ProcessId":"invalid","ModdedDir":"game"}}"#,
    )
    .unwrap();
    let store = PlaytimeStore::new(&paths);
    assert!(
        store
            .reconcile_for_launch(LaunchTarget::LocalVanilla)
            .is_err()
    );
}

#[test]
fn oversized_statistics_are_optional_and_oversized_commit_still_requires_recovery() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    fs::create_dir_all(&paths.data_dir).unwrap();
    fs::File::create(paths.data_dir.join("playtime.sessions.json"))
        .unwrap()
        .set_len(MAX_STATISTICS_BYTES + 1)
        .unwrap();
    let store = PlaytimeStore::new(&paths);
    let warning = store
        .reconcile_for_launch(LaunchTarget::LocalVanilla)
        .unwrap()
        .unwrap();
    assert!(warning.contains("слишком большой"), "{warning}");
    fs::File::create(paths.data_dir.join("playtime.commit.json"))
        .unwrap()
        .set_len(MAX_COMMIT_BYTES + 1)
        .unwrap();
    let error = store
        .reconcile_for_launch(LaunchTarget::LocalVanilla)
        .unwrap_err();
    assert!(error.contains("слишком большой"), "{error}");
}

#[test]
fn stale_plain_sessions_report_statistics_errors_without_blocking_either_mode() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let store = PlaytimeStore::new(&paths);
    let id = u32::MAX;
    let start = Utc::now() - chrono::Duration::seconds(60);
    let end = start + chrono::Duration::seconds(38);
    let session = PendingSession {
        game_id: "peak".into(),
        target: Some("LocalVanilla".into()),
        process_id: id,
        process_start_time_ticks: 1,
        session_start_utc: start,
        modded_dir: None,
        process_creation_file_time: Some(1),
        last_observed_utc: Some(end),
    };
    write_json(
        &paths.data_dir.join("playtime.sessions.json"),
        &BTreeMap::from([(id.to_string(), session)]),
    )
    .unwrap();
    let statistics = paths.data_dir.join("playtime.json");
    fs::create_dir(&statistics).unwrap();
    for target in [LaunchTarget::LocalVanilla, LaunchTarget::LocalModded] {
        assert!(store.reconcile_for_launch(target).unwrap().is_some());
        assert!(store.pending().unwrap().contains_key(&id.to_string()));
    }
    assert!(store.reconcile().is_err());
    fs::remove_dir(statistics).unwrap();
    store.reconcile().unwrap();
    assert!(store.pending().unwrap().is_empty());
    let totals = store.totals("PEAK").unwrap();
    assert_eq!(totals.total_seconds, 38);
    assert_eq!(totals.last_session_seconds, 38);
    assert_eq!(totals.last_session_at, Some(end));
}

#[test]
fn stale_legacy_loader_obligation_is_recovered_before_optional_statistics() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let root = paths.game_root("peak").unwrap();
    fs::create_dir_all(&root).unwrap();
    let doorstop = root.join("doorstop_config.ini");
    fs::write(&doorstop, "other=true").unwrap();
    let start = Utc::now() - chrono::Duration::seconds(60);
    let session = |id: u32, modded_dir| PendingSession {
        game_id: "peak".into(),
        target: None,
        process_id: id,
        process_start_time_ticks: 1,
        session_start_utc: start,
        modded_dir,
        process_creation_file_time: None,
        last_observed_utc: Some(start + chrono::Duration::seconds(20)),
    };
    let plain_pid = u32::MAX - 1;
    let modded_pid = u32::MAX;
    write_json(
        &paths.data_dir.join("playtime.sessions.json"),
        &BTreeMap::from([
            (plain_pid.to_string(), session(plain_pid, None)),
            (
                modded_pid.to_string(),
                session(modded_pid, Some(root.to_string_lossy().into_owned())),
            ),
        ]),
    )
    .unwrap();
    let store = PlaytimeStore::new(&paths);
    assert!(
        store
            .reconcile_for_launch(LaunchTarget::LocalVanilla)
            .is_err()
    );
    let journal_path = paths.data_dir.join("playtime.commit.json");
    let journal: serde_json::Value =
        serde_json::from_slice(&fs::read(&journal_path).unwrap()).unwrap();
    assert_eq!(journal["Playtime"]["peak"]["TotalSeconds"], 20);
    assert!(journal["Pending"].get(plain_pid.to_string()).is_some());
    assert!(journal["Pending"].get(modded_pid.to_string()).is_none());
    assert!(store.recover_commit().is_err());

    fs::write(&doorstop, "enabled=true").unwrap();
    store.reconcile().unwrap();
    assert!(!journal_path.exists());
    assert!(store.pending().unwrap().is_empty());
    assert_eq!(store.totals("peak").unwrap().total_seconds, 40);
    assert_eq!(fs::read_to_string(doorstop).unwrap(), "enabled=false");
}

#[cfg(windows)]
#[test]
fn reconciled_live_process_has_one_watcher_and_updates_its_heartbeat() {
    use std::process::{Child, Command, Stdio};
    use std::time::Instant;

    struct StopProcess(Child);
    impl Drop for StopProcess {
        fn drop(&mut self) {
            let _ = self.0.kill();
            let _ = self.0.wait();
        }
    }
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let ping = PathBuf::from(std::env::var_os("SystemRoot").unwrap()).join("System32/ping.exe");
    let mut child = StopProcess(
        Command::new(ping)
            .args(["-t", "127.0.0.1"])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .spawn()
            .unwrap(),
    );
    let process = ProcessIdentity::capture(child.0.id()).unwrap();
    let key = process.pid.to_string();
    let start = Utc::now();
    let mut pending = BTreeMap::from([(
        key.clone(),
        PendingSession {
            game_id: "peak".into(),
            target: Some("LocalVanilla".into()),
            process_id: process.pid,
            process_start_time_ticks: process.legacy_start_ticks,
            session_start_utc: start,
            modded_dir: None,
            process_creation_file_time: Some(process.creation_file_time + 1),
            last_observed_utc: Some(start),
        },
    )]);
    let path = paths.data_dir.join("playtime.sessions.json");
    write_json(&path, &pending).unwrap();
    let store = PlaytimeStore::new(&paths);
    assert!(
        store
            .begin("peak", LaunchTarget::LocalVanilla, process.clone(), None)
            .is_err()
    );
    assert_eq!(
        store.pending().unwrap()[&key].process_creation_file_time,
        Some(process.creation_file_time + 1)
    );

    pending.get_mut(&key).unwrap().process_creation_file_time = None;
    write_json(&path, &pending).unwrap();
    let resumed = store.reconcile().unwrap();
    assert_eq!(resumed.len(), 1);
    assert_eq!(resumed[0].process, process);
    store
        .begin("peak", LaunchTarget::LocalVanilla, process.clone(), None)
        .unwrap();
    assert_eq!(store.watching.lock().unwrap().len(), 1);

    let deadline = Instant::now() + Duration::from_secs(30);
    while store.pending().unwrap()[&key].last_observed_utc == Some(start) {
        assert!(
            Instant::now() < deadline,
            "resumed session has no heartbeat"
        );
        std::thread::sleep(Duration::from_millis(100));
    }
    child.0.kill().unwrap();
    child.0.wait().unwrap();
    let deadline = Instant::now() + Duration::from_secs(12);
    while !store.watching.lock().unwrap().is_empty() {
        assert!(
            Instant::now() < deadline,
            "resumed session was not finished"
        );
        std::thread::sleep(Duration::from_millis(60));
    }
    assert!(store.pending().unwrap().is_empty());
    assert!(store.totals("peak").unwrap().total_seconds >= 15);
}
