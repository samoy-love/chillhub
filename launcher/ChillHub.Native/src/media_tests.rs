use super::*;
use std::{
    io::{self, BufRead, BufReader, Write},
    net::{TcpListener, TcpStream},
    sync::{Arc, mpsc},
};

const FISH_ICON: &[u8] = include_bytes!("../tests/fixtures/how-to-fish-icon.png");
// A fixture starts listening before its URL acquires a striped lock. Allow
// another URL's bounded two-attempt HTTP operation (2 x 10s) to finish first.
const SERVER_WAIT: Duration = Duration::from_secs(30);

fn image_listener() -> TcpListener {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    listener
}

fn accept_image_connection(listener: &TcpListener, deadline: Instant) -> TcpStream {
    let stream = loop {
        match listener.accept() {
            Ok((stream, _)) => break stream,
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                assert!(Instant::now() < deadline, "expected raw image request");
                std::thread::sleep(Duration::from_millis(10));
            }
            Err(error) => panic!("could not accept raw image request: {error}"),
        }
    };
    stream.set_nonblocking(false).unwrap();
    let remaining = deadline.saturating_duration_since(Instant::now());
    assert!(!remaining.is_zero(), "expected raw image request");
    stream.set_read_timeout(Some(remaining)).unwrap();
    stream.set_write_timeout(Some(remaining)).unwrap();
    stream
}

// A private trust root keeps these requests on the wire while isolating them
// from local plaintext HTTP filtering.
fn tls_image_server() -> (TcpListener, ApiClient, reqwest::blocking::Client) {
    let listener = image_listener();
    let address = listener.local_addr().unwrap();
    let api = ApiClient::new(&format!("https://localhost:{}", address.port())).unwrap();
    let ca =
        reqwest::Certificate::from_pem(include_bytes!("../tests/fixtures/media-loopback/root.pem"))
            .unwrap();
    let client = client_builder()
        .no_proxy()
        .resolve("localhost", address)
        .tls_built_in_root_certs(false)
        .add_root_certificate(ca)
        .build()
        .unwrap();
    (listener, api, client)
}

type TlsImageConnection = BufReader<rustls::StreamOwned<rustls::ServerConnection, TcpStream>>;

fn tls_image_request(listener: &TcpListener) -> (TlsImageConnection, String) {
    use rustls::pki_types::{CertificateDer, PrivateKeyDer, pem::PemObject};
    static TLS: OnceLock<Arc<rustls::ServerConfig>> = OnceLock::new();
    let config = TLS.get_or_init(|| {
        let cert = CertificateDer::from_pem_slice(include_bytes!(
            "../tests/fixtures/media-loopback/server.pem"
        ))
        .unwrap();
        // The public fixture key is only valid for the loopback certificate.
        let key = PrivateKeyDer::from_pem_slice(include_bytes!(
            "../tests/fixtures/media-loopback/server-key.pem"
        ))
        .unwrap();
        Arc::new(
            rustls::ServerConfig::builder()
                .with_no_client_auth()
                .with_single_cert(vec![cert], key)
                .unwrap(),
        )
    });
    let deadline = Instant::now() + SERVER_WAIT;
    let mut accepted_cancel = false;
    loop {
        let socket = accept_image_connection(listener, deadline);
        let tls = rustls::ServerConnection::new(config.clone()).unwrap();
        let mut connection = BufReader::new(rustls::StreamOwned::new(tls, socket));
        match raw_image_request(&mut connection) {
            Ok(headers) => return (connection, headers),
            Err(error)
                if !accepted_cancel
                    && matches!(
                        error.kind(),
                        io::ErrorKind::ConnectionReset
                            | io::ErrorKind::ConnectionAborted
                            | io::ErrorKind::UnexpectedEof
                    ) =>
            {
                // An aborted connection delivered no complete HTTP request.
                // Keep listening for the client's one remaining attempt.
                accepted_cancel = true;
                eprintln!(
                    "Loopback client cancelled before HTTP: {error}; local={:?}, peer={:?}",
                    connection.get_ref().sock.local_addr(),
                    connection.get_ref().sock.peer_addr(),
                );
            }
            Err(error) => panic!("Could not receive a TLS HTTP request: {error}"),
        }
    }
}

