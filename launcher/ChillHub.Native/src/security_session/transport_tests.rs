use super::*;

fn connected_pair() -> (fs::File, fs::File) {
    let name = format!(r"\\.\pipe\ChillHub.Test.{}", uuid::Uuid::new_v4());
    let server = create_pipe(&name).unwrap();
    // FIRST_PIPE_INSTANCE must prevent a second server impersonating us.
    assert!(create_pipe(&name).is_err());
    let client = fs::OpenOptions::new()
        .read(true)
        .write(true)
        .open(&name)
        .unwrap();
    unsafe {
        ConnectNamedPipe(handle(&server), std::ptr::null_mut());
    }
    (server, client)
}

#[test]
fn authenticated_pipe_preserves_unicode_and_message_boundaries() {
    let (mut server, mut client) = connected_pair();
    let peer = peer_process(std::process::id()).unwrap();
    assert!(alive(&peer));
    assert!(current_sid().unwrap().starts_with("S-1-"));
    let first = serde_json::json!({"path":"C:\\Игры\\PEAK", "success":true});
    let second = serde_json::json!({"error":"Доступ запрещён", "success":false});
    send(&mut client, &first).unwrap();
    send(&mut client, &second).unwrap();
    let deadline = Some(Instant::now() + Duration::from_secs(2));
    assert_eq!(
        serde_json::from_str::<serde_json::Value>(&line(&mut server, &peer, deadline).unwrap())
            .unwrap(),
        first
    );
    assert_eq!(
        serde_json::from_str::<serde_json::Value>(&line(&mut server, &peer, deadline).unwrap())
            .unwrap(),
        second
    );
    send(&mut server, &second).unwrap();
    assert_eq!(
        serde_json::from_str::<serde_json::Value>(&line(&mut client, &peer, deadline).unwrap())
            .unwrap(),
        second
    );
}

#[test]
fn pipe_waits_for_a_delayed_response_without_treating_idle_as_eof() {
    let (mut server, mut client) = connected_pair();
    let peer = peer_process(std::process::id()).unwrap();
    let writer = std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(100));
        send(&mut client, &"готово").unwrap();
    });
    assert_eq!(
        line(
            &mut server,
            &peer,
            Some(Instant::now() + Duration::from_secs(5))
        )
        .unwrap(),
        "\"готово\""
    );
    writer.join().unwrap();
}

#[test]
fn pipe_rejects_invalid_utf8_and_disconnected_peer() {
    let (mut server, mut client) = connected_pair();
    let peer = peer_process(std::process::id()).unwrap();
    client.write_all(&[0xff, b'\n']).unwrap();
    assert!(
        line(&mut server, &peer, Some(Instant::now()))
            .unwrap_err()
            .contains("Некорректный ответ")
    );
    drop(client);
    assert!(line(&mut server, &peer, Some(Instant::now())).is_err());
}

#[test]
fn pipe_timeout_and_outgoing_limit_are_fail_closed() {
    let (mut server, mut client) = connected_pair();
    let peer = peer_process(std::process::id()).unwrap();
    assert!(
        line(&mut server, &peer, Some(Instant::now()))
            .unwrap_err()
            .contains("вовремя")
    );
    assert!(
        send(&mut client, &"x".repeat(65537))
            .unwrap_err()
            .contains("Слишком большой")
    );
    send(&mut client, &"still usable").unwrap();
    assert_eq!(
        line(&mut server, &peer, Some(Instant::now())).unwrap(),
        "\"still usable\""
    );
    assert!(peer_process(0).is_err());
}

#[test]
fn pipe_rejects_oversized_incoming_message() {
    let (mut server, mut client) = connected_pair();
    let peer = peer_process(std::process::id()).unwrap();
    let writer = std::thread::spawn(move || {
        let _ = client.write_all(&vec![b'x'; 65538]);
    });
    assert!(
        line(
            &mut server,
            &peer,
            Some(Instant::now() + Duration::from_secs(5))
        )
        .unwrap_err()
        .contains("Слишком большой")
    );
    drop(server);
    writer.join().unwrap();
}

#[test]
fn ordinary_arguments_never_start_an_elevated_session() {
    assert!(elevated_request_from_args(&[]).unwrap().is_none());
    assert!(
        elevated_request_from_args(&["--preview".into()])
            .unwrap()
            .is_none()
    );
    assert!(elevated_request_from_args(&["--elevated-session".into()]).is_err());
}

