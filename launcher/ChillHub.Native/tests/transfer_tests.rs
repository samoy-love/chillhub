use base64::Engine as _;
use chillhub_native::queue::{DownloadQueue, QueueState};
use chillhub_native::{
    manifest::{Manifest, ManifestFile},
    transfer::{
        SyncScope, TransferControl, TransferEngine, TransferError, TransferRequest, TransferStage,
    },
};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::{self, Read},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::Duration,
};
use tempfile::TempDir;

#[test]
fn forced_game_verification_bypasses_persisted_hash_cache() {
    let temp = TempDir::new().unwrap();
    let root = temp.path().join("game");
    fs::create_dir(&root).unwrap();
    let path = root.join("data.bin");
    let original = b"original game data";
    fs::write(&path, original).unwrap();
    let manifest = Manifest {
        files: vec![ManifestFile {
            path: "data.bin".into(),
            size: original.len() as u64,
            sha256: Some(format!("{:x}", Sha256::digest(original))),
            blake3: blake3::hash(original).to_hex().to_string(),
            ..Default::default()
        }],
        ..Default::default()
    };
    let mut request = TransferRequest {
        root,
        manifest_url: "http://127.0.0.1/manifest".into(),
        content_base_url: "http://127.0.0.1/content/".into(),
        ..Default::default()
    };
    let engine = TransferEngine::new()
        .unwrap()
        .with_hash_cache_dir(temp.path().join("cache"));
    let control = TransferControl::default();
    assert!(
        engine
            .plan_manifest(&request, manifest.clone(), &control, &|_| {})
            .unwrap()
            .downloads
            .is_empty()
    );
    let modified = fs::metadata(&path).unwrap().modified().unwrap();
    fs::write(&path, vec![0; original.len()]).unwrap();
    fs::OpenOptions::new()
        .write(true)
        .open(&path)
        .unwrap()
        .set_times(fs::FileTimes::new().set_modified(modified))
        .unwrap();
    request.force_rehash = true;
    let plan = engine
        .plan_manifest(&request, manifest, &control, &|_| {})
        .unwrap();
    assert_eq!(
        plan.downloads.len(),
        1,
        "explicit verification must detect same-size corruption despite the cached timestamp"
    );
}