fn raw_image_request(connection: &mut impl BufRead) -> io::Result<String> {
    let mut reader = connection.take(8192);
    let mut headers = String::new();
    loop {
        let offset = headers.len();
        match reader.read_line(&mut headers) {
            Ok(0) if headers.is_empty() => {
                return Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "missing HTTP request",
                ));
            }
            Ok(0) => {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "incomplete HTTP headers",
                ));
            }
            Err(error) if headers.is_empty() => return Err(error),
            Err(error) => return Err(io::Error::new(io::ErrorKind::InvalidData, error)),
            Ok(_) => {}
        }
        if &headers[offset..] == "\r\n" {
            break;
        }
    }
    Ok(headers)
}

#[test]
fn partial_http_headers_are_not_a_pre_request_disconnect() {
    struct Reset;
    impl Read for Reset {
        fn read(&mut self, _: &mut [u8]) -> io::Result<usize> {
            Err(io::ErrorKind::ConnectionReset.into())
        }
    }

    assert_eq!(
        raw_image_request(&mut BufReader::new(Reset))
            .unwrap_err()
            .kind(),
        io::ErrorKind::ConnectionReset
    );
    for fragment in [b"GET /fish.png".as_slice(), b"GET /fish.png HTTP/1.1\r\n"] {
        let mut connection = BufReader::new(fragment.chain(Reset));
        assert_eq!(
            raw_image_request(&mut connection).unwrap_err().kind(),
            io::ErrorKind::InvalidData
        );
    }
}

fn raw_response(
    connection: &mut impl Write,
    status: &str,
    headers: &str,
    body: &[u8],
    keep_alive: bool,
) {
    let connection_header = if keep_alive { "keep-alive" } else { "close" };
    let mut response = format!("HTTP/1.1 {status}\r\nConnection: {connection_header}\r\n");
    if !status.starts_with("304 ") {
        response.push_str(&format!("Content-Length: {}\r\n", body.len()));
    }
    response.push_str(headers);
    response.push_str("\r\n");
    let mut response = response.into_bytes();
    response.extend_from_slice(body);
    connection.write_all(&response).unwrap();
    connection.flush().unwrap();
}

fn raw_header<'a>(headers: &'a str, name: &str) -> Option<&'a str> {
    headers.lines().skip(1).find_map(|line| {
        let (header, value) = line.split_once(':')?;
        header.eq_ignore_ascii_case(name).then(|| value.trim())
    })
}

fn image_server(statuses: Vec<u16>) -> (ApiClient, std::thread::JoinHandle<()>) {
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let api = ApiClient::new(&format!("http://{}", server.server_addr())).unwrap();
    let worker = std::thread::spawn(move || {
        for status in statuses {
            let request = server
                .recv_timeout(SERVER_WAIT)
                .unwrap()
                .expect("expected image request");
            request
                .respond(tiny_http::Response::from_data(FISH_ICON).with_status_code(status))
                .unwrap();
        }
    });
    (api, worker)
}

#[test]
fn how_to_fish_production_icon_decodes_without_extension() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("cache.img");
    fs::write(&path, FISH_ICON).unwrap();
    assert!(cached_image(&path));
    let decoded = image::ImageReader::open(&path)
        .unwrap()
        .with_guessed_format()
        .unwrap()
        .decode()
        .unwrap();
    assert_eq!((decoded.width(), decoded.height()), (256, 256));
}

