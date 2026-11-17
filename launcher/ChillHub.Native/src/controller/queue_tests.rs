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
