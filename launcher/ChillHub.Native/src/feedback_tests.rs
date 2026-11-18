use super::*;
#[test]
fn development_fallback_is_local_and_never_bypasses_rejection_or_quota() {
    for status in [404, 503, 400, 429] {
        let temp = tempfile::tempdir().unwrap();
        let primary = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let admin = std::sync::Arc::new(tiny_http::Server::http("127.0.0.1:0").unwrap());
        let service = FeedbackService::new(
            ApiClient::new(&format!("http://{}", primary.server_addr())).unwrap(),
            temp.path().into(),
        )
        .with_local_admin_port(Some(admin.server_addr().to_ip().unwrap().port()));
        let primary_worker = std::thread::spawn(move || {
            let request = primary
                .recv_timeout(std::time::Duration::from_secs(10))
                .unwrap()
                .unwrap();
            request.respond(tiny_http::Response::empty(status)).unwrap();
        });
        let admin_worker = if matches!(status, 404 | 503) {
            let admin = std::sync::Arc::clone(&admin);
            Some(std::thread::spawn(move || {
                let mut request = admin
                    .recv_timeout(std::time::Duration::from_secs(10))
                    .unwrap()
                    .expect("fallback request should reach the local admin endpoint");
                assert_eq!(request.url(), "/feedback/submit");
                let body: serde_json::Value = serde_json::from_reader(request.as_reader()).unwrap();
                assert_eq!(body["comment"], "local fixture");
                request.respond(tiny_http::Response::empty(200)).unwrap();
            }))
        } else {
            None
        };
        let result = service.submit(FeedbackDraft {
            comment: "local fixture".into(),
            ..Default::default()
        });
        match status {
            404 | 503 => assert_eq!(result, SendResult::Sent),
            429 => assert!(matches!(result, SendResult::RateLimited(_))),
            _ => assert!(matches!(result, SendResult::Rejected(_))),
        }
        primary_worker.join().unwrap();
        if let Some(worker) = admin_worker {
            worker.join().unwrap();
        } else {
            assert!(
                admin.try_recv().unwrap().is_none(),
                "rejected or rate-limited feedback must not reach the local admin endpoint"
            );
        }
    }
    let temp = tempfile::tempdir().unwrap();
    for base in ["https://launcher.samoy.love", "http://127.0.0.1:55777"] {
        assert!(
            FeedbackService::new(ApiClient::new(base).unwrap(), temp.path().into())
                .local_admin_endpoint()
                .is_none()
        );
    }
}
#[test]
fn invalid_message_is_not_saved_or_sent() {
    let temp = tempfile::tempdir().unwrap();
    let service = FeedbackService::new(
        ApiClient::new("http://127.0.0.1:9").unwrap(),
        temp.path().into(),
    );
    assert!(matches!(
        service.submit(FeedbackDraft::default()),
        SendResult::Rejected(_)
    ));
    assert!(!service.queue_path().exists());
}
#[test]
fn rejected_queue_is_not_misreported_as_empty() {
    let temp = tempfile::tempdir().unwrap();
    let service = FeedbackService::new(
        ApiClient::new("http://127.0.0.1:9").unwrap(),
        temp.path().into(),
    );
    fs::write(service.queue_path(), b"broken").unwrap();
    assert!(service.pending().is_err());
    assert_eq!(fs::read(service.queue_path()).unwrap(), b"broken");
}

#[test]
fn simultaneous_retry_does_not_wait_or_spend_another_batch() {
    let temp = tempfile::tempdir().unwrap();
    let service = FeedbackService::new(
        ApiClient::new("http://127.0.0.1:9").unwrap(),
        temp.path().into(),
    );
    let _held = service.lock.lock().unwrap();
    assert_eq!(service.flush().unwrap(), 0);
}

#[test]
fn legacy_feedback_keeps_the_system_from_when_it_was_written() {
    let temp = tempfile::tempdir().unwrap();
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let service = FeedbackService::new(
        ApiClient::new(&format!("http://{}", server.server_addr())).unwrap(),
        temp.path().into(),
    );
    let legacy = br#"[{"Name":"Player","Contact":"","Type":"bug","Comment":"old report","AttachLogs":false,"System":{"os":"Windows 10","appVersion":"1.7.20"}}]"#;
    fs::write(service.queue_path(), legacy).unwrap();
    let worker = std::thread::spawn(move || {
        let mut request = server
            .recv_timeout(std::time::Duration::from_secs(5))
            .unwrap()
            .unwrap();
        let body: serde_json::Value = serde_json::from_reader(request.as_reader()).unwrap();
        assert_eq!(body["system"]["appVersion"], "1.7.20");
        assert_eq!(body["system"]["os"], "Windows 10");
        assert_eq!(body["comment"], "old report");
        request.respond(tiny_http::Response::empty(200)).unwrap();
    });
    assert_eq!(service.flush().unwrap(), 1);
    assert_eq!(service.pending().unwrap(), 0);
    worker.join().unwrap();
}