#[test]
fn transient_image_failure_retries_and_cache_works_offline() {
    let temp = tempfile::tempdir().unwrap();
    let (api, server) = image_server(vec![503, 200]);
    let api = api.with_diagnostics(temp.path().join("profile"));
    let cache = temp.path().join("cache");
    let path = image_path("/fish.png", temp.path(), &cache, &api).unwrap();
    server.join().unwrap();
    assert_eq!(fs::read(&path).unwrap(), FISH_ICON);
    assert_eq!(
        image_path("/fish.png", temp.path(), &cache, &api).unwrap(),
        path
    );
    let logs = fs::read_to_string(temp.path().join("profile/logs/launcher.log")).unwrap();
    assert!(logs.contains("image.error") && logs.contains("image.download"));
}

#[test]
fn corrupt_cached_image_is_replaced_and_missing_image_is_not_cached() {
    let temp = tempfile::tempdir().unwrap();
    let (api, server) = image_server(vec![200, 404]);
    let url = api.base.join("/fish.png").unwrap();
    let hash = blake3::hash(url.as_str().as_bytes()).to_hex();
    fs::write(temp.path().join(format!("{hash}.img")), b"broken download").unwrap();
    let path = image_path("/fish.png", temp.path(), temp.path(), &api).unwrap();
    assert_eq!(fs::read(path).unwrap(), FISH_ICON);
    assert!(image_path("/missing.png", temp.path(), temp.path(), &api).is_err());
    server.join().unwrap();
}

fn stale_image(api: &ApiClient, directory: &Path) -> PathBuf {
    let address = api.base.join("/fish.png").unwrap();
    let hash = blake3::hash(address.as_str().as_bytes()).to_hex();
    let path = directory.join(format!("{hash}.img"));
    fs::write(&path, FISH_ICON).unwrap();
    let old = std::time::SystemTime::now() - CACHE_TTL - Duration::from_secs(60);
    fs::OpenOptions::new()
        .write(true)
        .open(&path)
        .unwrap()
        .set_times(fs::FileTimes::new().set_modified(old))
        .unwrap();
    path
}

#[test]
fn oversized_remote_image_preserves_valid_stale_cache() {
    let temp = tempfile::tempdir().unwrap();
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let api = ApiClient::new(&format!("http://{}", server.server_addr())).unwrap();
    let output = stale_image(&api, temp.path());
    let worker = std::thread::spawn(move || {
        let request = server.recv_timeout(SERVER_WAIT).unwrap().unwrap();
        let response = tiny_http::Response::new(
            tiny_http::StatusCode(200),
            vec![],
            std::io::Cursor::new(Vec::<u8>::new()),
            Some(MAX_IMAGE_BYTES as usize + 1),
            None,
        )
        // tiny_http defaults to chunked above 32KiB. This fixture must
        // advertise Content-Length so the client rejects it before the body.
        .with_chunked_threshold(usize::MAX);
        let _ = request.respond(response);
    });
    assert_eq!(
        image_path("/fish.png", temp.path(), temp.path(), &api).unwrap(),
        output
    );
    assert_eq!(fs::read(output).unwrap(), FISH_ICON);
    worker.join().unwrap();
}

#[cfg(windows)]
#[test]
fn replacement_sharing_violation_preserves_readable_stale_cache() {
    use std::os::windows::fs::OpenOptionsExt;
    let temp = tempfile::tempdir().unwrap();
    let (api, server) = image_server(vec![200]);
    let output = stale_image(&api, temp.path());
    // A reader without FILE_SHARE_DELETE models a scanner holding the old
    // cache file. Reads succeed, but atomic replacement must fail safely.
    let held = fs::OpenOptions::new()
        .read(true)
        .share_mode(1)
        .open(&output)
        .unwrap();
    assert_eq!(
        image_path("/fish.png", temp.path(), temp.path(), &api).unwrap(),
        output
    );
    assert_eq!(fs::read(&output).unwrap(), FISH_ICON);
    drop(held);
    server.join().unwrap();
}

