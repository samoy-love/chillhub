use super::*;
const FISH_ICON: &[u8] = include_bytes!("../tests/fixtures/how-to-fish-icon.png");
// A fixture starts listening before its URL acquires a striped lock. Allow
// another URL's bounded two-attempt HTTP operation (2 x 10s) to finish first.
const SERVER_WAIT: Duration = Duration::from_secs(30);

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
    let temp = tempfile::tempdir().unwrap();
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let api = ApiClient::new(&format!("http://{}", server.server_addr()))
        .unwrap()
        .with_diagnostics(temp.path().join("profile"));
    let diagnostic_root = temp.path().to_owned();
    let diagnostic_path = diagnostic_root.join(format!(
        "{}.img",
        blake3::hash(api.base.join("/fish.png").unwrap().as_str().as_bytes()).to_hex()
    ));
    let worker = std::thread::spawn(move || {
        let first = server.recv_timeout(SERVER_WAIT).unwrap().unwrap();
        assert!(
            first
                .headers()
                .iter()
                .all(|h| !h.field.equiv("If-None-Match"))
        );
        first
            .respond(
                tiny_http::Response::from_data(FISH_ICON)
                    .with_header(tiny_http::Header::from_bytes("ETag", "\"fish-v1\"").unwrap()),
            )
            .unwrap();
        let second = server.recv_timeout(SERVER_WAIT).unwrap().unwrap();
        assert!(
            second
                .headers()
                .iter()
                .any(|h| h.field.equiv("If-None-Match") && h.value.as_str() == "\"fish-v1\""),
            "Second request: {} {}; headers: {:?}; {}",
            second.method(),
            second.url(),
            second.headers(),
            cache_diagnostics(&diagnostic_root, &diagnostic_path)
        );
        second.respond(tiny_http::Response::empty(304)).unwrap();
    });
    let path = image_path("/fish.png", temp.path(), temp.path(), &api).unwrap();
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
    assert_eq!(
        image_path("/fish.png", temp.path(), temp.path(), &api).unwrap(),
        path
    );
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
    worker.join().unwrap();
}

#[cfg(windows)]
#[test]
fn temporary_validator_sharing_violation_keeps_conditional_request() {
    use std::os::windows::fs::OpenOptionsExt;
    let temp = tempfile::tempdir().unwrap();
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let api = ApiClient::new(&format!("http://{}", server.server_addr()))
        .unwrap()
        .with_diagnostics(temp.path().join("profile"));
    let path = stale_image(&api, temp.path());
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
    let worker = std::thread::spawn(move || {
        let request = server.recv_timeout(SERVER_WAIT).unwrap().unwrap();
        let conditional = request
            .headers()
            .iter()
            .any(|h| h.field.equiv("If-None-Match") && h.value.as_str() == "\"fish-v1\"");
        let headers = format!("{:?}", request.headers());
        request
            .respond(tiny_http::Response::empty(if conditional {
                304
            } else {
                404
            }))
            .unwrap();
        assert!(
            conditional,
            "Expected If-None-Match after unlocking metadata: {headers}; {}",
            cache_diagnostics(&diagnostic_root, &diagnostic_path)
        );
    });
    let mut reads = 0;
    assert_eq!(
        image_path_with_read_observer(
            "/fish.png",
            temp.path(),
            temp.path(),
            &api,
            |error, attempt| {
                assert_eq!(error.raw_os_error(), Some(32));
                assert_eq!(attempt, 1);
                reads += 1;
                drop(held.take());
            }
        )
        .unwrap(),
        path
    );
    worker.join().unwrap();
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
            < CACHE_TTL
    );
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