#[derive(Clone, Copy, PartialEq)]
enum Behavior {
    Normal,
    IgnoreRange,
    WrongRange,
    Range416Once,
    InterruptOnce,
    InterruptEveryBlock,
    Slow,
    Corrupt,
}
struct Server {
    base: String,
    stop: Arc<AtomicBool>,
    thread: Option<thread::JoinHandle<()>>,
    ranges: Arc<Mutex<Vec<Option<String>>>>,
}
impl Server {
    fn start(manifest: Manifest, data: Vec<u8>, behavior: Behavior) -> Self {
        let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let address = server.server_addr().to_string();
        let stop = Arc::new(AtomicBool::new(false));
        let stopped = stop.clone();
        let ranges = Arc::new(Mutex::new(Vec::new()));
        let seen = ranges.clone();
        let worker = thread::spawn(move || {
            let mut content_requests = 0;
            while !stopped.load(Ordering::Relaxed) {
                let Some(request) = server.recv_timeout(Duration::from_millis(20)).unwrap() else {
                    continue;
                };
                if request.url() == "/manifest" {
                    let _ = request.respond(tiny_http::Response::from_data(
                        serde_json::to_vec(&manifest).unwrap(),
                    ));
                    continue;
                }
                let range = request
                    .headers()
                    .iter()
                    .find(|h| h.field.equiv("Range"))
                    .map(|h| h.value.as_str().to_owned());
                seen.lock().unwrap().push(range.clone());
                content_requests += 1;
                if behavior == Behavior::Range416Once && content_requests == 1 {
                    let _ = request.respond(tiny_http::Response::empty(416));
                    continue;
                }
                let mut start = 0;
                let mut end = data.len().saturating_sub(1);
                let partial = range.is_some() && behavior != Behavior::IgnoreRange;
                if partial {
                    let (from, to) = range
                        .as_ref()
                        .unwrap()
                        .strip_prefix("bytes=")
                        .unwrap()
                        .split_once('-')
                        .unwrap();
                    start = from.parse().unwrap();
                    if !to.is_empty() {
                        end = to.parse().unwrap();
                    }
                }
                let count = if data.is_empty() { 0 } else { end - start + 1 };
                let mut headers = Vec::new();
                if partial {
                    let from = if behavior == Behavior::WrongRange {
                        start + 1
                    } else {
                        start
                    };
                    headers.push(
                        tiny_http::Header::from_bytes(
                            "Content-Range",
                            format!("bytes {from}-{end}/{}", data.len()),
                        )
                        .unwrap(),
                    );
                }
                let limit = if behavior == Behavior::InterruptOnce && content_requests == 1 {
                    start + count / 2
                } else if behavior == Behavior::InterruptEveryBlock {
                    start + count.min(64 * 1024)
                } else {
                    start + count
                };
                let body = TestBody {
                    data: data[start..limit].to_vec(),
                    position: 0,
                    behavior,
                    interrupted: limit != start + count,
                };
                let response = tiny_http::Response::new(
                    tiny_http::StatusCode(if partial { 206 } else { 200 }),
                    headers,
                    body,
                    Some(count),
                    None,
                );
                let _ = request.respond(response);
            }
        });
        Self {
            base: format!("http://{address}"),
            stop,
            thread: Some(worker),
            ranges,
        }
    }
    fn request(&self, root: &TempDir) -> TransferRequest {
        TransferRequest {
            game_id: "test".into(),
            version: "2".into(),
            root: root.path().join("game"),
            manifest_url: format!("{}/manifest", self.base),
            content_base_url: format!("{}/content", self.base),
            allow_cleanup: true,
            max_parallel: 2,
            ..Default::default()
        }
    }
}
impl Drop for Server {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(worker) = self.thread.take() {
            let _ = worker.join();
        }
    }
}
struct TestBody {
    data: Vec<u8>,
    position: usize,
    behavior: Behavior,
    interrupted: bool,
}
impl Read for TestBody {
    fn read(&mut self, output: &mut [u8]) -> io::Result<usize> {
        if self.position == self.data.len() && self.interrupted {
            return Err(io::Error::new(
                io::ErrorKind::ConnectionAborted,
                "intentional stream interruption",
            ));
        }
        let count = output
            .len()
            .min(16 * 1024)
            .min(self.data.len() - self.position);
        if count == 0 {
            return Ok(0);
        }
        if self.behavior == Behavior::Slow {
            thread::sleep(Duration::from_millis(4));
        }
        if self.behavior == Behavior::Corrupt {
            output[..count].fill(0x55);
        } else {
            output[..count].copy_from_slice(&self.data[self.position..self.position + count]);
        }
        self.position += count;
        Ok(count)
    }
}
fn manifest(data: &[u8]) -> Manifest {
    Manifest {
        game_id: "test".into(),
        version: "2".into(),
        files: vec![ManifestFile {
            path: "game.bin".into(),
            size: data.len() as u64,
            blake3: blake3::hash(data).to_hex().to_string(),
            sha256: Some(format!("{:x}", Sha256::digest(data))),
            ..Default::default()
        }],
        ..Default::default()
    }
}
fn data(size: usize) -> Vec<u8> {
    let mut state = 0x1234abcd_u32;
    (0..size)
        .map(|_| {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            state as u8
        })
        .collect()
}
fn prepare(root: &TempDir) {
    fs::create_dir_all(root.path().join("game")).unwrap();
}