#[test]
fn video_is_a_direct_file_from_the_configured_server() {
    let api = ApiClient::new("https://launcher.samoy.love").unwrap();
    assert_eq!(
        video_location("/assets/movie.mp4", &api).unwrap(),
        "https://launcher.samoy.love/assets/movie.mp4"
    );
    for bad in [
        "https://youtube.com/watch?v=test",
        "https://example.com/assets/video.mp4",
        "https://launcher.samoy.love.evil.test/assets/video.mp4",
        "//example.com/assets/video.mp4",
        "https://user@launcher.samoy.love/assets/video.mp4",
        "/assets/video.m3u8",
        "/watch",
        "",
        "/assets/video.mp4#external",
    ] {
        assert!(video_location(bad, &api).is_err(), "{bad}");
    }
    let local = ApiClient::new("http://127.0.0.1:54321").unwrap();
    assert!(video_location("/assets/movie.mp4", &local).is_ok());
    assert!(video_location("http://127.0.0.1:54322/assets/movie.mp4", &local).is_err());
}
#[test]
fn payload_cannot_read_arbitrary_files() {
    let a = ApiClient::new("https://launcher.samoy.love").unwrap();
    let b = std::env::temp_dir();
    for bad in [
        "file:///C:/secret",
        "data:text/a,hi",
        "javascript:x",
        "Assets/../../secret",
        "https://user:password@x.test/a",
    ] {
        assert!(location(bad, &b, &a).is_err(), "{bad}");
    }
}

#[test]
fn media_urls_use_server_origin_and_keep_case_sensitive_paths() {
    let api = ApiClient::new("https://launcher.samoy.love/api/v1").unwrap();
    let bundle = std::env::temp_dir();
    assert_eq!(
        location("icon.png", &bundle, &api).unwrap(),
        "https://launcher.samoy.love/icon.png"
    );
    assert_eq!(
        location("//cdn.example.test/icon.png", &bundle, &api).unwrap(),
        "https://cdn.example.test/icon.png"
    );
    assert_ne!(
        location("Icon.PNG", &bundle, &api).unwrap(),
        location("icon.png", &bundle, &api).unwrap()
    );
}

#[test]
fn stale_image_is_revalidated_without_downloading_it_again() {
    revalidate_stale_image(false);
}

#[test]
fn stale_image_is_revalidated_over_the_same_http_connection() {
    revalidate_stale_image(true);
}

