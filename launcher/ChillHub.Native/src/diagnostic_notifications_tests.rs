use super::*;
#[test]
fn notices_are_profile_scoped_and_quota_notice_is_not_repeated_each_tick() {
    let temp = tempfile::tempdir().unwrap();
    let (tx, rx) = std::sync::mpsc::channel();
    subscribe(temp.path(), move |notice| {
        let _ = tx.send(notice);
    });
    let delayed = ReportNotice::Delayed {
        retry_after_seconds: 61,
    };
    notify(&temp.path().join("other"), ReportNotice::Sent, None);
    assert!(rx.try_recv().is_err());
    notify(temp.path(), delayed.clone(), Some(1));
    notify(temp.path(), delayed.clone(), Some(1));
    assert_eq!(rx.try_recv().unwrap(), delayed);
    assert!(rx.try_recv().is_err());
    notify(temp.path(), ReportNotice::Sent, None);
    assert_eq!(rx.try_recv().unwrap(), ReportNotice::Sent);
    notify(temp.path(), delayed, Some(2));
    assert!(rx.try_recv().is_ok());
}
