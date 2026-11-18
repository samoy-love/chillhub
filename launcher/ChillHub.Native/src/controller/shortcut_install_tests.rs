use super::*;
fn job() -> Job {
    Job {
        game: GameRecord {
            game_id: "chosen".into(),
            ..Default::default()
        },
        target: LaunchTarget::LocalModded,
        root: String::new(),
        tasks: vec!["base".into(), "mods".into()],
        completed: Default::default(),
        _reservation: LaunchReservations::default().hold("chosen".into()),
    }
}
fn task(id: &str, state: QueueState) -> QueueItem {
    QueueItem {
        id: id.into(),
        state,
        request: Default::default(),
        progress: None,
        outcome: None,
        error: None,
        cleanup_files: vec![],
        report: Default::default(),
    }
}
#[test]
fn launch_reservations_follow_independent_requests_for_the_same_game() {
    let reservations = LaunchReservations::default();
    let first = reservations.hold("chosen".into());
    let second = reservations.hold("chosen".into());
    assert!(reservations.contains("chosen"));
    assert!(!reservations.contains("unrelated"));
    drop(first);
    assert!(reservations.contains("chosen"));
    drop(second);
    assert!(!reservations.contains("chosen"));
}

#[test]
fn waits_for_both_parts_across_pause_and_reordering_then_consumes_once() {
    let mut pending = Pending {
        jobs: vec![job()],
        ..Default::default()
    };
    assert!(
        pending
            .take_ready(&[
                task("mods", QueueState::Paused),
                task("base", QueueState::Completed)
            ])
            .is_none()
    );
    assert_eq!(pending.jobs.len(), 1);
    let complete = [
        task("unrelated", QueueState::Running),
        task("mods", QueueState::Completed),
        task("base", QueueState::Completed),
    ];
    pending.observe_finished(&complete[1]);
    assert!(pending.take_ready(&complete).is_none());
    pending.observe_finished(&complete[2]);
    let ready = pending.take_ready(&complete).unwrap();
    assert_eq!(ready.game.game_id, "chosen");
    assert_eq!(ready.target, LaunchTarget::LocalModded);
    assert!(pending.take_ready(&complete).is_none());
}
#[test]
fn cancelled_failed_or_deleted_part_revokes_launch_even_after_retry() {
    for state in [
        QueueState::Failed,
        QueueState::Cancelled,
        QueueState::Cancelling,
    ] {
        let mut pending = Pending {
            jobs: vec![job()],
            ..Default::default()
        };
        assert!(
            pending
                .take_ready(&[task("base", QueueState::Completed), task("mods", state)])
                .is_none()
        );
        assert!(
            pending
                .take_ready(&[
                    task("base", QueueState::Completed),
                    task("mods", QueueState::Completed)
                ])
                .is_none()
        );
        assert!(pending.jobs.is_empty());
    }
    let mut pending = Pending {
        jobs: vec![job()],
        ..Default::default()
    };
    assert!(pending.take_ready(&[]).is_none());
    assert!(pending.jobs.is_empty());
}

#[test]
fn terminal_failure_revokes_launch_even_when_snapshot_has_already_advanced() {
    let mut pending = Pending {
        jobs: vec![job()],
        ..Default::default()
    };
    pending.observe_finished(&task("base", QueueState::Completed));
    pending.observe_finished(&task("mods", QueueState::Failed));
    pending.observe_finished(&task("mods", QueueState::Completed));
    assert!(
        pending
            .take_ready(&[
                task("base", QueueState::Completed),
                task("mods", QueueState::Completed),
            ])
            .is_none()
    );
    assert!(pending.jobs.is_empty());
}

#[test]
fn stale_shortcut_to_a_ready_relocated_copy_does_not_offer_installation() {
    assert!(!should_offer_install(false, &LaunchAction::Play));
    assert!(!should_offer_install(false, &LaunchAction::Unavailable));
    assert!(!should_offer_install(true, &LaunchAction::InstallGame));
    assert!(should_offer_install(false, &LaunchAction::InstallGame));
}

#[test]
fn completed_shortcut_remains_bound_to_the_published_installation_root() {
    let temp = tempfile::tempdir().unwrap();
    let original = temp.path().join("original");
    let changed = temp.path().join("changed");
    std::fs::create_dir(&original).unwrap();
    std::fs::create_dir(&changed).unwrap();
    let mut job = job();
    job.root = chillhub_native::paths::path_identity(&original).unwrap();
    assert!(job.matches_installation(&original));
    assert!(!job.matches_installation(&changed));
}

