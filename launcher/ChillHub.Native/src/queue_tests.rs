use super::*;
use crate::transfer::TransferStage;

#[test]
fn explicit_queue_reset_archives_corrupt_metadata_and_preserves_downloads() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("downloads.json");
    fs::write(&path, b"{unreadable queue").unwrap();
    fs::write(temp.path().join("game.exe.part"), b"downloaded bytes").unwrap();
    let queue = DownloadQueue::new(&path).unwrap();
    assert!(queue.recovery_error().is_some());
    queue.reset_unreadable().unwrap();
    assert!(queue.recovery_error().is_none());
    assert!(read_items(&path).unwrap().is_empty());
    assert_eq!(
        fs::read(temp.path().join("game.exe.part")).unwrap(),
        b"downloaded bytes"
    );
    queue.reset_unreadable().unwrap();
    let backups: Vec<_> = fs::read_dir(temp.path())
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| {
            path.file_name()
                .unwrap()
                .to_string_lossy()
                .starts_with("downloads-unreadable-")
        })
        .collect();
    assert_eq!(backups.len(), 1);
    assert_eq!(fs::read(&backups[0]).unwrap(), b"{unreadable queue");
    queue.shutdown();
}

#[test]
fn queue_reset_does_not_move_an_unexpected_directory() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("downloads.json");
    fs::create_dir(&path).unwrap();
    fs::write(path.join("keep"), b"original").unwrap();
    let queue = DownloadQueue::new(&path).unwrap();
    assert!(queue.recovery_error().is_some());
    assert!(queue.reset_unreadable().is_err());
    assert!(queue.recovery_error().is_some());
    assert_eq!(fs::read(path.join("keep")).unwrap(), b"original");
    queue.shutdown();
}

#[test]
fn queue_reset_adopts_a_repaired_file_instead_of_erasing_it() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("downloads.json");
    fs::write(&path, b"{broken").unwrap();
    let queue = DownloadQueue::new(&path).unwrap();
    let mut task = item("recovered", QueueState::Waiting);
    task.request.approved_cleanup_paths = vec!["save.dat".into()];
    fs::write(&path, serde_json::to_vec(&vec![task]).unwrap()).unwrap();
    queue.reset_unreadable().unwrap();
    assert_eq!(queue.snapshot()[0].id, "recovered");
    assert_eq!(queue.snapshot()[0].state, QueueState::Paused);
    assert!(
        queue.snapshot()[0]
            .request
            .approved_cleanup_paths
            .is_empty()
    );
    assert!(queue.recovery_error().is_none());
    queue.shutdown();
    assert_eq!(read_items(&path).unwrap()[0].id, "recovered");
}

#[test]
fn resumed_or_restored_tasks_require_fresh_cleanup_consent() {
    let temp = tempfile::tempdir().unwrap();
    for initial in [
        QueueState::Paused,
        QueueState::Failed,
        QueueState::Cancelled,
    ] {
        let mut task = item("task", initial);
        task.request.approved_cleanup_paths = vec!["save.dat".into()];
        let queue = DownloadQueue {
            shared: Arc::new(Shared {
                state: Mutex::new(state(vec![task])),
                wake: Condvar::new(),
                path: temp.path().join("queue.json"),
            }),
            worker: Mutex::new(None),
        };
        queue.resume("task").unwrap();
        assert!(
            queue.snapshot()[0]
                .request
                .approved_cleanup_paths
                .is_empty()
        );
        queue.shutdown();
    }
    let mut task = item("restored", QueueState::Running);
    task.request.approved_cleanup_paths = vec!["save.dat".into()];
    let path = temp.path().join("restore.json");
    fs::write(&path, serde_json::to_vec(&vec![task]).unwrap()).unwrap();
    let queue = DownloadQueue::new(path).unwrap();
    assert!(
        queue.snapshot()[0]
            .request
            .approved_cleanup_paths
            .is_empty()
    );
    queue.shutdown();
}

