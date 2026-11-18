//! Run with `cargo test --release --no-default-features --test download_benchmark -- --ignored --nocapture`.
//! CHILLHUB_BENCH_FIXTURES preserves the generated data for identical runs across versions.
//! Set CHILLHUB_BENCH_SERVE_ONLY to a port (0 chooses a free one) to host the same fixtures
//! for another client. CHILLHUB_BENCH_BASE runs the benchmark against that server.
//! CHILLHUB_BENCH_SCHEDULE_ONLY runs only the delayed mixed-size fixture.
//! CHILLHUB_BENCH_PACKED=1 compares fresh installs of the same 1,200 small
//! files with and without pack metadata. To use publisher output, place its
//! manifest.json, files/ and packs/ under CHILLHUB_BENCH_FIXTURES/small-packed/.
use base64::Engine as _;
use chillhub_native::{
    manifest::{Manifest, ManifestFile, SmallPack},
    transfer::{TransferControl, TransferEngine, TransferRequest, TransferStage, verify_file},
};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::{Read, Seek, SeekFrom},
    path::Path,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicUsize, Ordering},
    },
    thread,
    time::{Duration, Instant},
};
use tiny_http::{Header, Response, Server, StatusCode};

const BLOCK: usize = 1024 * 1024;
const WORKERS: usize = 8;
const MIXED_ARCHIVE: &str = "archive.bin";

fn payload(size: usize, seed: u32) -> Vec<u8> {
    let mut state = seed;
    (0..size)
        .map(|_| {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            state as u8
        })
        .collect()
}

fn make_fixtures(root: &Path, schedule_only: bool, packed: bool) {
    for (case, file_count, size) in [
        ("small", 1200, 4096),
        ("small-packed", 1200, 4096),
        ("large", 4, 64 * BLOCK),
        ("delta", 2, 48 * BLOCK),
        ("mixed", 49, 4096),
    ] {
        let pack_fixture = case == "small-packed";
        if (schedule_only && case != "mixed")
            || (packed && !matches!(case, "small" | "small-packed"))
            || (!packed && pack_fixture)
        {
            continue;
        }
        let directory = root.join(case);
        let manifest_path = directory.join("manifest.json");
        if manifest_path.is_file() {
            continue;
        }
        let mut manifest = Manifest {
            game_id: format!("bench-{case}"),
            version: "2".into(),
            block_size: if case == "delta" { BLOCK as u64 } else { 0 },
            ..Default::default()
        };
        let mut pack_bytes = Vec::new();
        for index in 0..file_count {
            let archive = case == "mixed" && index + 1 == file_count;
            let size = if archive { 16 * BLOCK } else { size };
            let data = payload(size, 0x2b2a_3321 + index as u32);
            let relative = if archive {
                MIXED_ARCHIVE.into()
            } else {
                format!("dir-{:03}/file-{index:04}.bin", index / 32)
            };
            let path = directory
                .join(if pack_fixture { "files" } else { "content" })
                .join(&relative);
            fs::create_dir_all(path.parent().unwrap()).unwrap();
            fs::write(path, &data).unwrap();
            if pack_fixture {
                pack_bytes.extend_from_slice(&data);
                if (index + 1) % 128 == 0 || index + 1 == file_count {
                    let sha256 = format!("{:x}", Sha256::digest(&pack_bytes));
                    let packs = directory.join("packs");
                    fs::create_dir_all(&packs).unwrap();
                    fs::write(packs.join(format!("{sha256}.bin")), &pack_bytes).unwrap();
                    manifest.small_packs.push(SmallPack {
                        sha256,
                        files: (index / 128 * 128..=index).collect(),
                    });
                    pack_bytes.clear();
                }
            }
            let blocks = (case == "delta").then(|| {
                let mut old = vec![0x55; 33];
                old.extend_from_slice(&data[..size - 33]);
                // Two unavailable full blocks force bounded Range requests; the rest shift.
                old[8 * BLOCK + 33..9 * BLOCK + 33].fill(0xaa);
                let path = directory.join("old").join(&relative);
                fs::create_dir_all(path.parent().unwrap()).unwrap();
                fs::write(path, old).unwrap();
                let mut digests = Vec::new();
                for block in data.chunks(BLOCK) {
                    let weak = block.iter().fold(0u64, |hash, byte| {
                        hash.wrapping_mul(0x100000001b3).wrapping_add(*byte as u64)
                    });
                    digests.extend_from_slice(&weak.to_le_bytes());
                    digests.extend_from_slice(&Sha256::digest(block)[..8]);
                }
                base64::engine::general_purpose::STANDARD.encode(digests)
            });
            manifest.files.push(ManifestFile {
                path: relative,
                size: data.len() as u64,
                sha256: Some(format!("{:x}", Sha256::digest(&data))),
                blake3: blake3::hash(&data).to_hex().to_string(),
                blocks,
                ..Default::default()
            });
        }
        fs::write(manifest_path, serde_json::to_vec(&manifest).unwrap()).unwrap();
    }
}