#[test]
fn repairs_corrupted_file_and_writes_version_only_after_hashes_match() {
    let bytes = data(200_000);
    let server = Server::start(manifest(&bytes), bytes.clone(), Behavior::Normal);
    let root = TempDir::new().unwrap();
    prepare(&root);
    fs::write(root.path().join("game/game.bin"), vec![0; bytes.len()]).unwrap();
    let result = TransferEngine::new()
        .unwrap()
        .run(&server.request(&root), &TransferControl::default(), &|_| {})
        .unwrap();
    assert_eq!(fs::read(root.path().join("game/game.bin")).unwrap(), bytes);
    assert_eq!(
        fs::read_to_string(root.path().join("game/.version")).unwrap(),
        "2"
    );
    assert_eq!(result.downloaded_files, 1);
    assert!(!root.path().join("game/.updating").exists());
}

#[test]
fn queue_reports_install_update_and_verify_with_real_plan_and_network_totals() {
    use chillhub_native::queue::TransferOperation;
    let bytes = data(100_000);
    let server = Server::start(manifest(&bytes), bytes.clone(), Behavior::Slow);
    let root = TempDir::new().unwrap();
    let queue = DownloadQueue::new(root.path().join("state/queue.json")).unwrap();
    for (operation, force) in [
        (TransferOperation::Install, false),
        (TransferOperation::Update, false),
        (TransferOperation::Verify, true),
    ] {
        if operation == TransferOperation::Verify {
            fs::write(root.path().join("game/game.bin"), vec![0; bytes.len()]).unwrap();
        }
        let mut request = server.request(&root);
        request.force_rehash = force;
        let id = queue.enqueue(request).unwrap();
        wait_until(|| {
            queue
                .snapshot()
                .iter()
                .any(|item| item.id == id && item.state == QueueState::Completed)
        });
        let item = queue
            .snapshot()
            .into_iter()
            .find(|item| item.id == id)
            .unwrap();
        assert_eq!(item.report.operation, Some(operation));
        assert_eq!(item.report.files_total, 1);
        assert_eq!(item.report.full_bytes, bytes.len() as u64);
        if operation != TransferOperation::Update {
            assert_eq!(item.report.files_downloaded, 1);
            assert_eq!(item.outcome.unwrap().network_bytes, bytes.len() as u64);
            assert!(item.report.duration_ms >= 20);
        }
        assert_eq!(item.report.hash_mismatches, usize::from(force));
        assert!(item.report.error_code.is_none());
    }
}

#[test]
fn resumes_partial_on_206_and_restarts_safely_on_200_or_416() {
    for behavior in [
        Behavior::Normal,
        Behavior::IgnoreRange,
        Behavior::Range416Once,
    ] {
        let bytes = data(200_000);
        let server = Server::start(manifest(&bytes), bytes.clone(), behavior);
        let root = TempDir::new().unwrap();
        prepare(&root);
        fs::write(root.path().join("game/game.bin.part"), &bytes[..77_777]).unwrap();
        TransferEngine::new()
            .unwrap()
            .run(&server.request(&root), &TransferControl::default(), &|_| {})
            .unwrap();
        assert_eq!(fs::read(root.path().join("game/game.bin")).unwrap(), bytes);
        assert_eq!(
            server.ranges.lock().unwrap()[0].as_deref(),
            Some("bytes=77777-")
        );
    }
}

#[test]
fn wrong_content_range_never_promotes_or_deletes_existing_files() {
    let bytes = data(100_000);
    let server = Server::start(manifest(&bytes), bytes.clone(), Behavior::WrongRange);
    let root = TempDir::new().unwrap();
    prepare(&root);
    fs::write(root.path().join("game/game.bin.part"), &bytes[..4000]).unwrap();
    fs::write(root.path().join("game/extra.sav"), b"save").unwrap();
    fs::write(root.path().join("game/.version"), b"1").unwrap();
    assert!(
        TransferEngine::new()
            .unwrap()
            .run(&server.request(&root), &TransferControl::default(), &|_| {})
            .is_err()
    );
    assert!(!root.path().join("game/game.bin").exists());
    assert!(root.path().join("game/extra.sav").exists());
    assert_eq!(fs::read(root.path().join("game/.version")).unwrap(), b"1");
    assert!(root.path().join("game/.updating").exists());
}

