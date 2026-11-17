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
        atomic::{AtomicBool, AtomicUsize, Ordering},
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
    ManifestUnavailableOnce,
    ManifestUnavailable,
    ManifestNotFound,
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
    manifest_requests: Arc<AtomicUsize>,
}
impl Server {
    fn start(manifest: Manifest, data: Vec<u8>, behavior: Behavior) -> Self {
        let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let address = server.server_addr().to_string();
        let stop = Arc::new(AtomicBool::new(false));
        let stopped = stop.clone();
        let ranges = Arc::new(Mutex::new(Vec::new()));
        let seen = ranges.clone();
        let manifest_requests = Arc::new(AtomicUsize::new(0));
        let seen_manifests = manifest_requests.clone();
        let worker = thread::spawn(move || {
            let mut content_requests = 0;
            while !stopped.load(Ordering::Relaxed) {
                let Some(request) = server.recv_timeout(Duration::from_millis(20)).unwrap() else {
                    continue;
                };
                if request.url().split('?').next() == Some("/manifest") {
                    let previous = seen_manifests.fetch_add(1, Ordering::Relaxed);
                    let status = match behavior {
                        Behavior::ManifestUnavailableOnce if previous == 0 => 503,
                        Behavior::ManifestUnavailable => 503,
                        Behavior::ManifestNotFound => 404,
                        _ => 200,
                    };
                    let response =
                        tiny_http::Response::from_data(serde_json::to_vec(&manifest).unwrap())
                            .with_status_code(status);
                    let _ = request.respond(response);
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
            manifest_requests,
        }
    }
    fn request(&self, root: &TempDir) -> TransferRequest {
        TransferRequest {
            game_id: "test".into(),
            version: "2".into(),
            root: root.path().join("game"),
            manifest_url: format!("{}/manifest", self.base),
            content_base_url: format!("{}/content", self.base),
            approved_cleanup_files: vec![],
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
fn manifest_http_retries_transient_status_once_and_never_retries_not_found() {
    let engine = TransferEngine::new().unwrap();
    let control = TransferControl::default();
    for (behavior, expected_requests, expected_version) in [
        (Behavior::ManifestUnavailableOnce, 2, Some("2")),
        (Behavior::ManifestUnavailable, 2, None),
        (Behavior::ManifestNotFound, 1, None),
    ] {
        let server = Server::start(manifest(b"game"), b"game".to_vec(), behavior);
        let result = engine.fetch_manifest(&format!("{}/manifest", server.base), &control);
        match expected_version {
            Some(version) => assert_eq!(result.unwrap().version, version),
            None => assert!(matches!(result, Err(TransferError::Network(_)))),
        }
        assert_eq!(
            server.manifest_requests.load(Ordering::Relaxed),
            expected_requests
        );
    }
}

#[test]
fn manifest_validation_failure_is_not_retried() {
    let mut invalid = manifest(b"game");
    invalid.files[0].path = "../game.bin".into();
    let server = Server::start(invalid, b"game".to_vec(), Behavior::Normal);
    assert!(matches!(
        TransferEngine::new().unwrap().fetch_manifest(
            &format!("{}/manifest", server.base),
            &TransferControl::default()
        ),
        Err(TransferError::InvalidManifest(_))
    ));
    assert_eq!(server.manifest_requests.load(Ordering::Relaxed), 1);
}

#[test]
fn manifest_http_diagnostics_log_attempts_and_status_without_the_signed_url() {
    let signature = "test-secret-manifest-signature-41af";
    for (behavior, status, retries) in [
        (Behavior::ManifestUnavailable, 503, &[true, false][..]),
        (Behavior::ManifestNotFound, 404, &[false][..]),
    ] {
        let server = Server::start(manifest(b"game"), b"game".to_vec(), behavior);
        let temp = TempDir::new().unwrap();
        let queue = DownloadQueue::new(temp.path().join("queue.json")).unwrap();
        let mut request = server.request(&temp);
        request.manifest_url.push_str("?signature=");
        request.manifest_url.push_str(signature);
        let signed_url = request.manifest_url.clone();
        let id = queue.enqueue(request).unwrap();
        wait_until(|| {
            queue
                .snapshot()
                .iter()
                .any(|item| item.id == id && item.state == QueueState::Failed)
        });
        let report = queue
            .snapshot()
            .into_iter()
            .find(|item| item.id == id)
            .unwrap();
        queue.shutdown();
        assert_eq!(report.report.error_code.as_deref(), Some("network"));
        let expected_error = TransferError::Network(String::new()).to_string();
        assert_eq!(report.error.as_deref(), Some(expected_error.as_str()));
        let log = fs::read_to_string(temp.path().join("logs").join("launcher.log")).unwrap();
        assert!(!log.contains(signature));
        assert!(!log.contains(&signed_url));
        assert!(!log.contains(&server.base));
        let events: Vec<_> = log
            .lines()
            .filter_map(|line| line.split_once(" [event] ").map(|(_, value)| value))
            .map(|value| serde_json::from_str::<serde_json::Value>(value).unwrap())
            .filter(|value| value["event"] == "download.manifest_http_error")
            .collect();
        assert_eq!(events.len(), retries.len());
        for (index, (event, retry)) in events.iter().zip(retries).enumerate() {
            assert_eq!(
                event["fields"],
                serde_json::json!({
                    "attempt": index + 1, "cause": "http", "status": status, "retry": retry
                })
            );
        }
    }
}

#[test]
fn manifest_retry_respects_cancellation_after_the_first_request() {
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let url = format!("http://{}/manifest", server.server_addr());
    let engine = TransferEngine::new().unwrap();
    let control = TransferControl::default();
    thread::scope(|scope| {
        scope.spawn(|| {
            let request = server
                .recv_timeout(Duration::from_secs(5))
                .unwrap()
                .unwrap();
            control.cancel();
            request.respond(tiny_http::Response::empty(503)).unwrap();
        });
        assert!(matches!(
            engine.fetch_manifest(&url, &control),
            Err(TransferError::Cancelled)
        ));
    });
}

#[test]
fn parallel_files_keep_one_download_stage_and_publish_final_network_totals() {
    let bytes = data(4096);
    let mut manifest = manifest(&bytes);
    let sample = manifest.files.pop().unwrap();
    for index in 0..96 {
        manifest.files.push(ManifestFile {
            path: format!("data/{index:03}.bin"),
            ..sample.clone()
        });
    }
    let temp = TempDir::new().unwrap();
    let server = Server::start(manifest, bytes, Behavior::Normal);
    let mut request = server.request(&temp);
    request.max_parallel = 8;
    let events = Mutex::new(Vec::new());
    let result = TransferEngine::new()
        .unwrap()
        .run(&request, &TransferControl::default(), &|event| {
            events.lock().unwrap().push(event);
        })
        .unwrap();
    let events = events.into_inner().unwrap();
    let mut stages: Vec<_> = events.iter().map(|event| event.stage).collect();
    stages.dedup();
    assert_eq!(
        stages,
        [
            TransferStage::Checking,
            TransferStage::Downloading,
            TransferStage::Activating,
            TransferStage::Completed
        ]
    );
    assert!(
        events
            .windows(2)
            .all(|pair| pair[0].network_bytes <= pair[1].network_bytes)
    );
    let final_event = events.last().unwrap();
    assert_eq!(final_event.completed_files, 96);
    assert_eq!(final_event.completed_bytes, 96 * 4096);
    assert_eq!(final_event.network_bytes, result.network_bytes);
}

#[test]
fn largest_file_starts_in_the_first_worker_wave_and_resumes_after_pause() {
    let payloads = [
        ("small-a.bin", data(512)),
        ("small-b.bin", data(256)),
        ("small-c.bin", data(128)),
        ("archive.bin", data(16 * 1024)),
    ];
    let files = payloads
        .iter()
        .map(|(path, bytes)| ManifestFile {
            path: (*path).into(),
            size: bytes.len() as u64,
            sha256: Some(format!("{:x}", Sha256::digest(bytes))),
            blake3: blake3::hash(bytes).to_hex().to_string(),
            ..Default::default()
        })
        .collect();
    let manifest = Manifest {
        files,
        ..Default::default()
    };
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let base = format!("http://{}", server.server_addr());
    let temp = TempDir::new().unwrap();
    let request = TransferRequest {
        root: temp.path().join("game"),
        manifest_url: format!("{base}/manifest"),
        content_base_url: format!("{base}/content/"),
        max_parallel: 2,
        ..Default::default()
    };
    let control = TransferControl::default();
    let engine = TransferEngine::new().unwrap();
    let plan = engine
        .plan_manifest(&request, manifest, &control, &|_| {})
        .unwrap();
    let manifest_order = payloads.each_ref().map(|(path, _)| *path);
    let planned_paths = || {
        plan.downloads
            .iter()
            .map(|file| file.path.as_str())
            .collect::<Vec<_>>()
    };
    assert_eq!(planned_paths(), manifest_order);

    thread::scope(|scope| {
        let observer = scope.spawn(|| {
            let receive = || {
                server
                    .recv_timeout(Duration::from_secs(5))
                    .unwrap()
                    .unwrap()
            };
            let reply = |request: tiny_http::Request| {
                let name = request.url().strip_prefix("/content/").unwrap();
                let (_, bytes) = payloads.iter().find(|(path, _)| *path == name).unwrap();
                request.respond(tiny_http::Response::from_data(bytes.clone()))
            };
            // Hold both responses until two requests are in flight, so the sample
            // is the first worker wave regardless of OS thread start order.
            let first = [receive(), receive()];
            let first_paths = first.each_ref().map(|request| request.url().to_owned());
            control.pause();
            let first_replies = first.into_iter().try_for_each(&reply);
            let queued_while_paused = server.recv_timeout(Duration::from_millis(100));
            let activated_while_paused = payloads
                .iter()
                .any(|(path, _)| request.root.join(path).exists());
            control.resume();
            first_replies.unwrap();
            assert!(queued_while_paused.unwrap().is_none());
            assert!(!activated_while_paused);
            for _ in 2..payloads.len() {
                reply(receive()).unwrap();
            }
            first_paths
        });
        let outcome = engine.execute(&request, &plan, &control, &|_| {}).unwrap();
        let first_paths = observer.join().unwrap();
        assert!(
            first_paths
                .iter()
                .any(|path| path == "/content/archive.bin")
        );
        assert_eq!(outcome.downloaded_files, payloads.len());
        assert_eq!(
            outcome.network_bytes,
            payloads
                .iter()
                .map(|(_, bytes)| bytes.len() as u64)
                .sum::<u64>()
        );
    });
    assert_eq!(planned_paths(), manifest_order);
    for (path, bytes) in payloads {
        assert_eq!(fs::read(request.root.join(path)).unwrap(), bytes);
    }
}

#[test]
fn disk_plan_counts_fresh_bytes_and_only_concurrent_replacement_overlap() {
    struct SpaceCase {
        name: &'static str,
        sizes: &'static [(u8, u8)],
        parallel: usize,
        expected_units: u64,
    }

    const UNIT: usize = 4096;
    let engine = TransferEngine::new().unwrap();
    let control = TransferControl::default();
    let cases = [
        SpaceCase {
            name: "fresh",
            sizes: &[(20, 0), (30, 0), (50, 0)],
            parallel: 3,
            expected_units: 100,
        },
        SpaceCase {
            name: "all shrink",
            sizes: &[(30, 100), (22, 100), (50, 100)],
            parallel: 2,
            expected_units: 80,
        },
        SpaceCase {
            name: "growth before shrink",
            sizes: &[(33, 2), (28, 0), (20, 101)],
            parallel: 1,
            expected_units: 79,
        },
        SpaceCase {
            name: "serial shrink releases space for a later file",
            sizes: &[(10, 100), (9, 0)],
            parallel: 1,
            expected_units: 10,
        },
        SpaceCase {
            name: "serial estimate follows the descending size schedule",
            sizes: &[(9, 0), (10, 100)],
            parallel: 1,
            expected_units: 10,
        },
        SpaceCase {
            name: "equal-size serial replacements keep manifest order",
            sizes: &[(10, 0), (10, 100)],
            parallel: 1,
            expected_units: 20,
        },
        SpaceCase {
            name: "serial growth is included before a later shrink",
            sizes: &[(30, 10), (20, 50), (19, 0)],
            parallel: 1,
            expected_units: 40,
        },
        SpaceCase {
            name: "parallel shrink does not promise an early release",
            sizes: &[(10, 100), (9, 0)],
            parallel: 2,
            expected_units: 19,
        },
        SpaceCase {
            name: "mixed three workers",
            sizes: &[(31, 1), (30, 9), (5, 7), (10, 11), (8, 0)],
            parallel: 3,
            expected_units: 83,
        },
    ];
    for SpaceCase {
        name: case,
        sizes,
        parallel,
        expected_units,
    } in cases
    {
        let temp = TempDir::new().unwrap();
        let root = temp.path().join("game");
        if case != "fresh" {
            fs::create_dir(&root).unwrap();
            fs::write(root.join("preserved.bin"), b"keep settings").unwrap();
        }
        let mut files = Vec::new();
        for (index, &(new, old)) in sizes.iter().enumerate() {
            let path = format!("{index}.bin");
            if old > 0 {
                fs::write(root.join(&path), vec![0; old as usize * UNIT]).unwrap();
            }
            files.push(ManifestFile {
                path,
                size: u64::from(new) * UNIT as u64,
                blake3: blake3::hash(&vec![1; new as usize * UNIT])
                    .to_hex()
                    .to_string(),
                ..Default::default()
            });
        }
        if case != "fresh" {
            files.push(ManifestFile {
                path: "preserved.bin".into(),
                size: 332,
                blake3: "00".repeat(32),
                ..Default::default()
            });
        }
        let request = TransferRequest {
            root,
            manifest_url: "http://127.0.0.1/manifest".into(),
            content_base_url: "http://127.0.0.1/content/".into(),
            preserve_paths: vec!["preserved.bin".into()],
            max_parallel: parallel,
            ..Default::default()
        };
        let plan = engine
            .plan_manifest(
                &request,
                Manifest {
                    files,
                    ..Default::default()
                },
                &control,
                &|_| {},
            )
            .unwrap();
        assert_eq!(plan.downloads.len(), sizes.len(), "{case}");
        assert!(plan.to_delete.is_empty(), "{case}");
        assert_eq!(
            plan.required_free_bytes,
            expected_units * UNIT as u64,
            "{case}"
        );
    }
}

#[test]
fn physically_allocated_partial_reduces_required_space_for_resume() {
    let temp = TempDir::new().unwrap();
    prepare(&temp);
    let bytes = data(1024 * 1024);
    let server = Server::start(manifest(&bytes), bytes.clone(), Behavior::Normal);
    let request = server.request(&temp);
    let prefix = 128 * 1024;
    fs::write(request.root.join("game.bin.part"), &bytes[..prefix]).unwrap();
    let engine = TransferEngine::new().unwrap();
    let control = TransferControl::default();
    let plan = engine.plan(&request, &control, &|_| {}).unwrap();
    assert_eq!(plan.required_free_bytes, (bytes.len() - prefix) as u64);
    assert!(plan.to_delete.is_empty());
    let outcome = engine.execute(&request, &plan, &control, &|_| {}).unwrap();
    assert_eq!(outcome.network_bytes, plan.required_free_bytes);
    assert_eq!(fs::read(request.root.join("game.bin")).unwrap(), bytes);
}

#[test]
fn stale_resumed_prefix_requires_complete_retry_before_promotion() {
    let temp = TempDir::new().unwrap();
    prepare(&temp);
    let bytes = data(1024 * 1024);
    let server = Server::start(manifest(&bytes), bytes.clone(), Behavior::Normal);
    let request = server.request(&temp);
    let prefix = 128 * 1024;
    fs::write(request.root.join("game.bin.part"), vec![0; prefix]).unwrap();
    let outcome = TransferEngine::new()
        .unwrap()
        .run(&request, &TransferControl::default(), &|_| {})
        .unwrap();
    assert_eq!(fs::read(request.root.join("game.bin")).unwrap(), bytes);
    assert_eq!(outcome.network_bytes, (2 * bytes.len() - prefix) as u64);
    assert_eq!(
        *server.ranges.lock().unwrap(),
        vec![Some(format!("bytes={prefix}-")), None]
    );
}

#[test]
fn multiple_games_reorder_pause_cancel_then_restore_and_resume() {
    let temp = TempDir::new().unwrap();
    let mut jobs = Vec::new();
    for (name, size) in [
        ("first", 4 * 1024 * 1024),
        ("cancelled", 64 * 1024),
        ("priority", 2 * 1024 * 1024),
    ] {
        let bytes = data(size);
        let mut manifest = manifest(&bytes);
        manifest.game_id = name.into();
        let server = Server::start(manifest, bytes, Behavior::Slow);
        let mut request = server.request(&temp);
        request.game_id = name.into();
        request.root = temp.path().join(name);
        jobs.push((server, request));
    }
    let path = temp.path().join("queue.json");
    let queue = DownloadQueue::new(&path).unwrap();
    let first = queue.enqueue(jobs[0].1.clone()).unwrap();
    let part = jobs[0].1.root.join("game.bin.part");
    wait_until(|| fs::metadata(&part).is_ok_and(|meta| meta.len() >= 256 * 1024));
    let cancelled = queue.enqueue(jobs[1].1.clone()).unwrap();
    queue.cancel(&cancelled).unwrap();
    let priority = queue.enqueue(jobs[2].1.clone()).unwrap();
    queue.move_before(&priority, Some(&first)).unwrap();
    let state = |queue: &DownloadQueue, id: &str| {
        queue
            .snapshot()
            .into_iter()
            .find(|item| item.id == id)
            .unwrap()
            .state
    };
    wait_until(|| state(&queue, &priority) == QueueState::Running);
    queue.pause(&first).unwrap();
    wait_until(|| state(&queue, &priority) == QueueState::Completed);
    assert_eq!(state(&queue, &first), QueueState::Paused);
    assert_eq!(state(&queue, &cancelled), QueueState::Cancelled);
    assert!(jobs[1].0.ranges.lock().unwrap().is_empty());
    assert!(part.is_file());
    drop(queue);

    let queue = DownloadQueue::new(path).unwrap();
    assert_eq!(state(&queue, &first), QueueState::Paused);
    assert_eq!(state(&queue, &cancelled), QueueState::Cancelled);
    assert_eq!(state(&queue, &priority), QueueState::Completed);
    queue.resume(&first).unwrap();
    wait_until(|| state(&queue, &first) == QueueState::Completed);
    assert!(jobs[0].0.ranges.lock().unwrap().iter().any(Option::is_some));
    assert!(!part.exists());
    assert_eq!(
        fs::read(jobs[0].1.root.join("game.bin")).unwrap(),
        data(4 * 1024 * 1024)
    );
    assert_eq!(
        fs::read(jobs[2].1.root.join("game.bin")).unwrap(),
        data(2 * 1024 * 1024)
    );
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
    let mut request = server.request(&root);
    let engine = TransferEngine::new().unwrap();
    let control = TransferControl::default();
    let plan = engine.plan(&request, &control, &|_| {}).unwrap();
    request.approved_cleanup_files = plan.to_delete.clone();
    assert!(engine.execute(&request, &plan, &control, &|_| {}).is_err());
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
    request.approved_cleanup_files.clear();
    request.foreign_paths = vec!["mod.dll".into()];
    let engine = TransferEngine::new().unwrap();
    let result = engine.run(&request, &TransferControl::default(), &|_| {});
    let Err(TransferError::NeedsCleanupConfirmation(files)) = result else {
        panic!("expected cleanup confirmation: {result:?}");
    };
    assert_eq!(
        files
            .iter()
            .map(|file| file.path.as_str())
            .collect::<Vec<_>>(),
        ["old.bin"]
    );
    assert!(!root.path().join("game/game.bin").exists());
    assert!(!root.path().join("game/.updating").exists());
    request.approved_cleanup_files = files;
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
    let engine = TransferEngine::new().unwrap();
    let control = TransferControl::default();
    let plan = engine.plan(&request, &control, &|_| {}).unwrap();
    request.approved_cleanup_files = plan.to_delete.clone();
    let outcome = engine.execute(&request, &plan, &control, &|_| {}).unwrap();
    assert_eq!(outcome.network_bytes, 0);
    assert_eq!(outcome.reused_bytes, bytes.len() as u64);
    assert!(root.path().join("game/steamgame.exe").exists());
    assert!(root.path().join("game/doorstop_config.ini").exists());
    assert!(!root.path().join("game/oldmod.dll").exists());
    assert!(root.path().join("game/.mods.manifest.json").exists());
    assert!(!root.path().join("game/.version").exists());
}

#[test]
fn cleanup_consent_cannot_expand_when_the_queue_replans_or_restarts() {
    let bytes = data(100);
    let server = Server::start(manifest(&bytes), bytes, Behavior::Normal);
    let root = TempDir::new().unwrap();
    prepare(&root);
    fs::write(root.path().join("game/old-extra.txt"), b"old").unwrap();
    let state_file = root.path().join("queue.json");
    let queue = DownloadQueue::new(&state_file).unwrap();
    let id = queue.enqueue(server.request(&root)).unwrap();
    wait_until(|| queue.snapshot()[0].state == QueueState::NeedsConfirmation);
    let displayed = queue.snapshot()[0].cleanup_files.clone();
    assert_eq!(
        displayed
            .iter()
            .map(|file| file.path.as_str())
            .collect::<Vec<_>>(),
        ["old-extra.txt"]
    );
    fs::write(root.path().join("game/new-save.txt"), b"new save").unwrap();
    queue.confirm_cleanup(&id, &displayed).unwrap();
    wait_until(|| queue.snapshot()[0].state == QueueState::NeedsConfirmation);
    assert!(root.path().join("game/old-extra.txt").exists());
    assert_eq!(
        fs::read(root.path().join("game/new-save.txt")).unwrap(),
        b"new save"
    );
    assert_eq!(
        queue.snapshot()[0]
            .cleanup_files
            .iter()
            .map(|file| file.path.as_str())
            .collect::<Vec<_>>(),
        ["new-save.txt", "old-extra.txt"]
    );
    queue.shutdown();
    drop(queue);
    let restored = DownloadQueue::new(&state_file).unwrap();
    fs::write(root.path().join("game/third-save.txt"), b"third").unwrap();
    restored
        .confirm_cleanup(&id, &restored.snapshot()[0].cleanup_files)
        .unwrap();
    wait_until(|| restored.snapshot()[0].state == QueueState::NeedsConfirmation);
    assert_eq!(
        fs::read(root.path().join("game/third-save.txt")).unwrap(),
        b"third"
    );
    restored
        .confirm_cleanup(&id, &restored.snapshot()[0].cleanup_files)
        .unwrap();
    wait_until(|| restored.snapshot()[0].state == QueueState::Completed);
    assert!(!root.path().join("game/old-extra.txt").exists());
    assert!(!root.path().join("game/new-save.txt").exists());
    assert!(!root.path().join("game/third-save.txt").exists());
}

#[test]
fn legacy_boolean_and_path_only_cleanup_approval_never_authorize_deletion() {
    let mut value = serde_json::to_value(TransferRequest::default()).unwrap();
    value
        .as_object_mut()
        .unwrap()
        .remove("approved_cleanup_files");
    value["allow_cleanup"] = true.into();
    value["approved_cleanup_paths"] = serde_json::json!(["old.dat"]);
    let request: TransferRequest = serde_json::from_value(value).unwrap();
    assert!(request.approved_cleanup_files.is_empty());
}

#[test]
fn a_failed_attempt_cannot_reuse_consent_for_a_recreated_save() {
    let bytes = data(100);
    let server = Server::start(manifest(&bytes), bytes, Behavior::Corrupt);
    let root = TempDir::new().unwrap();
    prepare(&root);
    let save = root.path().join("game/save.dat");
    fs::write(&save, b"old save").unwrap();
    let queue = DownloadQueue::new(root.path().join("queue.json")).unwrap();
    let id = queue.enqueue(server.request(&root)).unwrap();
    wait_until(|| queue.snapshot()[0].state == QueueState::NeedsConfirmation);
    queue
        .confirm_cleanup(&id, &queue.snapshot()[0].cleanup_files)
        .unwrap();
    wait_until(|| queue.snapshot()[0].state == QueueState::Failed);
    assert!(
        queue.snapshot()[0]
            .request
            .approved_cleanup_files
            .is_empty()
    );
    fs::write(&save, b"new save from another game session").unwrap();
    queue.resume(&id).unwrap();
    wait_until(|| queue.snapshot()[0].state == QueueState::NeedsConfirmation);
    assert_eq!(
        fs::read(save).unwrap(),
        b"new save from another game session"
    );
    queue.shutdown();
}

#[test]
fn a_save_replaced_at_the_same_path_between_preview_and_confirmation_is_protected() {
    let bytes = data(100);
    let server = Server::start(manifest(&bytes), bytes, Behavior::Normal);
    let root = TempDir::new().unwrap();
    prepare(&root);
    let save = root.path().join("game/save.dat");
    fs::write(&save, b"old save").unwrap();
    let queue = DownloadQueue::new(root.path().join("queue.json")).unwrap();
    let id = queue.enqueue(server.request(&root)).unwrap();
    wait_until(|| queue.snapshot()[0].state == QueueState::NeedsConfirmation);
    let first_preview = queue.snapshot()[0].cleanup_files.clone();
    assert_eq!(first_preview[0].path, "save.dat");
    let replacement = root.path().join("new-save.dat");
    fs::write(&replacement, b"new save").unwrap();
    fs::remove_file(&save).unwrap();
    fs::rename(replacement, &save).unwrap();
    queue.confirm_cleanup(&id, &first_preview).unwrap();
    wait_until(|| queue.snapshot()[0].state == QueueState::NeedsConfirmation);
    let current_preview = queue.snapshot()[0].cleanup_files.clone();
    assert_eq!(current_preview[0].path, "save.dat");
    assert_ne!(first_preview, current_preview);
    assert_eq!(fs::read(&save).unwrap(), b"new save");
    assert!(matches!(
        queue.confirm_cleanup(&id, &first_preview),
        Err(TransferError::CleanupConfirmationChanged)
    ));
    queue.confirm_cleanup(&id, &current_preview).unwrap();
    wait_until(|| queue.snapshot()[0].state == QueueState::Completed);
    assert!(!save.exists());
    queue.shutdown();
}

#[test]
fn a_save_edited_during_download_is_never_deleted_under_its_old_approval() {
    let bytes = data(1_500_000);
    let server = Server::start(manifest(&bytes), bytes, Behavior::Slow);
    let root = TempDir::new().unwrap();
    prepare(&root);
    let save = root.path().join("game/save.dat");
    fs::write(&save, b"old save").unwrap();
    let original_time = fs::metadata(&save).unwrap().modified().unwrap();
    let engine = TransferEngine::new().unwrap();
    let control = TransferControl::default();
    let mut request = server.request(&root);
    let plan = engine.plan(&request, &control, &|_| {}).unwrap();
    request.approved_cleanup_files = plan.to_delete.clone();
    let edited = AtomicBool::new(false);
    let result = engine.execute(&request, &plan, &control, &|progress| {
        if progress.stage == TransferStage::Downloading
            && progress.network_bytes > 0
            && progress.completed_bytes < progress.total_bytes
            && !edited.swap(true, Ordering::SeqCst)
        {
            fs::write(&save, b"new save").unwrap();
            fs::OpenOptions::new()
                .write(true)
                .open(&save)
                .unwrap()
                .set_times(fs::FileTimes::new().set_modified(original_time))
                .unwrap();
        }
    });
    assert!(
        edited.load(Ordering::SeqCst),
        "fixture must edit a file during a download"
    );
    let Err(TransferError::NeedsCleanupConfirmation(files)) = result else {
        panic!("expected a new cleanup preview: {result:?}");
    };
    assert_eq!(files[0].path, "save.dat");
    assert_ne!(files, request.approved_cleanup_files);
    assert_eq!(fs::read(&save).unwrap(), b"new save");
    assert!(!root.path().join("game/.version").exists());
    assert!(root.path().join("game/.updating").exists());
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
fn a_cancelled_bundle_resumes_from_either_part() {
    for resume_modpack in [false, true] {
        let base_bytes = data(2 * 1024 * 1024);
        let mod_bytes = data(32_000);
        let base = Server::start(manifest(&base_bytes), base_bytes.clone(), Behavior::Slow);
        let mut mod_manifest = manifest(&mod_bytes);
        mod_manifest.files[0].path = "mod.bin".into();
        let mods = Server::start(mod_manifest, mod_bytes.clone(), Behavior::Normal);
        let temp = TempDir::new().unwrap();
        let path = temp.path().join("queue.json");
        let queue = DownloadQueue::new(&path).unwrap();
        let req = base.request(&temp);
        let part = req.root.join("game.bin.part");
        let base_id = queue.enqueue(req).unwrap();
        let mut request = mods.request(&temp);
        request.scope = SyncScope::OwnFilesOnly;
        request.depends_on = Some(base_id.clone());
        let mod_id = queue.enqueue(request).unwrap();
        wait_until(|| fs::metadata(&part).is_ok_and(|meta| meta.len() > 0));
        queue.cancel(&base_id).unwrap();
        wait_until(|| {
            queue
                .snapshot()
                .iter()
                .any(|item| item.id == mod_id && item.state == QueueState::Paused)
        });
        assert!(mods.ranges.lock().unwrap().is_empty());
        queue.shutdown();

        let queue = DownloadQueue::new(path).unwrap();
        queue
            .resume(if resume_modpack { &mod_id } else { &base_id })
            .unwrap();
        wait_until(|| {
            queue
                .snapshot()
                .iter()
                .all(|item| item.state == QueueState::Completed)
        });
        assert!(base.ranges.lock().unwrap().iter().any(Option::is_some));
        assert_eq!(
            fs::read(temp.path().join("game/game.bin")).unwrap(),
            base_bytes
        );
        assert_eq!(
            fs::read(temp.path().join("game/mod.bin")).unwrap(),
            mod_bytes
        );
    }
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
        base.ranges.lock().unwrap().len() > 3
            && queue
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
    let saved = fs::read(root.path().join("queue.json")).unwrap();
    assert!(queue.resume(&missing_id).is_err());
    assert_eq!(fs::read(root.path().join("queue.json")).unwrap(), saved);
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