#[test]
fn elevated_request_checks_token_and_paths_before_opening_an_existing_shared_lease() {
    let temp = tempfile::tempdir().unwrap();
    let game = temp.path().join("Games/Игры");
    fs::create_dir_all(&game).unwrap();
    let config = temp.path().join("state");
    let lease_path = config.join("security-session.lock");
    let token = "a".repeat(64);
    let envelope = || Envelope {
        token: token.clone(),
        request: SecurityRequest {
            paths: vec![game.clone(), game.clone()],
            operation: SecurityOperation::Add,
        },
        lease_path: lease_path.clone(),
    };

    assert!(
        accept_envelope(envelope(), &token[..63])
            .unwrap_err()
            .contains("не совпал")
    );
    let mut malformed = envelope();
    malformed.token.replace_range(31..32, "b");
    assert!(
        accept_envelope(malformed, &token)
            .unwrap_err()
            .contains("не совпал")
    );
    let mut malformed = envelope();
    malformed.request.paths.push(temp.path().join("missing"));
    assert!(
        accept_envelope(malformed, &token)
            .unwrap_err()
            .contains("не существует")
    );
    let mut malformed = envelope();
    malformed.lease_path = config.join("other.lock");
    assert!(
        accept_envelope(malformed, &token)
            .unwrap_err()
            .contains("Некорректная сессия")
    );
    assert!(accept_envelope(envelope(), &token).is_err());
    assert!(!config.exists());

    let ordinary = open_lease(&lease_path, true, false).unwrap();
    let (request, elevated) = accept_envelope(envelope(), &token).unwrap();
    assert_eq!(request.paths, [validate_directory(&game).unwrap()]);
    assert_eq!(request.operation, SecurityOperation::Add);
    drop(ordinary);
    assert!(update_lease(&config).is_err());
    drop(elevated);
    let _update = update_lease(&config).unwrap();
    assert!(
        accept_envelope(envelope(), &token)
            .unwrap_err()
            .contains("завершите настройку")
    );
}

fn applied_result(path: &Path) -> ExclusionResult {
    ExclusionResult {
        path: path.display().to_string(),
        status: "applied".into(),
        detail: String::new(),
        added: true,
    }
}

fn nonblocking_client(client: &fs::File) {
    assert_ne!(
        unsafe {
            SetNamedPipeHandleState(
                handle(client),
                &PIPE_NOWAIT,
                std::ptr::null_mut(),
                std::ptr::null_mut(),
            )
        },
        0,
    );
}

#[test]
fn elevated_worker_waits_for_each_journal_write_and_stops_on_failure() {
    let temp = tempfile::tempdir().unwrap();
    let paths: Vec<_> = ["First", "Second"]
        .into_iter()
        .map(|name| {
            let path = temp.path().join(name);
            fs::create_dir(&path).unwrap();
            validate_directory(&path).unwrap()
        })
        .collect();
    let request = SecurityRequest {
        paths: paths.clone(),
        operation: SecurityOperation::Add,
    };
    let journal = temp.path().join("security-exclusions.json");
    for fail_first in [false, true] {
        let (mut server, mut client) = connected_pair();
        nonblocking_client(&client);
        let peer = peer_process(std::process::id()).unwrap();
        let child_peer = peer_process(std::process::id()).unwrap();
        let child_request = request.clone();
        let child_journal = journal.clone();
        let child = std::thread::spawn(move || {
            let mut calls = 0;
            let result = apply_with(
                &child_request,
                true,
                |input| {
                    let requested: SecurityRequest = serde_json::from_slice(input).unwrap();
                    assert_eq!(requested.paths, child_request.paths[calls..calls + 1]);
                    assert_eq!(
                        read_journal(&child_journal).unwrap().added_paths,
                        child_request.paths[..calls],
                    );
                    calls += 1;
                    serde_json::to_string(&[applied_result(&requested.paths[0])])
                        .map_err(|error| error.to_string())
                },
                |result| {
                    send_result_and_wait_for_receipt(
                        &mut client,
                        &child_peer,
                        result,
                        Instant::now() + Duration::from_secs(5),
                    )
                },
            );
            (calls, result)
        });

        let mut recorded = Vec::new();
        let parent_result = receive_results(
            &request,
            &mut server,
            &peer,
            Some(Instant::now() + Duration::from_secs(5)),
            |result| {
                recorded.push(PathBuf::from(&result.path));
                if fail_first {
                    return Err("disk unavailable".into());
                }
                record_results(&journal, std::slice::from_ref(result))
            },
        );
        let (calls, child_result) = child.join().unwrap();
        if fail_first {
            let error = parent_result.unwrap_err();
            assert!(error.contains("журнал сохранить не удалось: disk unavailable"));
            assert_eq!(child_result.unwrap_err(), error);
            assert_eq!(calls, 1, "the second OS change must not run");
            assert_eq!(recorded, paths[..1]);
            assert!(!journal.exists());
        } else {
            parent_result.unwrap();
            assert_eq!(child_result.unwrap().len(), paths.len());
            assert_eq!(calls, paths.len());
            assert_eq!(recorded, paths);
            assert_eq!(read_journal(&journal).unwrap().added_paths, paths);
            fs::remove_file(&journal).unwrap();
        }
    }
}