#[test]
fn interrupted_response_resumes_from_written_prefix() {
    let bytes = data(500_000);
    let server = Server::start(manifest(&bytes), bytes.clone(), Behavior::InterruptOnce);
    let root = TempDir::new().unwrap();
    let outcome = TransferEngine::new()
        .unwrap()
        .run(&server.request(&root), &TransferControl::default(), &|_| {})
        .unwrap();
    assert_eq!(fs::read(root.path().join("game/game.bin")).unwrap(), bytes);
    assert!(
        server
            .ranges
            .lock()
            .unwrap()
            .iter()
            .skip(1)
            .any(|r| r.is_some())
    );
    assert_eq!(outcome.network_bytes, bytes.len() as u64);
}

#[test]
fn cancellation_preserves_partial_and_next_run_resumes() {
    let bytes = data(2_000_000);
    let server = Server::start(manifest(&bytes), bytes.clone(), Behavior::Slow);
    let root = TempDir::new().unwrap();
    let control = TransferControl::default();
    let engine = TransferEngine::new().unwrap();
    let result = engine.run(&server.request(&root), &control, &|e| {
        if e.stage == TransferStage::Downloading && e.completed_bytes > 100_000 {
            control.cancel();
        }
    });
    assert!(matches!(result, Err(TransferError::Cancelled)));
    let size = fs::metadata(root.path().join("game/game.bin.part"))
        .unwrap()
        .len();
    assert!(size > 0 && size < bytes.len() as u64);
    assert!(!root.path().join("game/.version").exists());
    engine
        .run(&server.request(&root), &TransferControl::default(), &|_| {})
        .unwrap();
    assert_eq!(fs::read(root.path().join("game/game.bin")).unwrap(), bytes);
    assert!(
        server
            .ranges
            .lock()
            .unwrap()
            .iter()
            .any(|r| r.as_deref() == Some(&format!("bytes={size}-")))
    );
}

#[test]
fn corrupted_download_does_not_replace_old_file_or_advance_version() {
    let bytes = data(80_000);
    let server = Server::start(manifest(&bytes), bytes, Behavior::Corrupt);
    let root = TempDir::new().unwrap();
    prepare(&root);
    fs::write(root.path().join("game/game.bin"), b"old").unwrap();
    fs::write(root.path().join("game/.version"), b"1").unwrap();
    assert!(matches!(
        TransferEngine::new().unwrap().run(
            &server.request(&root),
            &TransferControl::default(),
            &|_| {}
        ),
        Err(TransferError::Integrity(_))
    ));
    assert_eq!(fs::read(root.path().join("game/game.bin")).unwrap(), b"old");
    assert_eq!(fs::read(root.path().join("game/.version")).unwrap(), b"1");
    assert_eq!(server.ranges.lock().unwrap().len(), 3);
}

#[test]
fn cleanup_requires_confirmation_and_preserves_foreign_and_service_files() {
    let bytes = data(40_000);
    let server = Server::start(manifest(&bytes), bytes, Behavior::Normal);
    let root = TempDir::new().unwrap();
    prepare(&root);
    for name in [
        "old.bin",
        "mod.dll",
        ".mods.version",
        ".mods.revision",
        ".mods.manifest.json",
        ".integrity.json",
    ] {
        fs::write(root.path().join("game").join(name), b"keep").unwrap();
    }
    fs::create_dir(root.path().join("game/freetp")).unwrap();
    fs::write(root.path().join("game/freetp/.hash"), b"keep").unwrap();
    let mut request = server.request(&root);
    request.allow_cleanup = false;
    request.foreign_paths = vec!["mod.dll".into()];
    let engine = TransferEngine::new().unwrap();
    let result = engine.run(&request, &TransferControl::default(), &|_| {});
    assert!(
        matches!(result,Err(TransferError::NeedsCleanupConfirmation(ref files)) if files==&vec!["old.bin".to_owned()])
    );
    assert!(!root.path().join("game/game.bin").exists());
    assert!(!root.path().join("game/.updating").exists());
    request.allow_cleanup = true;
    engine
        .run(&request, &TransferControl::default(), &|_| {})
        .unwrap();
    assert!(!root.path().join("game/old.bin").exists());
    assert!(root.path().join("game/mod.dll").exists());
    assert!(root.path().join("game/.mods.revision").exists());
    assert!(root.path().join("game/.integrity.json").exists());
    assert!(root.path().join("game/freetp/.hash").exists());
}