#[derive(Default)]
struct Requests {
    content: AtomicUsize,
    manifests: AtomicUsize,
    range: AtomicUsize,
}
impl Requests {
    fn take(&self) -> (usize, usize, usize) {
        (
            self.content.swap(0, Ordering::Relaxed),
            self.manifests.swap(0, Ordering::Relaxed),
            self.range.swap(0, Ordering::Relaxed),
        )
    }
}

fn serve(server: &Server, fixtures: &Path, stop: &AtomicBool, requests: &Requests) {
    while !stop.load(Ordering::Relaxed) {
        let Some(request) = server.recv_timeout(Duration::from_millis(20)).unwrap() else {
            continue;
        };
        if request.url() == "/__requests" {
            let _ = request.respond(Response::from_data(
                serde_json::to_vec(&requests.take()).unwrap(),
            ));
            continue;
        }
        let relative = request.url().strip_prefix('/').unwrap();
        chillhub_native::manifest::validate_relative(relative).unwrap();
        let mut file = fs::File::open(fixtures.join(relative)).unwrap();
        let size = file.metadata().unwrap().len();
        let range = request
            .headers()
            .iter()
            .find(|header| header.field.equiv("Range"));
        if relative.ends_with("/manifest.json") {
            requests.manifests.fetch_add(1, Ordering::Relaxed);
        } else {
            requests.content.fetch_add(1, Ordering::Relaxed);
        }
        if let Some(path) = relative.strip_prefix("mixed/content/") {
            // Model the time each request occupies a worker. The archive comes
            // last in the manifest and takes longer than a wave of small files.
            thread::sleep(Duration::from_millis(if path == MIXED_ARCHIVE {
                450
            } else {
                45
            }));
        }
        let (start, length, status, headers) = if let Some(range) = range {
            let (start, end) = range
                .value
                .as_str()
                .strip_prefix("bytes=")
                .unwrap()
                .split_once('-')
                .unwrap();
            let start: u64 = start.parse().unwrap();
            let end: u64 = end.parse().unwrap_or(size - 1);
            requests.range.fetch_add(1, Ordering::Relaxed);
            (
                start,
                end + 1 - start,
                206,
                vec![
                    Header::from_bytes("Content-Range", format!("bytes {start}-{end}/{size}"))
                        .unwrap(),
                ],
            )
        } else {
            (0, size, 200, Vec::new())
        };
        file.seek(SeekFrom::Start(start)).unwrap();
        let response = Response::new(
            StatusCode(status),
            headers,
            file.take(length),
            Some(length as usize),
            None,
        );
        let _ = request.respond(response);
    }
}