fn revalidate_stale_image(keep_alive: bool) {
    let temp = tempfile::tempdir().unwrap();
    let (server, api, client) = tls_image_server();
    let api = api.with_diagnostics(temp.path().join("profile"));
    let diagnostic_root = temp.path().to_owned();
    let diagnostic_path = diagnostic_root.join(format!(
        "{}.img",
        blake3::hash(api.base.join("/fish.png").unwrap().as_str().as_bytes()).to_hex()
    ));
    let caller_diagnostics = diagnostic_path.clone();
    let (initial_finished, wait_initial) = mpsc::channel();
    let (revalidation_finished, wait_revalidation) = mpsc::channel();
    let worker = std::thread::spawn(move || {
        let (mut connection, headers) = tls_image_request(&server);
        assert!(headers.starts_with("GET /fish.png HTTP/1.1\r\n"));
        assert_eq!(raw_header(&headers, "If-None-Match"), None);
        raw_response(
            connection.get_mut(),
            "200 OK",
            "ETag: \"fish-v1\"\r\n",
            FISH_ICON,
            keep_alive,
        );
        wait_initial.recv_timeout(SERVER_WAIT).unwrap();
        let headers = if keep_alive {
            // The second request must arrive on the original stream.
            raw_image_request(&mut connection).unwrap()
        } else {
            drop(connection);
            let (next, headers) = tls_image_request(&server);
            connection = next;
            headers
        };
        let conditional = raw_header(&headers, "If-None-Match") == Some("\"fish-v1\"");
        raw_response(
            connection.get_mut(),
            if conditional {
                "304 Not Modified"
            } else {
                "404 Not Found"
            },
            "",
            &[],
            false,
        );
        assert!(
            conditional && headers.starts_with("GET /fish.png HTTP/1.1\r\n"),
            "Second wire request: {headers:?}; {}",
            cache_diagnostics(&diagnostic_root, &diagnostic_path)
        );
        // Keep the listener and socket alive until the client consumes 304.
        wait_revalidation.recv_timeout(SERVER_WAIT).unwrap();
    });
    let fetch = || {
        image_path_with_client(
            "/fish.png",
            temp.path(),
            temp.path(),
            &api,
            || Ok(&client),
            |_, _| {},
        )
    };
    let path = fetch().unwrap_or_else(|error| {
        panic!(
            "Initial fetch failed: {error}; {}",
            cache_diagnostics(temp.path(), &caller_diagnostics)
        )
    });
    initial_finished.send(()).unwrap();
    let metadata_bytes = fs::read(path.with_extension("meta")).unwrap_or_else(|error| {
        panic!(
            "Image validators were not persisted after 200: {error}; diagnostics: {}",
            fs::read_to_string(temp.path().join("profile/logs/launcher.log")).unwrap_or_default()
        )
    });
    let metadata: Validators = serde_json::from_slice(&metadata_bytes).unwrap();
    assert_eq!(metadata.etag.as_deref(), Some("\"fish-v1\""));
    assert_eq!(
        metadata.payload_hash,
        blake3::hash(FISH_ICON).to_hex().as_str()
    );
    assert_eq!(
        validators(&path).unwrap().etag.as_deref(),
        Some("\"fish-v1\"")
    );
    stale_image(&api, temp.path());
    let after_stale = cache_diagnostics(temp.path(), &path);
    assert!(cached_image(&path), "After stale_image: {after_stale}");
    assert_eq!(
        validators(&path).unwrap().etag.as_deref(),
        Some("\"fish-v1\""),
        "After stale_image: {after_stale}"
    );
    let revalidated = fetch();
    let _ = revalidation_finished.send(());
    worker.join().unwrap();
    assert_eq!(revalidated.unwrap(), path);
    assert_eq!(fs::read(&path).unwrap(), FISH_ICON);
    assert!(
        fs::metadata(&path)
            .unwrap()
            .modified()
            .unwrap()
            .elapsed()
            .unwrap()
            < CACHE_TTL,
        "Before revalidation: {after_stale}; after: {}",
        cache_diagnostics(temp.path(), &path)
    );
}

#[test]
fn reset_get_retries_once_and_preserves_a_stale_image_on_second_reset() {
    for (recover_on_retry, seeded_cache) in [(true, false), (false, false), (false, true)] {
        let temp = tempfile::tempdir().unwrap();
        let server = image_listener();
        let api = ApiClient::new(&format!("http://{}", server.local_addr().unwrap())).unwrap();
        let api = api.with_diagnostics(temp.path().join("profile"));
        let existing = seeded_cache.then(|| stale_image(&api, temp.path()));
        let (finished, wait_client) = mpsc::channel();
        let worker = std::thread::spawn(move || {
            for attempt in 1..=2 {
                let mut connection = BufReader::new(accept_image_connection(
                    &server,
                    Instant::now() + SERVER_WAIT,
                ));
                let headers = raw_image_request(&mut connection).unwrap();
                assert!(headers.starts_with("GET /fish.png HTTP/1.1\r\n"));
                if attempt == 2 && recover_on_retry {
                    raw_response(connection.get_mut(), "200 OK", "", FISH_ICON, false);
                    wait_client.recv_timeout(SERVER_WAIT).unwrap();
                } else {
                    socket2::SockRef::from(connection.get_ref())
                        .set_linger(Some(Duration::ZERO))
                        .unwrap();
                    // Zero linger makes close reset the socket before any response.
                    drop(connection);
                }
            }
        });
        let result = image_path("/fish.png", temp.path(), temp.path(), &api);
        let _ = finished.send(());
        worker.join().unwrap();
        let log = fs::read_to_string(temp.path().join("profile/logs/launcher.log")).unwrap();
        assert_eq!(
            log.matches("\"event\":\"image.cache_request\"").count(),
            2,
            "{log}"
        );
        assert!(log.contains("ConnectionReset"), "{log}");
        if recover_on_retry || seeded_cache {
            let file = result.unwrap_or_else(|error| panic!("{error}; {log}"));
            assert_eq!(fs::read(&file).unwrap(), FISH_ICON);
            if let Some(existing) = existing {
                assert_eq!(file, existing);
                assert!(
                    fs::metadata(&file)
                        .unwrap()
                        .modified()
                        .unwrap()
                        .elapsed()
                        .unwrap()
                        >= CACHE_TTL
                );
            }
        } else {
            assert!(result.is_err());
            assert!(!fs::read_dir(temp.path()).unwrap().any(|entry| {
                entry
                    .unwrap()
                    .path()
                    .extension()
                    .is_some_and(|ext| ext == "img")
            }));
        }
    }
}