fn with_feedback_responses(
    directory: &Path,
    replies: &[(u16, Option<&str>)],
    check: impl FnOnce(&FeedbackService),
) -> Vec<(String, serde_json::Value)> {
    use std::time::Duration;

    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let api = ApiClient::new(&format!("http://{}", server.server_addr())).unwrap();
    let service = FeedbackService::new(api, directory.into()).with_local_admin_port(None);
    std::thread::scope(|scope| {
        let worker = scope.spawn(move || {
            let mut received = Vec::new();
            for &(status, retry_after) in replies {
                let mut request = server
                    .recv_timeout(Duration::from_secs(8))
                    .unwrap()
                    .unwrap();
                assert_eq!(request.url(), "/feedback/submit");
                let id = request
                    .headers()
                    .iter()
                    .find(|header| header.field.equiv("X-Request-ID"))
                    .unwrap()
                    .value
                    .to_string();
                assert!(uuid::Uuid::parse_str(&id).is_ok());
                let body = serde_json::from_reader(request.as_reader()).unwrap();
                received.push((id, body));
                let mut response = tiny_http::Response::empty(status);
                if let Some(seconds) = retry_after {
                    response
                        .add_header(tiny_http::Header::from_bytes("Retry-After", seconds).unwrap());
                }
                request.respond(response).unwrap();
            }
            received
        });
        check(&service);
        worker.join().unwrap()
    })
}

#[test]
fn retry_archives_rejected_drafts_and_keeps_order_and_identity_for_the_rest() {
    let temp = tempfile::tempdir().unwrap();
    let replies = [
        (400, None),
        (200, None),
        (200, None),
        (200, None),
        (200, None),
        (429, Some("12")),
        (200, None),
        (200, None),
    ];
    let received = with_feedback_responses(temp.path(), &replies, |service| {
        let queue: Vec<_> = (0..7)
            .map(|index| FeedbackDraft {
                comment: format!("draft {index}"),
                system: Some(BTreeMap::new()),
                id: uuid::Uuid::new_v4().to_string(),
                ..FeedbackDraft::default()
            })
            .collect();
        service.save(&queue).unwrap();

        assert_eq!(service.flush().unwrap(), 4);
        let waiting = service.load().unwrap();
        assert_eq!(waiting.len(), 2);
        assert_eq!(waiting[0].id, queue[5].id);
        assert_eq!(waiting[1].id, queue[6].id);
        let archived_path = fs::read_dir(temp.path())
            .unwrap()
            .map(|entry| entry.unwrap().path())
            .find(|path| {
                path.file_name()
                    .unwrap()
                    .to_string_lossy()
                    .starts_with("feedback-rejected-")
            })
            .unwrap();
        let archive: serde_json::Value =
            serde_json::from_slice(&fs::read(archived_path).unwrap()).unwrap();
        assert_eq!(archive["draft"]["Comment"], "draft 0");
        assert_eq!(archive["draft"]["Id"], queue[0].id);
        assert!(archive["reason"].as_str().unwrap().contains("400"));

        let before_rate_limit = fs::read(service.queue_path()).unwrap();
        assert_eq!(service.flush().unwrap(), 0);
        assert_eq!(fs::read(service.queue_path()).unwrap(), before_rate_limit);
        assert_eq!(service.flush().unwrap(), 2);
        assert_eq!(service.pending().unwrap(), 0);
    });
    let order: Vec<_> = received
        .iter()
        .map(|(_, body)| body["comment"].as_str().unwrap())
        .collect();
    assert_eq!(
        order,
        [
            "draft 0", "draft 1", "draft 2", "draft 3", "draft 4", "draft 5", "draft 5", "draft 6"
        ]
    );
    assert_eq!(
        received[5].0, received[6].0,
        "retry must keep its original request ID"
    );
}

