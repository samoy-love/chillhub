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