#[test]
fn result_without_a_valid_journal_receipt_fails_closed() {
    let temp = tempfile::tempdir().unwrap();
    let applied = applied_result(&temp.path().join("First"));
    let (mut server, mut client) = connected_pair();
    nonblocking_client(&client);
    let peer = peer_process(std::process::id()).unwrap();
    let error =
        send_result_and_wait_for_receipt(&mut client, &peer, &applied, Instant::now()).unwrap_err();
    assert!(error.contains("сохранение журнала не подтверждено"));
    assert!(error.contains("вовремя"));
    let result: ExclusionResult =
        serde_json::from_str(&line(&mut server, &peer, Some(Instant::now())).unwrap()).unwrap();
    assert_eq!(result.path, applied.path);

    send(&mut server, &"malformed receipt").unwrap();
    let error =
        send_result_and_wait_for_receipt(&mut client, &peer, &applied, Instant::now()).unwrap_err();
    assert!(error.contains("Некорректное подтверждение"));

    let server_peer = peer_process(std::process::id()).unwrap();
    let (mut server, mut client) = connected_pair();
    nonblocking_client(&client);
    let close_after_result = std::thread::spawn(move || {
        let sent = line(
            &mut server,
            &server_peer,
            Some(Instant::now() + Duration::from_secs(5)),
        )
        .unwrap();
        assert!(serde_json::from_str::<ExclusionResult>(&sent).is_ok());
    });
    let error = send_result_and_wait_for_receipt(
        &mut client,
        &peer,
        &applied,
        Instant::now() + Duration::from_secs(5),
    )
    .unwrap_err();
    close_after_result.join().unwrap();
    assert!(error.contains("сохранение журнала не подтверждено"));
    assert!(!error.contains("вовремя"));
}

#[test]
fn parent_rejects_malformed_foreign_and_repeated_pipe_results_before_persisting_them() {
    let temp = tempfile::tempdir().unwrap();
    let first = temp.path().join("First");
    let second = temp.path().join("Second");
    let foreign = temp.path().join("Other");
    let request = SecurityRequest {
        paths: vec![first.clone(), second],
        operation: SecurityOperation::Add,
    };
    let peer = peer_process(std::process::id()).unwrap();
    for (reply, expected) in [
        ("not json".to_string(), "Некорректный результат"),
        (
            serde_json::to_string(&applied_result(&foreign)).unwrap(),
            "неизвестный или повторный",
        ),
        (
            serde_json::to_string(&applied_result(&first)).unwrap(),
            "неизвестный или повторный",
        ),
    ] {
        let (mut server, mut client) = connected_pair();
        nonblocking_client(&client);
        send(&mut client, &applied_result(&first)).unwrap();
        client.write_all(format!("{reply}\n").as_bytes()).unwrap();
        let mut recorded = Vec::new();
        let error = receive_results(
            &request,
            &mut server,
            &peer,
            Some(Instant::now() + Duration::from_secs(5)),
            |result| {
                recorded.push(PathBuf::from(&result.path));
                Ok(())
            },
        )
        .unwrap_err();
        assert!(error.contains(expected), "{error}");
        assert_eq!(recorded.as_slice(), std::slice::from_ref(&first));
        let receipt = line(&mut client, &peer, Some(Instant::now())).unwrap();
        assert!(matches!(
            serde_json::from_str(&receipt).unwrap(),
            JournalReceipt::Recorded,
        ));
        assert!(line(&mut client, &peer, Some(Instant::now())).is_err());
    }
}