fn unwritable_queue(state: State, directory: &Path) -> DownloadQueue {
    // A file cannot be used as the parent directory of the persisted queue.
    let parent = directory.join("blocked");
    fs::write(&parent, b"keep").unwrap();
    DownloadQueue {
        shared: Arc::new(Shared {
            state: Mutex::new(state),
            wake: Condvar::new(),
            path: parent.join("queue.json"),
        }),
        worker: Mutex::new(None),
    }
}

#[test]
fn failed_queue_write_cannot_authorize_cleanup_or_resume_work() {
    for initial in [QueueState::NeedsConfirmation, QueueState::Paused] {
        let temp = tempfile::tempdir().unwrap();
        let mut task = item("task", initial);
        task.cleanup_paths = vec!["save.dat".into()];
        let queue = unwritable_queue(state(vec![task]), temp.path());
        let result = if initial == QueueState::NeedsConfirmation {
            queue.confirm_cleanup("task")
        } else {
            queue.resume("task")
        };
        assert!(result.is_err());
        let snapshot = queue.snapshot();
        assert_eq!(snapshot[0].state, initial);
        assert!(snapshot[0].request.approved_cleanup_paths.is_empty());
    }
}

#[test]
fn failed_reorder_does_not_interrupt_active_work_or_change_order() {
    let temp = tempfile::tempdir().unwrap();
    let control = TransferControl::default();
    let mut current = state(vec![
        item("active", QueueState::Running),
        item("waiting", QueueState::Waiting),
    ]);
    current.active = Some(Active {
        id: "active".into(),
        control: control.clone(),
        requeue: false,
        last_progress: Instant::now(),
    });
    let queue = unwritable_queue(current, temp.path());
    assert!(queue.move_before("waiting", Some("active")).is_err());
    assert!(!control.is_cancelled());
    let current = queue.shared.state.lock().unwrap();
    assert_eq!(current.items[0].id, "active");
    assert_eq!(current.items[0].state, QueueState::Running);
    assert!(!current.active.as_ref().unwrap().requeue);
}

#[test]
fn failed_active_pause_or_cancel_keeps_the_reported_state_unchanged() {
    for cancel in [false, true] {
        let temp = tempfile::tempdir().unwrap();
        let control = TransferControl::default();
        let mut current = state(vec![item("active", QueueState::Running)]);
        current.active = Some(Active {
            id: "active".into(),
            control: control.clone(),
            requeue: false,
            last_progress: Instant::now(),
        });
        let queue = unwritable_queue(current, temp.path());
        assert!(
            if cancel {
                queue.cancel("active")
            } else {
                queue.pause("active")
            }
            .is_err()
        );
        assert_eq!(queue.snapshot()[0].state, QueueState::Running);
        assert!(!control.is_cancelled());
    }
}

#[test]
fn moving_another_job_never_requeues_cancelled_or_user_paused_work() {
    for stopping in [
        QueueState::Cancelling,
        QueueState::Pausing,
        QueueState::Running,
    ] {
        let temp = tempfile::tempdir().unwrap();
        let mut state = state(vec![
            item("active", stopping),
            item("waiting", QueueState::Waiting),
        ]);
        state.active = Some(Active {
            id: "active".into(),
            control: TransferControl::default(),
            requeue: false,
            last_progress: Instant::now(),
        });
        let queue = DownloadQueue {
            shared: Arc::new(Shared {
                state: Mutex::new(state),
                wake: Condvar::new(),
                path: temp.path().join("queue.json"),
            }),
            worker: Mutex::new(None),
        };
        queue.move_before("waiting", Some("active")).unwrap();
        let state = queue.shared.state.lock().unwrap();
        assert_eq!(state.items[0].id, "waiting");
        assert_eq!(
            state.active.as_ref().unwrap().requeue,
            stopping == QueueState::Running
        );
        assert_eq!(
            state.items[1].state,
            if stopping == QueueState::Running {
                QueueState::Pausing
            } else {
                stopping
            }
        );
    }
}

