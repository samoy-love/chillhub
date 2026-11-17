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
fn waits_for_both_parts_across_pause_and_reordering_then_consumes_once() {
    let mut pending = Pending {
        jobs: vec![job()],
        scheduled: false,
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
            scheduled: false,
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
        scheduled: false,
    };
    assert!(pending.take_ready(&[]).is_none());
    assert!(pending.jobs.is_empty());
}

#[test]
fn terminal_failure_revokes_launch_even_when_snapshot_has_already_advanced() {
    let mut pending = Pending {
        jobs: vec![job()],
        scheduled: false,
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