#[test]
fn offline_submit_preserves_full_and_corrupt_queues() {
    let temp = tempfile::tempdir().unwrap();
    with_feedback_responses(temp.path(), &[(503, None), (503, None)], |service| {
        let draft = FeedbackDraft {
            comment: "new report".into(),
            system: Some(BTreeMap::new()),
            ..FeedbackDraft::default()
        };
        fs::write(service.queue_path(), "{").unwrap();
        assert!(matches!(
            service.submit(draft.clone()),
            SendResult::LocalSaveFailed(error) if error.contains("повреждена")
        ));
        assert_eq!(fs::read(service.queue_path()).unwrap(), b"{");

        service.save(&vec![draft.clone(); 100]).unwrap();
        let full = fs::read(service.queue_path()).unwrap();
        assert!(matches!(
            service.submit(draft),
            SendResult::LocalSaveFailed(error) if error.contains("заполнена")
        ));
        assert_eq!(fs::read(service.queue_path()).unwrap(), full);
    });
}

#[test]
fn player_feedback_uses_local_quota_before_posting_and_honors_server_retry_after() {
    let temp = tempfile::tempdir().unwrap();
    with_feedback_responses(temp.path(), &[(429, Some("23"))], |service| {
        let quota = temp.path().join("report_manual_rl.json");
        let draft = FeedbackDraft {
            comment: "test report".into(),
            ..FeedbackDraft::default()
        };
        let write_quota = |start| {
            fs::write(
                &quota,
                serde_json::to_vec(&Quota {
                    count: 5,
                    window_start_utc: start,
                })
                .unwrap(),
            )
            .unwrap();
        };
        write_quota(Utc::now());
        let before = fs::read(&quota).unwrap();
        assert!(matches!(
            service.submit(draft.clone()),
            SendResult::RateLimited(1..=300)
        ));
        assert_eq!(fs::read(&quota).unwrap(), before);
        write_quota(Utc::now() - chrono::Duration::minutes(6));
        assert_eq!(service.submit(draft.clone()), SendResult::RateLimited(23));
        let new: Quota = serde_json::from_slice(&fs::read(&quota).unwrap()).unwrap();
        assert_eq!(new.count, 1);
        assert!(!service.queue_path().exists());

        fs::remove_file(&quota).unwrap();
        fs::create_dir(&quota).unwrap();
        assert!(matches!(
            service.submit(draft),
            SendResult::LocalSaveFailed(_)
        ));
        assert!(quota.is_dir());
        assert!(!service.queue_path().exists());
    });
}

#[test]
fn diagnostic_logs_are_attached_only_when_requested() {
    let temp = tempfile::tempdir().unwrap();
    let logs = temp.path().join("logs");
    fs::create_dir(&logs).unwrap();
    fs::write(logs.join("launcher.log"), "feedback-fixture-runtime-detail").unwrap();
    let received = with_feedback_responses(temp.path(), &[(200, None), (200, None)], |service| {
        for attach_logs in [false, true] {
            assert_eq!(
                service.submit(FeedbackDraft {
                    comment: "  diagnostic permission check  ".into(),
                    attach_logs,
                    ..Default::default()
                }),
                SendResult::Sent
            );
        }
        assert_eq!(service.pending().unwrap(), 0);
    });
    assert_eq!(received[0].1["comment"], "diagnostic permission check");
    assert_eq!(received[0].1["attachLogs"], false);
    assert_eq!(received[0].1["logs"], "");
    assert_eq!(received[1].1["attachLogs"], true);
    assert!(
        received[1].1["logs"]
            .as_str()
            .unwrap()
            .contains("feedback-fixture-runtime-detail")
    );
}

#[test]
fn oversized_feedback_queue_is_left_intact_for_recovery() {
    let temp = tempfile::tempdir().unwrap();
    let service = FeedbackService::new(
        ApiClient::new("http://127.0.0.1:9").unwrap(),
        temp.path().into(),
    );
    let bytes = 8 * 1024 * 1024 + 1;
    let file = fs::File::create(service.queue_path()).unwrap();
    file.set_len(bytes).unwrap();
    drop(file);
    assert!(service.pending().unwrap_err().contains("слишком велика"));
    assert!(service.flush().unwrap_err().contains("слишком велика"));
    assert_eq!(fs::metadata(service.queue_path()).unwrap().len(), bytes);
}
