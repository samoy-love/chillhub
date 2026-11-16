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
