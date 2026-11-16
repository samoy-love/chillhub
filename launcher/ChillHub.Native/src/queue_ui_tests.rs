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
        assert_eq!(can_prioritize(state, false), movable, "move {state:?}");
        assert!(
            !can_prioritize(state, true),
            "first item has nowhere to move: {state:?}"
        );
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
    assert!(!detail(&t, 9000., None).contains("/с"));
    t.state = QueueState::Cancelling;
    assert!(!detail(&t, 9000., Some(60)).contains("100"));
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
    assert!(detail(&t, 100., None).contains("Из интернета: 0 Б"));
    assert!(detail(&t, 100., None).contains("уточняется"));
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