#[test]
#[ignore = "release timing benchmark with local HTTP and transient installations"]
fn local_download_benchmark() {
    let schedule_only = std::env::var_os("CHILLHUB_BENCH_SCHEDULE_ONLY").is_some();
    let packed = std::env::var("CHILLHUB_BENCH_PACKED").as_deref() == Ok("1");
    assert!(!(schedule_only && packed), "select one benchmark mode");
    let workspace = tempfile::tempdir().unwrap();
    let fixtures = std::env::var_os("CHILLHUB_BENCH_FIXTURES")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| workspace.path().join("fixtures"));
    make_fixtures(&fixtures, schedule_only, packed);
    if packed {
        let file_list = |case: &str| {
            let bytes = fs::read(fixtures.join(case).join("manifest.json")).unwrap();
            serde_json::from_slice::<Manifest>(&bytes)
                .unwrap()
                .files
                .into_iter()
                .map(|file| (file.path, file.size, file.blake3, file.sha256))
                .collect::<Vec<_>>()
        };
        assert_eq!(
            file_list("small"),
            file_list("small-packed"),
            "benchmark inputs differ"
        );
    }
    if std::env::var_os("CHILLHUB_BENCH_PREPARE_ONLY").is_some() {
        assert!(std::env::var_os("CHILLHUB_BENCH_FIXTURES").is_some());
        return;
    }
    let stop = Arc::new(AtomicBool::new(false));
    let mut servers = Vec::new();
    let serve_only = std::env::var("CHILLHUB_BENCH_SERVE_ONLY").ok();
    let base = std::env::var("CHILLHUB_BENCH_BASE").unwrap_or_else(|_| {
        let port: u16 = serve_only.as_deref().unwrap_or("0").parse().unwrap();
        let server = Arc::new(Server::http(("127.0.0.1", port)).unwrap());
        let requests = Arc::new(Requests::default());
        for _ in 0..WORKERS {
            let (server, fixtures, stop, requests) = (
                server.clone(),
                fixtures.clone(),
                stop.clone(),
                requests.clone(),
            );
            servers.push(thread::spawn(move || {
                serve(&server, &fixtures, &stop, &requests)
            }));
        }
        format!("http://{}", server.server_addr())
    });
    if serve_only.is_some() {
        assert!(!servers.is_empty());
        println!("{}", json!({"baseUrl": base}));
        for server in servers {
            server.join().unwrap();
        }
        return;
    }
    let counts_url = format!("{base}/__requests");
    let inspect = reqwest::blocking::Client::new();
    let take_requests = || {
        inspect
            .get(&counts_url)
            .send()
            .unwrap()
            .error_for_status()
            .unwrap()
            .json::<(usize, usize, usize)>()
            .unwrap()
    };
    take_requests();
    let repetitions = std::env::var("CHILLHUB_BENCH_REPEATS")
        .ok()
        .and_then(|n| n.parse().ok())
        .unwrap_or(1);
    let cases: &[(&str, &str)] = if schedule_only {
        &[("mixed", "mixed")]
    } else if packed {
        &[("small", "small"), ("small-packed", "small-packed")]
    } else {
        &[
            ("small", "small"),
            ("large", "large"),
            ("verify", "large"),
            ("delta", "delta"),
            ("mixed", "mixed"),
        ]
    };
    for repeat in 0..repetitions {
        let roots = tempfile::tempdir_in(workspace.path()).unwrap();
        let engine = TransferEngine::new()
            .unwrap()
            .with_hash_cache_dir(roots.path().join("hash-cache"));
        let control = TransferControl::default();
        for &(case, target) in cases {
            let directory = fixtures.join(target);
            let manifest: Manifest =
                serde_json::from_slice(&fs::read(directory.join("manifest.json")).unwrap())
                    .unwrap();
            let root = roots.path().join(target);
            if case == "delta" {
                for file in &manifest.files {
                    let path = root.join(&file.path);
                    fs::create_dir_all(path.parent().unwrap()).unwrap();
                    fs::copy(directory.join("old").join(&file.path), path).unwrap();
                }
            }
            let request = TransferRequest {
                game_id: manifest.game_id.clone(),
                version: manifest.version.clone(),
                root: root.clone(),
                manifest_url: format!("{base}/{target}/manifest.json"),
                content_base_url: format!(
                    "{base}/{target}/{}/",
                    if case == "small-packed" {
                        "files"
                    } else {
                        "content"
                    }
                ),
                max_parallel: WORKERS,
                force_rehash: case == "verify",
                ..Default::default()
            };
            let checking = AtomicUsize::new(0);
            let downloading = AtomicUsize::new(0);
            let event = |event: chillhub_native::transfer::TransferEvent| match event.stage {
                TransferStage::Checking => {
                    checking.fetch_add(1, Ordering::Relaxed);
                }
                TransferStage::Downloading => {
                    downloading.fetch_add(1, Ordering::Relaxed);
                }
                _ => {}
            };
            let start = Instant::now();
            let plan = engine.prepare_plan(&request, &control, &event).unwrap();
            let planned = Instant::now();
            let outcome = engine.execute(&request, &plan, &control, &event).unwrap();
            let finished = Instant::now();
            let (content, manifests, range) = take_requests();
            assert_eq!(manifests, 1);
            let expected_bytes = match case {
                "verify" => 0,
                "delta" => (2 * BLOCK * manifest.files.len()) as u64,
                _ => manifest.files.iter().map(|file| file.size).sum(),
            };
            assert_eq!(outcome.network_bytes, expected_bytes);
            match case {
                "verify" => assert_eq!(content, 0),
                "delta" => {
                    assert_eq!(content, 2 * manifest.files.len());
                    assert_eq!(range, content);
                    assert_eq!(
                        outcome.reused_bytes + outcome.network_bytes,
                        outcome.total_bytes
                    );
                }
                "small-packed" => {
                    assert!(
                        !manifest.small_packs.is_empty(),
                        "expected a packed fixture"
                    );
                    let grouped: usize = manifest.small_packs.iter().map(|p| p.files.len()).sum();
                    assert_eq!(
                        content,
                        manifest.files.len() - grouped + manifest.small_packs.len()
                    );
                    assert_eq!(range, 0);
                }
                _ => assert_eq!(content, manifest.files.len()),
            }
            assert!(
                manifest.files.iter().all(|file| verify_file(
                    &root.join(&file.path),
                    file,
                    &control
                )
                .unwrap())
            );
            println!(
                "{}",
                json!({
                    "case": case, "repeat": repeat + 1,
                    "planMs": (planned - start).as_secs_f64() * 1000.0,
                    "executeMs": (finished - planned).as_secs_f64() * 1000.0,
                    "totalMs": (finished - start).as_secs_f64() * 1000.0,
                    "networkBytes": outcome.network_bytes, "reusedBytes": outcome.reused_bytes,
                    "contentRequests": content, "rangeRequests": range, "manifestRequests": manifests,
                    "checkingEvents": checking.load(Ordering::Relaxed),
                    "downloadingEvents": downloading.load(Ordering::Relaxed),
                })
            );
        }
    }
    stop.store(true, Ordering::Relaxed);
    for server in servers {
        server.join().unwrap();
    }
}