#[test]
fn queue_reporting_classifies_expected_outages_without_duplicate_mod_installs() {
    let mut report = QueueReport {
        operation: Some(TransferOperation::Install),
        ..Default::default()
    };
    assert_eq!(
        report.metric_event(SyncScope::WholeRoot),
        Some("game_install")
    );
    assert_eq!(report.metric_event(SyncScope::OwnFilesOnly), None);
    report.failure(&TransferError::Network("offline".into()));
    assert!(report.transient_error);
    assert_eq!(report.error_code.as_deref(), Some("network"));
    report.failure(&TransferError::Integrity("game.dll".into()));
    assert!(!report.transient_error);
    assert_eq!(report.error_code.as_deref(), Some("hash_mismatch"));
    report.failure(&TransferError::AntivirusBlocked("mod.dll".into()));
    assert!(report.transient_error);
    assert_eq!(report.error_code.as_deref(), Some("sync_security_blocked"));
    report.failure(&TransferError::Busy);
    assert_eq!(report.metric_event(SyncScope::WholeRoot), None);
    report.operation = Some(TransferOperation::Verify);
    assert_eq!(
        report.metric_event(SyncScope::OwnFilesOnly),
        Some("integrity_check")
    );
}

fn item(id: &str, state: QueueState) -> QueueItem {
    QueueItem {
        id: id.into(),
        state,
        request: TransferRequest {
            game_id: "test".into(),
            version: "1".into(),
            manifest_url: "file:///never-requested".into(),
            previous_owned_paths: vec!["BepInEx/plugins/owned.dll".into()],
            foreign_paths: vec!["Game_Data/shared.dat".into()],
            preserve_paths: vec!["Saves".into()],
            donor_roots: vec![PathBuf::from("donor")],
            ..Default::default()
        },
        progress: Some(TransferEvent {
            stage: TransferStage::Completed,
            current_file: "last.dll".into(),
            completed_bytes: 7,
            total_bytes: 7,
            network_bytes: 7,
            reused_bytes: 0,
            completed_files: 1,
            total_files: 1,
        }),
        outcome: Some(TransferOutcome {
            network_bytes: 7,
            ..Default::default()
        }),
        error: None,
        cleanup_paths: vec!["old.dll".into()],
        report: QueueReport::default(),
    }
}
fn state(items: Vec<QueueItem>) -> State {
    State {
        items,
        active: None,
        subscribers: vec![],
        stopping: false,
        logged_states: Default::default(),
        recovery_error: None,
        speed_limit: None,
    }
}