#[test]
fn modpack_cleanup_owns_only_previous_manifest_and_uses_verified_donor() {
    let bytes = data(120_000);
    let server = Server::start(manifest(&bytes), bytes.clone(), Behavior::Corrupt);
    let root = TempDir::new().unwrap();
    prepare(&root);
    let donor = TempDir::new().unwrap();
    fs::write(donor.path().join("game.bin"), &bytes).unwrap();
    for name in ["steamgame.exe", "oldmod.dll", "doorstop_config.ini"] {
        fs::write(root.path().join("game").join(name), b"keep").unwrap();
    }
    fs::write(
        root.path().join("game/doorstop_config.ini"),
        b"[General]\nenabled=true\n",
    )
    .unwrap();
    let mut request = server.request(&root);
    request.scope = SyncScope::OwnFilesOnly;
    request.previous_owned_paths = vec!["oldmod.dll".into()];
    request.donor_roots = vec![donor.path().to_owned()];
    request.preserve_paths = vec!["doorstop_config.ini".into()];
    let outcome = TransferEngine::new()
        .unwrap()
        .run(&request, &TransferControl::default(), &|_| {})
        .unwrap();
    assert_eq!(outcome.network_bytes, 0);
    assert_eq!(outcome.reused_bytes, bytes.len() as u64);
    assert!(root.path().join("game/steamgame.exe").exists());
    assert!(root.path().join("game/doorstop_config.ini").exists());
    assert!(!root.path().join("game/oldmod.dll").exists());
    assert!(root.path().join("game/.mods.manifest.json").exists());
    assert!(!root.path().join("game/.version").exists());
}

#[test]
fn traversal_manifest_fails_before_any_write() {
    let bytes = data(100);
    let mut bad = manifest(&bytes);
    bad.files[0].path = "../outside.exe".into();
    let server = Server::start(bad, bytes, Behavior::Normal);
    let root = TempDir::new().unwrap();
    assert!(matches!(
        TransferEngine::new().unwrap().run(
            &server.request(&root),
            &TransferControl::default(),
            &|_| {}
        ),
        Err(TransferError::InvalidManifest(_))
    ));
    assert!(!root.path().join("game").exists());
    assert!(!root.path().join("outside.exe").exists());
}

#[test]
fn published_empty_directory_trailing_separator_is_normalized_and_created() {
    let mut manifest = manifest(&[]);
    manifest.empty_dirs = vec!["over the hill_Data/StreamingAssets/aa/AddressablesLink/".into()];
    let server = Server::start(manifest, vec![], Behavior::Normal);
    let root = TempDir::new().unwrap();
    TransferEngine::new()
        .unwrap()
        .run(&server.request(&root), &TransferControl::default(), &|_| {})
        .unwrap();
    assert!(
        root.path()
            .join("game/over the hill_Data/StreamingAssets/aa/AddressablesLink")
            .is_dir()
    );
}

