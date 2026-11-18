use super::*;
use chillhub_native::{
    queue::{QueueReport, TransferOperation},
    transfer::{SyncScope, TransferEvent, TransferOutcome, TransferStage},
};

fn task(state: QueueState, operation: TransferOperation) -> QueueItem {
    QueueItem {
        id: "task".into(),
        request: chillhub_native::transfer::TransferRequest {
            game_id: "fixture".into(),
            version: "2.0".into(),
            ..Default::default()
        },
        state,
        progress: None,
        outcome: None,
        error: None,
        cleanup_files: Vec::new(),
        report: QueueReport {
            operation: Some(operation),
            ..Default::default()
        },
    }
}

fn metrics(task: &QueueItem) -> Vec<Metric> {
    let mut metrics = Vec::new();
    report_queue_metrics(task, |metric| metrics.push(metric));
    metrics
}

fn progress(stage: TransferStage, network_bytes: u64, reused_bytes: u64) -> TransferEvent {
    TransferEvent {
        stage,
        current_file: "Game.exe".into(),
        completed_bytes: network_bytes + reused_bytes,
        total_bytes: 2000,
        network_bytes,
        reused_bytes,
        completed_files: 0,
        total_files: 12,
    }
}

#[test]
fn completed_verification_reports_network_bytes_and_file_counts() {
    let mut task = task(QueueState::Completed, TransferOperation::Verify);
    task.request.scope = SyncScope::OwnFilesOnly;
    task.progress = Some(progress(TransferStage::Checking, 2, 0));
    task.outcome = Some(TransferOutcome {
        network_bytes: 850,
        reused_bytes: 1150,
        ..Default::default()
    });
    task.report.duration_ms = 125;
    task.report.files_total = 12;
    task.report.files_downloaded = 3;
    task.report.full_bytes = 2000;
    task.report.hash_mismatches = 1;

    let reports = metrics(&task);
    assert_eq!(reports.len(), 1);
    let report = serde_json::to_value(&reports[0]).unwrap();
    assert_eq!(
        report,
        serde_json::json!({
            "event": "integrity_check", "gameId": "fixture", "version": "2.0", "result": "ok",
            "bytes": 850, "durationMs": 125, "errorCode": "", "filesDownloaded": 3,
            "filesTotal": 12, "fullBytes": 2000, "hashMismatches": 1,
        })
    );
}

#[test]
fn cancellation_counts_only_received_download_bytes() {
    let mut task = task(QueueState::Cancelled, TransferOperation::Install);
    task.progress = Some(progress(TransferStage::Downloading, 28, 117));
    let reports = metrics(&task);
    assert_eq!(reports.len(), 1);
    let report = &reports[0];
    assert_eq!(report.event, "game_install");
    assert_eq!(report.result, "cancel");
    assert_eq!(report.bytes, 28);
}

#[test]
fn failed_check_reports_error_code_without_claiming_checked_bytes_as_downloaded() {
    let mut task = task(QueueState::Failed, TransferOperation::Update);
    task.progress = Some(progress(TransferStage::Checking, 512, 0));
    task.report.error_code = Some("manifest_invalid".into());
    let reports = metrics(&task);
    assert_eq!(reports.len(), 2);
    assert_eq!(reports[0].event, "game_update");
    assert_eq!(reports[0].result, "fail");
    assert_eq!(reports[0].bytes, 0);
    assert_eq!(reports[1].event, "error");
    assert_eq!(reports[1].error_code, "manifest_invalid");
    assert_eq!(reports[1].game_id, "fixture");

    task.request.scope = SyncScope::OwnFilesOnly;
    let reports = metrics(&task);
    assert_eq!(reports.len(), 1);
    assert_eq!(reports[0].event, "error");
}

#[test]
fn library_shows_running_base_before_its_waiting_modpack_and_clears_old_failure() {
    let mut base = task(QueueState::Running, TransferOperation::Install);
    base.id = "base".into();
    let mut mods = task(QueueState::Waiting, TransferOperation::Install);
    mods.id = "mods".into();
    mods.request.depends_on = Some("base".into());
    let mut tasks = vec![base, mods];
    assert_eq!(library_task(&tasks, "fixture").unwrap().id, "base");
    tasks[0].state = QueueState::Completed;
    assert_eq!(library_task(&tasks, "fixture").unwrap().id, "mods");
    tasks[1].state = QueueState::Failed;
    assert_eq!(
        library_task(&tasks, "fixture").unwrap().state,
        QueueState::Failed
    );
    tasks.push(task(QueueState::Completed, TransferOperation::Install));
    assert!(library_task(&tasks, "fixture").is_none());
    assert!(library_task(&tasks, "other-game").is_none());
}