#[test]
fn completed_history_is_bounded_without_losing_retry_data_or_dependencies() {
    let temp = tempfile::tempdir().unwrap();
    let mut items = (0..57)
        .map(|i| {
            let mut completed = item(&format!("done-{i}"), QueueState::Completed);
            completed.request.approved_cleanup_paths = vec!["old.dll".into()];
            completed
        })
        .collect::<Vec<_>>();
    items.push(item("failed", QueueState::Failed));
    items.push(item("cancelled", QueueState::Cancelled));
    let mut dependent = item("waiting-for-done", QueueState::Paused);
    dependent.request.depends_on = Some("done-0".into());
    items.push(dependent);
    let mut retry = item("waiting-for-failed", QueueState::Paused);
    retry.request.depends_on = Some("failed".into());
    items.push(retry);
    let mut state = state(items);
    persist(&temp.path().join("queue.json"), &mut state).unwrap();
    assert_eq!(state.items.len(), 54);
    assert_eq!(state.items[0].id, "done-7");
    for item in state
        .items
        .iter()
        .filter(|i| i.state == QueueState::Completed)
    {
        assert!(item.request.approved_cleanup_paths.is_empty());
        assert!(item.request.previous_owned_paths.is_empty());
        assert_eq!(item.request.foreign_paths.capacity(), 0);
        assert!(item.request.preserve_paths.is_empty() && item.request.donor_roots.is_empty());
        assert!(item.progress.is_none() && item.cleanup_paths.is_empty());
        assert_eq!(item.outcome.as_ref().unwrap().network_bytes, 7);
    }
    for id in [
        "failed",
        "cancelled",
        "waiting-for-done",
        "waiting-for-failed",
    ] {
        let item = state.items.iter().find(|i| i.id == id).unwrap();
        assert_eq!(
            item.request.previous_owned_paths,
            ["BepInEx/plugins/owned.dll"]
        );
        assert!(!item.request.foreign_paths.is_empty() && !item.request.preserve_paths.is_empty());
        assert!(!item.request.donor_roots.is_empty() && !item.cleanup_paths.is_empty());
    }
    assert!(
        state
            .items
            .iter()
            .find(|i| i.id == "waiting-for-done")
            .unwrap()
            .request
            .depends_on
            .is_none()
    );
    assert_eq!(
        state
            .items
            .iter()
            .find(|i| i.id == "waiting-for-failed")
            .unwrap()
            .request
            .depends_on
            .as_deref(),
        Some("failed")
    );
    assert!(fs::metadata(temp.path().join("queue.json")).unwrap().len() <= MAX_QUEUE_BYTES as u64);
}

#[test]
fn oversized_existing_queue_preserves_original_and_keeps_startup_available() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("queue.json");
    fs::write(&path, b"original-queue-preserved").unwrap();
    fs::OpenOptions::new()
        .write(true)
        .open(&path)
        .unwrap()
        .set_len(MAX_QUEUE_BYTES as u64 + 1)
        .unwrap();
    assert!(read_items(&path).is_err());
    let queue = DownloadQueue::new(&path).unwrap();
    assert!(queue.recovery_error().is_some());
    assert!(queue.snapshot().is_empty());
    let events = queue.subscribe();
    assert!(matches!(events.recv().unwrap(), QueueEvent::Changed(_)));
    assert!(matches!(
        events.recv().unwrap(),
        QueueEvent::PersistenceError(_)
    ));
    assert!(
        queue
            .enqueue(TransferRequest::default())
            .unwrap_err()
            .to_string()
            .contains("очередь")
    );
    queue.shutdown();
    drop(queue);
    assert_eq!(
        fs::metadata(&path).unwrap().len(),
        MAX_QUEUE_BYTES as u64 + 1
    );
    let mut prefix = [0; b"original-queue-preserved".len()];
    fs::File::open(&path)
        .unwrap()
        .read_exact(&mut prefix)
        .unwrap();
    assert_eq!(&prefix, b"original-queue-preserved");
    assert!(
        fs::read_to_string(temp.path().join("logs/launcher.log"))
            .unwrap()
            .contains("download.recovery")
    );
}

#[cfg(windows)]
#[test]
fn restored_queue_matches_short_and_canonical_ancestors_before_game_exists() {
    let temp = tempfile::tempdir().unwrap();
    let library = temp.path().join("Long library directory for queued games");
    fs::create_dir_all(&library).unwrap();
    let short = crate::paths::short_test_path(&library);
    let canonical = fs::canonicalize(&library).unwrap();
    let path = temp.path().join("queue.json");
    let mut paused = item("paused", QueueState::Paused);
    paused.request.root = short.join("not-installed-yet");
    fs::write(&path, serde_json::to_vec(&vec![paused.clone()]).unwrap()).unwrap();
    let queue = DownloadQueue::new(&path).unwrap();
    for alias in [&library, &canonical] {
        let mut request = paused.request.clone();
        request.root = alias.join("not-installed-yet");
        assert!(queue.has_pending_for_root(&request.root));
        assert_eq!(queue.enqueue(request.clone()).unwrap(), "paused");
        request.version = "another-version".into();
        assert!(matches!(queue.enqueue(request), Err(TransferError::Busy)));
    }
    assert!(!queue.has_pending_for_root(&library.join("different-game")));
    assert_eq!(queue.snapshot().len(), 1);
    assert_eq!(queue.snapshot()[0].state, QueueState::Paused);
    assert!(!library.join("not-installed-yet").exists());
    queue.shutdown();
}