#[test]
fn progressing_block_interruptions_do_not_exhaust_retries_or_restart_whole_download() {
    let length = 64 * 1024;
    let bytes = data(length * 8);
    let mut manifest = manifest(&bytes);
    manifest.block_size = length as u64;
    let mut digests = Vec::new();
    for block in bytes.chunks(length) {
        let weak = block.iter().fold(0u64, |hash, byte| {
            hash.wrapping_mul(0x100000001b3).wrapping_add(*byte as u64)
        });
        digests.extend(weak.to_le_bytes());
        digests.extend(&Sha256::digest(block)[..8]);
    }
    manifest.files[0].blocks = Some(base64::engine::general_purpose::STANDARD.encode(digests));
    let server = Server::start(manifest, bytes.clone(), Behavior::InterruptEveryBlock);
    let root = TempDir::new().unwrap();
    prepare(&root);
    fs::write(root.path().join("game/game.bin"), vec![0; bytes.len()]).unwrap();
    let outcome = TransferEngine::new()
        .unwrap()
        .run(&server.request(&root), &TransferControl::default(), &|_| {})
        .unwrap();
    assert_eq!(fs::read(root.path().join("game/game.bin")).unwrap(), bytes);
    assert_eq!(outcome.network_bytes, bytes.len() as u64);
    let ranges = server.ranges.lock().unwrap();
    assert_eq!(ranges.len(), 8);
    for (index, range) in ranges.iter().enumerate() {
        assert_eq!(
            range.as_deref(),
            Some(format!("bytes={}-{}", index * length, bytes.len() - 1).as_str())
        );
    }
}

#[test]
fn block_delta_reuses_shifted_old_blocks_and_fetches_only_missing_block() {
    let length = 64 * 1024;
    let bytes = data(length * 4);
    let mut m = manifest(&bytes);
    m.block_size = length as u64;
    let mut digests = Vec::new();
    for block in bytes.chunks(length) {
        let weak = block.iter().fold(0u64, |h, b| {
            h.wrapping_mul(0x100000001b3).wrapping_add(*b as u64)
        });
        digests.extend(weak.to_le_bytes());
        digests.extend(&Sha256::digest(block)[..8]);
    }
    m.files[0].blocks = Some(base64::engine::general_purpose::STANDARD.encode(digests));
    let server = Server::start(m, bytes.clone(), Behavior::Normal);
    let root = TempDir::new().unwrap();
    prepare(&root);
    let mut old = b"inserted-prefix".to_vec();
    old.extend(&bytes[..length]);
    old.extend(vec![0x55; length]);
    old.extend(&bytes[length * 2..]);
    fs::write(root.path().join("game/game.bin"), old).unwrap();
    let outcome = TransferEngine::new()
        .unwrap()
        .run(&server.request(&root), &TransferControl::default(), &|_| {})
        .unwrap();
    assert_eq!(fs::read(root.path().join("game/game.bin")).unwrap(), bytes);
    assert_eq!(outcome.network_bytes, length as u64);
    assert_eq!(outcome.reused_bytes, (length * 3) as u64);
    assert_eq!(
        server.ranges.lock().unwrap()[0].as_deref(),
        Some("bytes=65536-131071")
    );
}

#[test]
fn insufficient_space_fails_before_update_marker() {
    let bytes = data(5);
    let mut m = manifest(&bytes);
    m.files[0].size = 8_000_000_000_000_000;
    let server = Server::start(m, bytes, Behavior::Normal);
    let root = TempDir::new().unwrap();
    assert!(matches!(
        TransferEngine::new().unwrap().run(
            &server.request(&root),
            &TransferControl::default(),
            &|_| {}
        ),
        Err(TransferError::NoSpace { .. })
    ));
    assert!(!root.path().join("game/.updating").exists());
}

#[test]
fn pause_stops_disk_progress_until_resume() {
    let control = TransferControl::default();
    control.pause();
    let done = Arc::new(AtomicBool::new(false));
    let observed = done.clone();
    let worker_control = control.clone();
    let worker = thread::spawn(move || {
        worker_control.checkpoint().unwrap();
        observed.store(true, Ordering::Release);
    });
    thread::sleep(Duration::from_millis(30));
    assert!(!done.load(Ordering::Acquire));
    control.resume();
    worker.join().unwrap();
    assert!(done.load(Ordering::Acquire));
}