#[test]
fn library_labels_keep_the_stage_visible_without_truncating_actionable_states() {
    for (state, label) in [
        (QueueState::Failed, "Ошибка загрузки"),
        (QueueState::NeedsConfirmation, "Нужен выбор"),
        (QueueState::Pausing, "Ставим на паузу"),
    ] {
        assert_eq!(
            library_queue_label(&task(state, TransferOperation::Install)),
            label
        );
    }
    let mut running = task(QueueState::Running, TransferOperation::Verify);
    for stage in [TransferStage::Checking, TransferStage::Verifying] {
        running.progress = Some(progress(stage, 0, 0));
        assert_eq!(library_queue_label(&running), "Проверяем");
    }
    running.progress = Some(progress(TransferStage::Downloading, 10, 0));
    assert_eq!(library_queue_label(&running), "Восстанавливаем");
}

#[test]
fn library_status_shapes_follow_queue_state_and_operation() {
    for (state, expected) in [
        (QueueState::Waiting, "queued"),
        (QueueState::Paused, "paused"),
        (QueueState::Pausing, "paused"),
        (QueueState::Failed, "error"),
        (QueueState::NeedsConfirmation, "confirmation"),
        (QueueState::Running, "active"),
    ] {
        assert_eq!(
            library_queue_kind(&task(state, TransferOperation::Install)),
            expected
        );
    }
    let mut repair = task(QueueState::Running, TransferOperation::Verify);
    assert_eq!(library_queue_kind(&repair), "recovery");
    assert_eq!(library_queue_label(&repair), "Восстанавливаем");
    for stage in [TransferStage::Checking, TransferStage::Verifying] {
        repair.progress = Some(progress(stage, 0, 0));
        assert_eq!(library_queue_kind(&repair), "checking");
    }
}

#[test]
fn queue_security_consent_uses_only_the_selected_blocked_download() {
    use crate::{AppState, controller::bind};
    use chillhub_native::{paths::AppPaths, service::Services};
    use std::{cell::RefCell, rc::Rc};

    i_slint_backend_testing::init_no_event_loop();
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let root = paths.games_dir.join("fixture");
    let (services, prefs) = Services::new(paths).unwrap();
    let state = Rc::new(RefCell::new(AppState::new(
        services,
        prefs,
        temp.path().into(),
        temp.path().join("art"),
        vec![],
        None,
    )));
    let ui = AppWindow::new().unwrap();
    bind(&ui, &state);
    ui.set_panel_kind("downloads".into());
    let mut blocked = task(QueueState::Running, TransferOperation::Install);
    blocked.request.root = root.clone();
    blocked.error = Some("test security incident".into());
    blocked.report.error_code = Some("sync_security_blocked".into());
    let mut other = blocked.clone();
    other.id = "other".into();
    other.state = QueueState::Failed;
    other.report.error_code = Some("sync_network".into());
    state.borrow_mut().tasks = vec![blocked, other];

    ui.invoke_queue_action("missing".into(), "security".into());
    ui.invoke_queue_action("other".into(), "security".into());
    ui.invoke_queue_action("task".into(), "security".into());
    assert_eq!(ui.get_panel_kind(), "downloads");
    assert!(state.borrow().pending.is_none());

    state.borrow_mut().tasks[0].state = QueueState::Failed;
    ui.invoke_queue_action("task".into(), "security".into());
    assert_eq!(ui.get_panel_kind(), "confirm");
    let body = ui.get_panel_body();
    assert!(body.contains("test security incident"));
    assert!(body.contains(root.to_string_lossy().as_ref()));
    assert!(matches!(
        state.borrow().pending.as_ref(),
        Some(Confirmation::Security(paths)) if paths.as_slice() == [root]
    ));
    ui.invoke_action_requested("cancel-action".into());
    assert!(!ui.get_operation_busy());
    assert!(state.borrow().pending.is_none());
    assert!(state.borrow().services.queue.snapshot().is_empty());
    state.borrow().services.queue.shutdown();
}
