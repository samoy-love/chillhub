use super::*;
#[test]
fn queue_actions_follow_lifecycle_and_position() {
    use QueueState::*;
    for (state, removable, movable) in [
        (Waiting, false, true),
        (Running, false, false),
        (Pausing, false, false),
        (Paused, false, true),
        (Cancelling, false, false),
        (Completed, true, false),
        (Failed, true, false),
        (Cancelled, true, false),
        (NeedsConfirmation, false, false),
    ] {
        assert_eq!(can_remove(state), removable, "remove {state:?}");
        assert_eq!(can_reorder(state), movable, "move {state:?}");
    }
}
use chillhub_native::transfer::{TransferEvent, TransferRequest};
fn task() -> QueueItem {
    QueueItem {
        id: "one".into(),
        request: TransferRequest::default(),
        state: QueueState::Running,
        progress: Some(TransferEvent {
            stage: TransferStage::Checking,
            current_file: "a.bin".into(),
            completed_bytes: 100,
            total_bytes: 1000,
            network_bytes: 0,
            reused_bytes: 100,
            completed_files: 1,
            total_files: 2,
        }),
        outcome: None,
        error: None,
        cleanup_files: vec![],
        report: Default::default(),
    }
}
#[test]
fn checking_and_cancellation_never_pretend_to_download() {
    let mut t = task();
    assert_eq!(state_label(&t), "Проверяем файлы");
    let project = |task: &QueueItem| {
        row(
            task,
            &Speeds::default(),
            Default::default(),
            Image::default(),
        )
    };
    let checking = project(&t);
    assert_eq!(checking.progress_label.as_str(), "50%");
    assert!(!checking.downloading);
    assert!(checking.speed.is_empty());
    t.state = QueueState::Cancelling;
    let cancelling = project(&t);
    assert!(!cancelling.downloading);
    assert!(cancelling.progress_label.is_empty());
    assert!(cancelling.file.is_empty());
    assert!(cancelling.eta.is_empty());
}
#[test]
fn network_rate_resets_after_pause_and_stage_change() {
    let mut t = task();
    t.progress.as_mut().unwrap().stage = TransferStage::Downloading;
    let mut s = Speeds::default();
    let now = Instant::now();
    s.update(&[t.clone()], now);
    t.progress.as_mut().unwrap().network_bytes = 1000;
    s.update(&[t.clone()], now + Duration::from_secs(1));
    assert_eq!(s.rate(&t), 1000.);
    t.progress.as_mut().unwrap().stage = TransferStage::Verifying;
    s.update(&[t.clone()], now + Duration::from_millis(1100));
    assert_eq!(s.rate(&t), 0.);
    t.progress.as_mut().unwrap().stage = TransferStage::Downloading;
    s.update(&[t.clone()], now + Duration::from_millis(1200));
    assert_eq!(s.rate(&t), 1000.);
    t.state = QueueState::Paused;
    s.update(&[t.clone()], now + Duration::from_secs(2));
    assert_eq!(s.rate(&t), 0.);
    t.state = QueueState::Running;
    s.update(&[t.clone()], now + Duration::from_secs(3));
    assert_eq!(s.rate(&t), 0.);
}
#[test]
fn unknown_network_total_is_not_invented() {
    let mut t = task();
    t.progress.as_mut().unwrap().stage = TransferStage::Downloading;
    let row = row(&t, &Speeds::default(), Default::default(), Image::default());
    assert!(row.downloading);
    assert_eq!(row.processed.as_str(), "100 Б");
    assert_eq!(row.processed_total.as_str(), "1000 Б");
    assert_eq!(row.transferred.as_str(), "0 Б");
    assert_eq!(row.speed.as_str(), "—");
    assert_eq!(row.eta.as_str(), "Уточняем…");
}
#[test]
fn local_reuse_eta_does_not_divide_disk_work_by_network_speed() {
    let mut t = task();
    t.progress.as_mut().unwrap().stage = TransferStage::Downloading;
    let mut speeds = Speeds::default();
    let now = Instant::now();
    speeds.update(&[t.clone()], now);
    t.progress.as_mut().unwrap().completed_bytes = 300;
    speeds.update(&[t.clone()], now + Duration::from_secs(1));
    assert!(speeds.eta(&t).is_none());
    t.progress.as_mut().unwrap().completed_bytes = 500;
    speeds.update(&[t.clone()], now + Duration::from_secs(2));
    assert_eq!(speeds.rate(&t), 0.);
    assert_eq!(speeds.eta(&t), Some(3));
    t.state = QueueState::Paused;
    assert!(speeds.eta(&t).is_none());
}