#[test]
fn live_speed_limit_applies_to_active_and_previously_queued_http_downloads() {
    let bytes = data(400_000);
    let server = Server::start(manifest(&bytes), bytes.clone(), Behavior::Normal);
    let state = TempDir::new().unwrap();
    let first_root = TempDir::new().unwrap();
    let second_root = TempDir::new().unwrap();
    let queue = DownloadQueue::new(state.path().join("queue.json")).unwrap();
    let mut first = server.request(&first_root);
    first.bytes_per_second = 1;
    let first_id = queue.enqueue(first).unwrap();
    let mut second = server.request(&second_root);
    second.bytes_per_second = 1;
    let second_id = queue.enqueue(second).unwrap();
    let deadline = std::time::Instant::now() + Duration::from_secs(5);
    while server.ranges.lock().unwrap().is_empty() && std::time::Instant::now() < deadline {
        thread::sleep(Duration::from_millis(5));
    }
    assert!(!server.ranges.lock().unwrap().is_empty());
    queue.set_speed_limit(0);
    let deadline = std::time::Instant::now() + Duration::from_secs(5);
    loop {
        let items = queue.snapshot();
        if [&first_id, &second_id].iter().all(|id| {
            items
                .iter()
                .any(|item| &item.id == *id && item.state == QueueState::Completed)
        }) {
            // Updating a setting must not mutate the durable intent or its integrity fields.
            assert!(items.iter().all(|item| item.request.bytes_per_second == 1));
            break;
        }
        assert!(
            std::time::Instant::now() < deadline,
            "speed override did not reach both jobs: {items:?}"
        );
        thread::sleep(Duration::from_millis(10));
    }
    assert_eq!(
        fs::read(first_root.path().join("game/game.bin")).unwrap(),
        bytes
    );
    assert_eq!(
        fs::read(second_root.path().join("game/game.bin")).unwrap(),
        bytes
    );
}

#[test]
fn hardlinked_partial_cannot_truncate_file_outside_game() {
    let bytes = data(20);
    let server = Server::start(manifest(&bytes), bytes, Behavior::Normal);
    let root = TempDir::new().unwrap();
    prepare(&root);
    let outside = root.path().join("precious.txt");
    fs::write(&outside, b"precious contents longer than download").unwrap();
    fs::hard_link(&outside, root.path().join("game/game.bin.part")).unwrap();
    assert!(
        TransferEngine::new()
            .unwrap()
            .run(&server.request(&root), &TransferControl::default(), &|_| {})
            .is_err()
    );
    assert_eq!(
        fs::read(&outside).unwrap(),
        b"precious contents longer than download"
    );
}

#[test]
fn persistent_queue_pauses_restored_work_and_resumes_partial() {
    let bytes = data(2_000_000);
    let server = Server::start(manifest(&bytes), bytes.clone(), Behavior::Slow);
    let root = TempDir::new().unwrap();
    let state_file = root.path().join("queue.json");
    let queue = DownloadQueue::new(&state_file).unwrap();
    let id = queue.enqueue(server.request(&root)).unwrap();
    wait_until(|| {
        queue.snapshot().iter().any(|i| {
            i.id == id
                && i.progress
                    .as_ref()
                    .is_some_and(|p| p.stage == TransferStage::Downloading && p.completed_bytes > 0)
        })
    });
    queue.pause(&id).unwrap();
    wait_until(|| {
        queue
            .snapshot()
            .iter()
            .any(|i| i.id == id && i.state == QueueState::Paused)
    });
    queue.shutdown();
    drop(queue);
    let previous_requests = server.ranges.lock().unwrap().len();
    let queue = DownloadQueue::new(&state_file).unwrap();
    assert_eq!(queue.snapshot()[0].state, QueueState::Paused);
    thread::sleep(Duration::from_millis(40));
    assert_eq!(server.ranges.lock().unwrap().len(), previous_requests);
    queue.resume(&id).unwrap();
    wait_until(|| {
        queue
            .snapshot()
            .iter()
            .any(|i| i.id == id && i.state == QueueState::Completed)
    });
    queue.shutdown();
    assert_eq!(fs::read(root.path().join("game/game.bin")).unwrap(), bytes);
}