pub(crate) fn check_install_from_shortcut(ui: &AppWindow, state: &State) {
    use super::super::{downloads, shortcuts};
    use std::fs;

    let services = state.borrow().services.clone();
    let root = services.paths.game_root("fixture").unwrap();
    let downloaded_tasks = || {
        services
            .queue
            .snapshot()
            .into_iter()
            .filter(|task| task.request.root == root)
            .collect::<Vec<_>>()
    };
    // The recovery fixture swapped in an isolated queue. Attach that queue to its normal UI consumer.
    downloads::subscribe(ui, state);
    let open_missing_shortcut = || {
        shortcuts::external_request(
            ui,
            vec![
                "--game=fixture".into(),
                format!("--exe={}", root.join("Game.exe").display()),
                "--target=LocalVanilla".into(),
            ],
        );
        wait_for(ui, "the shortcut install offer", |ui| {
            ui.get_panel_kind() == "confirm"
        });
        assert!(ui.get_panel_title().contains("не найдена на диске"));
        assert!(matches!(
            state.borrow().pending.as_ref(),
            Some(Confirmation::InstallShortcut(game, LaunchTarget::LocalVanilla))
                if game.game_id == "fixture"
        ));
        assert!(downloaded_tasks().is_empty());
    };

    open_missing_shortcut();
    ui.invoke_action_requested("cancel-action".into());
    ui.invoke_action_requested("confirm-action".into());
    assert!(state.borrow().pending.is_none());
    assert!(downloaded_tasks().is_empty());
    assert!(!root.exists());

    open_missing_shortcut();
    let (game, _) = selected(state).unwrap();
    let config = services.config.load_readonly().unwrap();
    let version = config
        .selected_version(&game.game_id)
        .unwrap_or(&game.latest_version);
    // The executable appears after the prompt. Its contents cannot launch a process.
    fs::create_dir_all(&root).unwrap();
    fs::write(root.join("Game.exe"), b"local copy appeared").unwrap();
    fs::write(root.join(".version"), version).unwrap();
    ui.invoke_action_requested("confirm-action".into());
    wait_for(ui, "the already-installed notice", |ui| {
        !ui.get_operation_busy() && ui.get_toast().contains("Копия уже найдена")
    });
    assert!(downloaded_tasks().is_empty());
    assert_eq!(
        fs::read(root.join("Game.exe")).unwrap(),
        b"local copy appeared"
    );

    fs::remove_file(root.join("Game.exe")).unwrap();
    fs::remove_file(root.join(".version")).unwrap();
    open_missing_shortcut();
    ui.set_toast(Default::default());
    ui.invoke_action_requested("confirm-action".into());
    assert!(ui.get_operation_busy());
    assert!(state.borrow().pending.is_none());
    let finished_state = state.clone();
    let install_root = root.clone();
    wait_for(
        ui,
        "the shortcut installation and launch recheck",
        move |ui| {
            !ui.get_operation_busy()
                && finished_state.borrow().shortcut_installs.jobs.is_empty()
                && finished_state
                    .borrow()
                    .services
                    .queue
                    .snapshot()
                    .iter()
                    .any(|task| {
                        task.request.root == install_root
                            && matches!(
                                task.state,
                                QueueState::Completed
                                    | QueueState::Failed
                                    | QueueState::Cancelled
                                    | QueueState::NeedsConfirmation
                            )
                    })
        },
    );
    assert_eq!(ui.get_panel_kind(), "downloads");
    let tasks = downloaded_tasks();
    assert_eq!(tasks.len(), 1);
    assert_eq!(tasks[0].state, QueueState::Completed, "{tasks:?}");
    assert_eq!(ui.get_toast(), "fixture blocked launch");
    assert_eq!(
        fs::read(root.join("Game.exe")).unwrap(),
        b"controller fixture; this file cannot execute"
    );
    assert!(state.borrow().shortcut_installs.jobs.is_empty());
    assert!(services.playtime.pending().unwrap().is_empty());
}

fn wait_for(ui: &AppWindow, expected: &'static str, check: impl Fn(&AppWindow) -> bool + 'static) {
    use std::{cell::Cell, time::Instant};

    let tick = Timer::default();
    let deadline = Instant::now() + Duration::from_secs(5);
    let weak = ui.as_weak();
    let observed = Rc::new(Cell::new(None));
    let result = observed.clone();
    tick.start(
        slint::TimerMode::Repeated,
        Duration::from_millis(20),
        move || {
            if result.get().is_some() {
                return;
            }
            let ui = weak.upgrade().unwrap();
            let matched = check(&ui);
            if matched || Instant::now() >= deadline {
                result.set(Some(matched));
                let _ = slint::quit_event_loop();
            }
        },
    );
    slint::run_event_loop_until_quit().unwrap();
    tick.stop();
    assert!(
        observed.get() == Some(true),
        "waiting for {expected}: panel={}, busy={}, toast={}",
        ui.get_panel_kind(),
        ui.get_operation_busy(),
        ui.get_toast()
    );
}

#[cfg(windows)]
pub(crate) fn check_auto_launch(ui: &AppWindow, state: &State) {
    use super::super::shortcuts;
    use std::{fs, path::PathBuf};

    struct StopProbe(PathBuf);
    impl Drop for StopProbe {
        fn drop(&mut self) {
            let _ = fs::write(&self.0, []);
        }
    }

    let services = state.borrow().services.clone();
    let root = services.paths.game_root("plain").unwrap();
    let started = root.join("started.txt");
    let stop = StopProbe(root.join("stop"));
    ui.invoke_panel_closed();
    shortcuts::external_request(
        ui,
        vec![
            "--game=plain".into(),
            format!("--exe={}", root.join("Plain.exe").display()),
            "--target=LocalVanilla".into(),
        ],
    );
    wait_for(ui, "the launch probe install offer", |ui| {
        ui.get_panel_kind() == "confirm"
    });
    assert!(matches!(
        state.borrow().pending.as_ref(),
        Some(Confirmation::InstallShortcut(game, LaunchTarget::LocalVanilla))
            if game.game_id == "plain"
    ));
    ui.set_toast(Default::default());
    ui.invoke_action_requested("confirm-action".into());
    assert!(ui.get_operation_busy());

    let marker = started.clone();
    wait_for(
        ui,
        "a real process after installing from a shortcut",
        move |ui| !ui.get_operation_busy() && marker.is_file(),
    );
    assert_eq!(
        fs::canonicalize(fs::read_to_string(started).unwrap()).unwrap(),
        fs::canonicalize(&root).unwrap()
    );
    assert!(
        services
            .queue
            .snapshot()
            .iter()
            .any(|task| { task.request.root == root && task.state == QueueState::Completed })
    );
    assert!(!state.borrow().shortcut_installs.launching.contains("plain"));
    drop(stop);
    let playtime = services.playtime.clone();
    wait_for(ui, "the launch probe to stop", move |_| {
        playtime.pending().is_ok_and(|pending| pending.is_empty())
    });
}