#[cfg(windows)]
#[test]
fn temporary_validator_sharing_violation_keeps_conditional_request() {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::FILE_SHARE_READ;
    let temp = tempfile::tempdir().unwrap();
    let (server, api, client) = tls_image_server();
    let api = api.with_diagnostics(temp.path().join("profile"));
    let path = stale_image(&api, temp.path());
    // Display or scanning can read the cached image while denying data
    // writes. Renewing its timestamp after 304 must still work.
    let image_reader = fs::OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ)
        .open(&path)
        .unwrap();
    assert_eq!(
        fs::OpenOptions::new()
            .write(true)
            .open(&path)
            .unwrap_err()
            .raw_os_error(),
        Some(32)
    );
    let meta = Validators {
        etag: Some("\"fish-v1\"".into()),
        payload_hash: blake3::hash(FISH_ICON).to_hex().to_string(),
        ..Default::default()
    };
    fs::write(
        path.with_extension("meta"),
        serde_json::to_vec(&meta).unwrap(),
    )
    .unwrap();
    let mut held = Some(
        fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(path.with_extension("meta"))
            .unwrap(),
    );
    assert!(cached_image_result(&path).is_ok());
    assert_eq!(validators(&path).unwrap_err().raw_os_error(), Some(32));
    let diagnostic_root = temp.path().to_owned();
    let diagnostic_path = path.clone();
    let (finished, wait_client) = mpsc::channel();
    let worker = std::thread::spawn(move || {
        let (mut connection, headers) = tls_image_request(&server);
        let conditional = raw_header(&headers, "If-None-Match") == Some("\"fish-v1\"");
        raw_response(
            connection.get_mut(),
            if conditional {
                "304 Not Modified"
            } else {
                "404 Not Found"
            },
            "",
            &[],
            false,
        );
        assert!(
            conditional,
            "Expected If-None-Match on the wire after unlocking metadata: {headers:?}; {}",
            cache_diagnostics(&diagnostic_root, &diagnostic_path)
        );
        wait_client.recv_timeout(SERVER_WAIT).unwrap();
    });
    let mut reads = 0;
    let revalidated = image_path_with_client(
        "/fish.png",
        temp.path(),
        temp.path(),
        &api,
        || Ok(&client),
        |error, attempt| {
            assert_eq!(error.raw_os_error(), Some(32));
            assert_eq!(attempt, 1);
            reads += 1;
            drop(held.take());
        },
    );
    let _ = finished.send(());
    worker.join().unwrap();
    assert_eq!(revalidated.unwrap(), path);
    assert_eq!(reads, 1);
    assert_eq!(fs::read(&path).unwrap(), FISH_ICON);
    assert_eq!(
        validators(&path).unwrap().etag.as_deref(),
        Some("\"fish-v1\"")
    );
    assert!(
        fs::metadata(&path)
            .unwrap()
            .modified()
            .unwrap()
            .elapsed()
            .unwrap()
            < CACHE_TTL,
        "304 did not renew the cache: {}",
        cache_diagnostics(temp.path(), &path)
    );
    drop(image_reader);
}