#[test]
fn queue_groups_pending_history_and_current_stage_without_duplicates() {
    use QueueState::*;
    let item = |id: &str, state| QueueItem {
        id: id.into(),
        state,
        ..task()
    };
    let get_rows = |tasks: &[QueueItem]| {
        rows(tasks, &Speeds::default(), |_| {
            (Default::default(), Image::default())
        })
    };
    let mut tasks = [
        item("failed", Failed),
        item("checking", Running),
        item("waiting", Waiting),
        item("paused", Paused),
        item("confirmation", NeedsConfirmation),
    ];
    let (active, pending, history) = get_rows(&tasks);
    assert_eq!(active.id.as_str(), "checking");
    assert_eq!(
        pending
            .iter()
            .map(|row| row.id.as_str())
            .collect::<Vec<_>>(),
        ["waiting", "paused", "confirmation"]
    );
    assert!(pending[0].can_move_up && pending[0].can_move_down);
    assert!(pending[1].can_resume);
    assert!(pending[2].can_confirm);
    assert_eq!(history[0].id.as_str(), "failed");
    assert!(history[0].can_resume && history[0].can_remove);
    tasks[1].state = Cancelling;
    assert_eq!(get_rows(&tasks).0.id.as_str(), "checking");
    tasks[1].state = Cancelled;
    assert_eq!(get_rows(&tasks).0.id.as_str(), "confirmation");
    assert!(get_rows(&tasks[..4]).0.id.is_empty());
    assert_eq!(get_rows(&tasks[3..4]).0.id.as_str(), "paused");
    let (_, pending, _) = get_rows(&[item("failed", Failed), item("waiting", Waiting)]);
    assert!(pending[0].can_reorder);
    assert!(!pending[0].can_move_up && !pending[0].can_move_down);
}

#[test]
fn adjacent_moves_skip_history_and_preserve_prerequisite_order() {
    use QueueState::*;
    let item = |id: &str, state| QueueItem {
        id: id.into(),
        state,
        ..task()
    };
    let mut tasks = [
        item("base", Waiting),
        item("completed", Completed),
        item("mods", Waiting),
        item("running", Running),
        item("another", Paused),
    ];
    tasks[2].request.depends_on = Some("base".into());
    let (_, pending, _) = rows(&tasks, &Speeds::default(), |_| Default::default());
    assert_eq!(pending[1].state.as_str(), "После установки игры");
    assert_eq!(move_target(&tasks, "mods", true), None);
    assert_eq!(move_target(&tasks, "base", false), None);
    assert_eq!(
        move_target(&tasks, "mods", false),
        Some(Some("another".into()))
    );
    assert_eq!(
        move_target(&tasks, "another", true),
        Some(Some("running".into()))
    );
    assert_eq!(move_target(&tasks, "another", false), None);
    assert_eq!(move_target(&tasks, "running", true), None);
    tasks.swap(2, 3);
    assert_eq!(
        move_target(&tasks, "mods", true),
        None,
        "blocked mods must not preempt the active operation"
    );
    assert_eq!(move_target(&tasks, "mods", false), Some(None));
    tasks[0].state = Completed;
    assert_eq!(
        move_target(&tasks, "mods", true),
        Some(Some("running".into()))
    );
}

#[test]
fn queue_model_reuses_binding_and_updates_ids_after_mutations() {
    let item = |id: &str| QueueRow {
        id: id.into(),
        ..Default::default()
    };
    let model = ModelRc::new(VecModel::from(vec![item("a"), item("b"), item("c")]));
    let expected = vec![
        item("c"),
        QueueRow {
            state: "На паузе".into(),
            ..item("a")
        },
        item("d"),
    ];
    sync_rows(model.clone(), expected.clone(), |_| {
        panic!("existing model was rebound")
    });
    assert_eq!(model.row_count(), expected.len());
    assert!(model.iter().zip(expected).all(|(a, b)| same_row(&a, &b)));
    let with_icon = QueueRow {
        icon: Image::from_rgba8(slint::SharedPixelBuffer::new(2, 2)),
        ..item("a")
    };
    assert!(!same_row(&item("a"), &with_icon));
    assert!(same_row(&with_icon, &with_icon.clone()));
}

#[test]
fn download_units_and_eta_follow_russian_number_format() {
    assert_eq!(localized_size(1536), "1,5 КБ");
    assert_eq!(remaining(Some(59)), "< 1 мин");
    assert_eq!(remaining(Some(60)), "≈ 1 мин");
    assert_eq!(remaining(Some(3599)), "≈ 1 ч");
    assert_eq!(remaining(Some(3601)), "≈ 1 ч 1 мин");
}