fn wait_until(mut condition: impl FnMut() -> bool) {
    let start = std::time::Instant::now();
    while !condition() {
        assert!(start.elapsed() < Duration::from_secs(8), "timed out");
        thread::sleep(Duration::from_millis(10));
    }
}

#[test]
fn modpack_dependency_runs_only_after_successful_game_install() {
    let base_bytes = data(40_000);
    let mod_bytes = data(70_000);
    let base = Server::start(manifest(&base_bytes), base_bytes, Behavior::Normal);
    let mut mods_manifest = manifest(&mod_bytes);
    mods_manifest.files[0].path = "mod.bin".into();
    let mods = Server::start(mods_manifest, mod_bytes.clone(), Behavior::Normal);
    let root = TempDir::new().unwrap();
    let queue = DownloadQueue::new(root.path().join("queue.json")).unwrap();
    let base_id = queue.enqueue(base.request(&root)).unwrap();
    let mut request = mods.request(&root);
    request.scope = SyncScope::OwnFilesOnly;
    request.depends_on = Some(base_id.clone());
    let mod_id = queue.enqueue(request).unwrap();
    wait_until(|| {
        queue
            .snapshot()
            .iter()
            .any(|i| i.id == mod_id && i.state == QueueState::Completed)
    });
    assert_eq!(
        queue
            .snapshot()
            .iter()
            .find(|i| i.id == base_id)
            .unwrap()
            .state,
        QueueState::Completed
    );
    queue.shutdown();
    assert_eq!(
        fs::read(root.path().join("game/mod.bin")).unwrap(),
        mod_bytes
    );
    assert!(root.path().join("game/game.bin").exists());
}

#[test]
fn failed_or_missing_dependency_keeps_mods_paused_even_after_resume() {
    let bytes = data(20_000);
    let base = Server::start(manifest(&bytes), bytes.clone(), Behavior::Corrupt);
    let mods = Server::start(manifest(&bytes), bytes, Behavior::Normal);
    let root = TempDir::new().unwrap();
    let queue = DownloadQueue::new(root.path().join("queue.json")).unwrap();
    let base_id = queue.enqueue(base.request(&root)).unwrap();
    let mut request = mods.request(&root);
    request.scope = SyncScope::OwnFilesOnly;
    request.depends_on = Some(base_id);
    let mod_id = queue.enqueue(request).unwrap();
    wait_until(|| {
        queue
            .snapshot()
            .iter()
            .any(|i| i.id == mod_id && i.state == QueueState::Paused)
    });
    queue.resume(&mod_id).unwrap();
    wait_until(|| {
        queue
            .snapshot()
            .iter()
            .any(|i| i.id == mod_id && i.state == QueueState::Paused)
    });
    assert!(mods.ranges.lock().unwrap().is_empty());
    let other = TempDir::new().unwrap();
    let mut request = mods.request(&other);
    request.depends_on = Some("missing".into());
    let missing_id = queue.enqueue(request).unwrap();
    wait_until(|| {
        queue
            .snapshot()
            .iter()
            .any(|i| i.id == missing_id && i.state == QueueState::Paused)
    });
    assert!(mods.ranges.lock().unwrap().is_empty());
    queue.shutdown();
}

#[cfg(unix)]
#[test]
fn symlink_parent_is_rejected_and_outside_is_untouched() {
    use std::os::unix::fs::symlink;
    let bytes = data(100);
    let server = Server::start(manifest(&bytes), bytes, Behavior::Normal);
    let root = TempDir::new().unwrap();
    let outside = TempDir::new().unwrap();
    symlink(outside.path(), root.path().join("game")).unwrap();
    assert!(
        TransferEngine::new()
            .unwrap()
            .run(&server.request(&root), &TransferControl::default(), &|_| {})
            .is_err()
    );
    assert_eq!(fs::read_dir(outside.path()).unwrap().count(), 0);
}