#[test]
fn malformed_and_mismatched_validator_data_is_never_retried_or_trusted() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("cached.img");
    fs::write(&path, FISH_ICON).unwrap();
    for bytes in [
        b"not json".to_vec(),
        serde_json::to_vec(&Validators {
            etag: Some("\"untrusted\"".into()),
            payload_hash: "wrong-payload".into(),
            ..Default::default()
        })
        .unwrap(),
    ] {
        fs::write(path.with_extension("meta"), bytes).unwrap();
        let mut retries = 0;
        let error = validators_with_retry(&path, |_, _| retries += 1).unwrap_err();
        assert_eq!(error.kind(), std::io::ErrorKind::InvalidData);
        assert_eq!(retries, 0);
    }
}

#[cfg(windows)]
#[test]
fn validator_access_denied_is_not_retried() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("cached.img");
    fs::write(&path, FISH_ICON).unwrap();
    // Opening a directory as a regular Windows file produces ERROR_ACCESS_DENIED;
    // no ACL, real profile or process-wide environment changes are needed.
    fs::create_dir(path.with_extension("meta")).unwrap();
    let mut retries = 0;
    let error = validators_with_retry(&path, |_, _| retries += 1).unwrap_err();
    assert_eq!(error.raw_os_error(), Some(5));
    assert_eq!(retries, 0);
    assert_eq!(fs::read(&path).unwrap(), FISH_ICON);
    assert!(path.with_extension("meta").is_dir());
}

#[cfg(windows)]
#[test]
fn persistent_validator_sharing_violation_has_a_fixed_retry_limit() {
    use std::os::windows::fs::OpenOptionsExt;
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("cached.img");
    fs::write(&path, FISH_ICON).unwrap();
    let metadata = serde_json::to_vec(&Validators {
        etag: Some("\"fish-v1\"".into()),
        payload_hash: blake3::hash(FISH_ICON).to_hex().to_string(),
        ..Default::default()
    })
    .unwrap();
    fs::write(path.with_extension("meta"), &metadata).unwrap();
    let held = fs::OpenOptions::new()
        .read(true)
        .share_mode(0)
        .open(path.with_extension("meta"))
        .unwrap();
    let mut retries = Vec::new();
    let error = validators_with_retry(&path, |error, attempt| {
        retries.push((attempt, error.raw_os_error()));
    })
    .unwrap_err();
    assert_eq!(error.raw_os_error(), Some(32));
    assert_eq!(retries, [(1, Some(32)), (2, Some(32)), (3, Some(32))]);
    drop(held);
    assert_eq!(fs::read(&path).unwrap(), FISH_ICON);
    assert_eq!(fs::read(path.with_extension("meta")).unwrap(), metadata);
}

fn cache_diagnostics(root: &Path, path: &Path) -> String {
    format!(
        "cached={:?}; validators={:?}; image={:?}; meta={:?}; log={}",
        cached_image_result(path),
        validators(path),
        fs::metadata(path).map(|m| (m.len(), m.modified())),
        fs::read_to_string(path.with_extension("meta")),
        fs::read_to_string(root.join("profile/logs/launcher.log")).unwrap_or_default()
    )
}

#[test]
fn concurrent_image_requests_share_one_download() {
    let temp = tempfile::tempdir().unwrap();
    let (api, worker) = image_server(vec![200]);
    let barrier = std::sync::Barrier::new(8);
    std::thread::scope(|scope| {
        let handles: Vec<_> = (0..8)
            .map(|_| {
                let api = &api;
                let path = temp.path();
                let barrier = &barrier;
                scope.spawn(move || {
                    barrier.wait();
                    image_path("/fish.png", path, path, api).unwrap()
                })
            })
            .collect();
        let paths: Vec<_> = handles.into_iter().map(|h| h.join().unwrap()).collect();
        assert!(paths.iter().all(|p| p == &paths[0]));
    });
    worker.join().unwrap();
}

fn cached_image(path: &Path) -> bool {
    cached_image_result(path).is_ok()
}