#[test]
fn unresolved_queue_identities_never_match_other_unknown_paths() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("queue.json");
    let mut paused = item("paused", QueueState::Paused);
    paused.request.root = PathBuf::from("unknown-one");
    fs::write(&path, serde_json::to_vec(&vec![paused]).unwrap()).unwrap();
    let queue = DownloadQueue::new(&path).unwrap();
    assert!(queue.has_pending_for_root(Path::new("unknown-one")));
    assert!(!queue.has_pending_for_root(Path::new("unknown-two")));
    queue.shutdown();
}

#[test]
fn oversized_enqueue_rolls_back_and_failed_job_remains_retryable() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("queue.json");
    let mut failed = item("retry", QueueState::Failed);
    failed.request.root = temp.path().join("game");
    fs::write(&path, serde_json::to_vec(&vec![failed]).unwrap()).unwrap();
    let queue = DownloadQueue::new(&path).unwrap();
    let before = fs::read(&path).unwrap();
    assert!(
        queue
            .enqueue(TransferRequest {
                version: "x".repeat(MAX_QUEUE_BYTES),
                ..Default::default()
            })
            .is_err()
    );
    assert_eq!(fs::read(&path).unwrap(), before);
    assert_eq!(queue.snapshot().len(), 1);
    let events = queue.subscribe();
    queue.resume("retry").unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        assert!(Instant::now() < deadline, "retry did not finish");
        if let Ok(QueueEvent::Finished(item)) = events.recv_timeout(Duration::from_millis(100)) {
            assert_eq!(item.id, "retry");
            assert_eq!(item.state, QueueState::Failed);
            break;
        }
    }
    let retried = queue.snapshot();
    assert_eq!(
        retried[0].request.previous_owned_paths,
        ["BepInEx/plugins/owned.dll"]
    );
    assert_eq!(retried[0].request.foreign_paths, ["Game_Data/shared.dat"]);
    assert_eq!(retried[0].request.preserve_paths, ["Saves"]);
    assert_eq!(retried[0].request.donor_roots, [PathBuf::from("donor")]);
    queue.shutdown();
}

#[test]
fn finished_event_is_emitted_once_on_transition_and_never_on_restore() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("queue.json");
    fs::write(
        &path,
        serde_json::to_vec(&vec![
            item("done", QueueState::Completed),
            item("paused", QueueState::Paused),
        ])
        .unwrap(),
    )
    .unwrap();
    let queue = DownloadQueue::new(&path).unwrap();
    let events = queue.subscribe();
    assert!(matches!(events.recv().unwrap(), QueueEvent::Changed(_)));
    assert!(events.try_recv().is_err());
    queue.cancel("paused").unwrap();
    queue.cancel("paused").unwrap();
    let completed = events
        .try_iter()
        .filter_map(|event| {
            if let QueueEvent::Finished(item) = event {
                Some(item)
            } else {
                None
            }
        })
        .collect::<Vec<_>>();
    assert_eq!(completed.len(), 1);
    assert_eq!(completed[0].id, "paused");
    assert_eq!(completed[0].state, QueueState::Cancelled);
    assert_eq!(completed[0].progress.as_ref().unwrap().total_files, 1);
    assert!(
        !queue
            .snapshot()
            .iter()
            .find(|i| i.id == "paused")
            .unwrap()
            .request
            .previous_owned_paths
            .is_empty()
    );
    queue.shutdown();
}
