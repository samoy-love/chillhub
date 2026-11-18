use chillhub_native::{
    manifest::{Manifest, ManifestFile, SmallPack},
    transfer::{TransferControl, TransferEngine, TransferError, TransferRequest, TransferStage},
};
use sha2::{Digest, Sha256};
use std::{
    fs,
    io::{self, Cursor, Read},
    path::Path,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::Duration,
};
use tiny_http::{Header, Response, StatusCode};

const COUNT: usize = 8;
const SIZE: usize = 4096;
type RequestHistory = Vec<(String, Option<String>)>;

#[derive(Clone, Copy)]
enum Body {
    Good,
    Missing,
    BadPackHash,
    BadFileHash,
    WrongLength { announced: bool, oversized: bool },
    Slow,
    PeerFailure,
}

struct Fixture {
    manifest: Manifest,
    contents: Vec<Vec<u8>>,
    bundle: Vec<u8>,
    mode: Body,
}

impl Fixture {
    fn new(mode: Body) -> Self {
        let contents: Vec<_> = (0..COUNT).map(|index| vec![index as u8; SIZE]).collect();
        let files = contents
            .iter()
            .enumerate()
            .map(|(index, bytes)| ManifestFile {
                path: format!("dir/{index}.dat"),
                size: bytes.len() as u64,
                blake3: blake3::hash(bytes).to_hex().to_string(),
                sha256: Some(hex::encode(Sha256::digest(bytes))),
                ..Default::default()
            })
            .collect();
        let mut bundle = contents.concat();
        if matches!(mode, Body::BadFileHash) {
            bundle[0] ^= 1;
        }
        let manifest = Manifest {
            game_id: "packs".into(),
            version: "2".into(),
            files,
            small_packs: vec![SmallPack {
                sha256: hex::encode(Sha256::digest(&bundle)),
                files: (0..COUNT).collect(),
            }],
            ..Default::default()
        };
        if matches!(mode, Body::BadPackHash) {
            bundle[0] ^= 1;
        }
        if let Body::WrongLength { oversized, .. } = mode {
            if oversized {
                bundle.push(0);
            } else {
                bundle.pop();
            }
        }
        Self {
            manifest,
            contents,
            bundle,
            mode,
        }
    }

    fn start(self, root: &Path) -> Server {
        let listener = tiny_http::Server::http(("127.0.0.1", 0)).unwrap();
        let address = listener.server_addr().to_string();
        let pack_path = format!("/build/packs/{}.bin", self.manifest.small_packs[0].sha256);
        let mut handle = Server {
            request: TransferRequest {
                game_id: self.manifest.game_id.clone(),
                version: self.manifest.version.clone(),
                root: root.into(),
                manifest_url: format!("http://{address}/build/manifest.json"),
                content_base_url: format!("http://{address}/build/files/"),
                max_parallel: 2,
                ..Default::default()
            },
            seen: Arc::new(Mutex::new(Vec::new())),
            stop: Arc::new(AtomicBool::new(false)),
            thread: None,
        };
        let seen = handle.seen.clone();
        let stop = handle.stop.clone();
        let game_root = root.to_path_buf();
        let thread =
            thread::spawn(move || {
                let manifest = serde_json::to_vec(&self.manifest).unwrap();
                let mut peers: Vec<tiny_http::Request> = Vec::new();
                while !stop.load(Ordering::Relaxed) {
                    let Some(request) = listener.recv_timeout(Duration::from_millis(20)).unwrap()
                    else {
                        continue;
                    };
                    let path = request.url();
                    let range = request
                        .headers()
                        .iter()
                        .find(|header| header.field.equiv("Range"));
                    seen.lock().unwrap().push((
                        path.to_owned(),
                        range.map(|header| header.value.to_string()),
                    ));
                    if matches!(self.mode, Body::PeerFailure)
                        && (path == pack_path || path == "/build/files/standalone.bin")
                    {
                        peers.push(request);
                        if peers.len() != 2 {
                            continue;
                        }
                        // Wait until both workers request their payload, then
                        // fail the individual writer before the bundle arrives.
                        fs::create_dir(game_root.join("standalone.bin.part")).unwrap();
                        let file_index = usize::from(peers[0].url() == pack_path);
                        let file = peers.swap_remove(file_index);
                        let _ = file
                            .respond(Response::from_data(self.contents.last().unwrap().clone()));
                        let pack = peers.pop().unwrap();
                        let source = PackReader {
                            data: Cursor::new(self.bundle.clone()),
                            slow: true,
                        };
                        let _ = pack.respond(Response::new(
                            StatusCode(200),
                            vec![],
                            source,
                            Some(self.bundle.len()),
                            None,
                        ));
                        continue;
                    }
                    if path == "/build/manifest.json" {
                        let _ = request.respond(Response::from_data(manifest.clone()));
                    } else if path == pack_path {
                        if matches!(self.mode, Body::Missing) {
                            let _ = request.respond(Response::empty(404));
                        } else {
                            let length = (!matches!(
                                self.mode,
                                Body::WrongLength {
                                    announced: false,
                                    ..
                                }
                            ))
                            .then_some(self.bundle.len());
                            let source = PackReader {
                                data: Cursor::new(self.bundle.clone()),
                                slow: matches!(self.mode, Body::Slow),
                            };
                            let _ = request.respond(
                                Response::new(StatusCode(200), vec![], source, length, None)
                                    // tiny_http otherwise defaults to chunked at 32 KiB.
                                    .with_chunked_threshold(usize::MAX),
                            );
                        }
                    } else if let Some(index) = self.manifest.files.iter().position(|file| {
                        path.strip_prefix("/build/files/") == Some(file.path.as_str())
                    }) {
                        let bytes = &self.contents[index];
                        let offset = range.map_or(0, |header| {
                            header
                                .value
                                .as_str()
                                .strip_prefix("bytes=")
                                .unwrap()
                                .trim_end_matches('-')
                                .parse()
                                .unwrap()
                        });
                        let mut response = Response::from_data(bytes[offset..].to_vec());
                        if offset != 0 {
                            response = response.with_status_code(206).with_header(
                                Header::from_bytes(
                                    "Content-Range",
                                    format!("bytes {offset}-{}/{}", bytes.len() - 1, bytes.len()),
                                )
                                .unwrap(),
                            );
                        }
                        let _ = request.respond(response);
                    } else {
                        let _ = request.respond(Response::empty(404));
                    }
                }
            });
        handle.thread = Some(thread);
        handle
    }
}

struct PackReader {
    data: Cursor<Vec<u8>>,
    slow: bool,
}

impl Read for PackReader {
    fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
        let limit = if self.slow {
            thread::sleep(Duration::from_millis(45));
            buffer.len().min(1024)
        } else {
            buffer.len()
        };
        self.data.read(&mut buffer[..limit])
    }
}

struct Server {
    request: TransferRequest,
    seen: Arc<Mutex<RequestHistory>>,
    stop: Arc<AtomicBool>,
    thread: Option<thread::JoinHandle<()>>,
}

impl Server {
    fn requests(&self, prefix: &str) -> Vec<(String, Option<String>)> {
        self.seen
            .lock()
            .unwrap()
            .iter()
            .filter(|(path, _)| path.starts_with(prefix))
            .cloned()
            .collect()
    }
}

impl Drop for Server {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        self.thread.take().unwrap().join().unwrap();
    }
}

#[test]
fn verified_small_pack_installs_every_file_with_one_content_request() {
    for workers in [1, 8] {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("game");
        let mut server = Fixture::new(Body::Good).start(&root);
        server.request.max_parallel = workers;
        let outcome = TransferEngine::new()
            .unwrap()
            .run(&server.request, &TransferControl::default(), &|_| {})
            .unwrap();
        assert_eq!(server.requests("/build/packs/").len(), 1);
        assert!(server.requests("/build/files/").is_empty());
        assert_eq!(outcome.network_bytes, (SIZE * COUNT) as u64);
        assert_eq!(outcome.reused_bytes, 0);
        assert_eq!(outcome.downloaded_files, COUNT);
        assert_eq!(fs::read(root.join(".version")).unwrap(), b"2");
        for index in 0..COUNT {
            assert_eq!(
                fs::read(root.join(format!("dir/{index}.dat"))).unwrap(),
                vec![index as u8; SIZE]
            );
        }
    }
}

#[test]
fn pack_unavailable_or_any_digest_mismatch_uses_original_files() {
    for (mode, copies) in [
        (Body::Missing, 1),
        (Body::BadPackHash, 2),
        (Body::BadFileHash, 2),
    ] {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("game");
        let server = Fixture::new(mode).start(&root);
        let outcome = TransferEngine::new()
            .unwrap()
            .run(&server.request, &TransferControl::default(), &|_| {})
            .unwrap();
        assert_eq!(server.requests("/build/packs/").len(), 1);
        assert_eq!(server.requests("/build/files/").len(), COUNT);
        assert_eq!(outcome.network_bytes, (SIZE * COUNT * copies) as u64);
        assert_eq!(fs::read(root.join("dir/0.dat")).unwrap(), vec![0; SIZE]);
    }
}

#[test]
fn a_partial_file_created_after_the_empty_root_check_is_resumed() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("game");
    let server = Fixture::new(Body::Good).start(&root);
    let created = AtomicBool::new(false);
    let engine = TransferEngine::new().unwrap();
    let outcome = engine
        .run(&server.request, &TransferControl::default(), &|event| {
            if event.stage == TransferStage::Downloading && !created.swap(true, Ordering::SeqCst) {
                fs::create_dir_all(root.join("dir")).unwrap();
                fs::write(root.join("dir/0.dat.part"), vec![0; SIZE / 2]).unwrap();
            }
        })
        .unwrap();
    assert_eq!(server.requests("/build/packs/").len(), 1);
    assert_eq!(
        server.requests("/build/files/"),
        vec![(
            "/build/files/dir/0.dat".into(),
            Some(format!("bytes={}-", SIZE / 2))
        )]
    );
    assert_eq!(outcome.network_bytes, (SIZE * COUNT + SIZE / 2) as u64);
    assert_eq!(fs::read(root.join("dir/0.dat")).unwrap(), vec![0; SIZE]);
}

#[test]
fn short_and_oversized_pack_responses_fall_back_with_accurate_traffic() {
    for announced in [false, true] {
        for oversized in [false, true] {
            let temp = tempfile::tempdir().unwrap();
            let root = temp.path().join("game");
            let server = Fixture::new(Body::WrongLength {
                announced,
                oversized,
            })
            .start(&root);
            let outcome = TransferEngine::new()
                .unwrap()
                .run(&server.request, &TransferControl::default(), &|_| {})
                .unwrap();
            let expected_size = (SIZE * COUNT) as u64;
            let bundle_received = if announced {
                0
            } else if oversized {
                expected_size + 1
            } else {
                expected_size - 1
            };
            assert_eq!(outcome.network_bytes, expected_size + bundle_received);
            assert_eq!(server.requests("/build/packs/").len(), 1);
            assert_eq!(server.requests("/build/files/").len(), COUNT);
            assert_eq!(fs::read(root.join("dir/0.dat")).unwrap(), vec![0; SIZE]);
        }
    }
}

#[test]
fn incomplete_groups_and_partial_files_use_individual_requests() {
    for existing in ["0.dat", "0.dat.part"] {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("game");
        fs::create_dir_all(root.join("dir")).unwrap();
        let prefix = if existing.ends_with(".part") {
            SIZE / 2
        } else {
            SIZE
        };
        fs::write(root.join("dir").join(existing), vec![0; prefix]).unwrap();
        let server = Fixture::new(Body::Good).start(&root);
        let outcome = TransferEngine::new()
            .unwrap()
            .run(&server.request, &TransferControl::default(), &|_| {})
            .unwrap();
        assert!(server.requests("/build/packs/").is_empty());
        let requests = server.requests("/build/files/");
        assert_eq!(requests.len(), COUNT - usize::from(prefix == SIZE));
        if prefix != SIZE {
            assert!(requests.contains(&(
                "/build/files/dir/0.dat".into(),
                Some(format!("bytes={prefix}-"))
            )));
        }
        assert_eq!(outcome.network_bytes, (SIZE * COUNT - prefix) as u64);
        assert_eq!(fs::read(root.join("dir/0.dat")).unwrap(), vec![0; SIZE]);
    }
}

#[test]
fn a_donor_or_untrusted_pack_index_keeps_the_individual_path() {
    for use_donor in [false, true] {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("game");
        let mut fixture = Fixture::new(Body::Good);
        if !use_donor {
            fixture.manifest.small_packs[0].files[1] = usize::MAX;
            let mut invalid_hash = fixture.manifest.small_packs[0].clone();
            invalid_hash.sha256 = "../redirect.bin".into();
            invalid_hash.files = (0..COUNT).collect();
            fixture.manifest.small_packs.push(invalid_hash);
        }
        let mut server = fixture.start(&root);
        if use_donor {
            let donor = temp.path().join("donor");
            fs::create_dir_all(donor.join("dir")).unwrap();
            fs::write(donor.join("dir/0.dat"), vec![0; SIZE]).unwrap();
            server.request.donor_roots.push(donor);
        }
        let outcome = TransferEngine::new()
            .unwrap()
            .run(&server.request, &TransferControl::default(), &|_| {})
            .unwrap();
        assert!(server.requests("/build/packs/").is_empty());
        assert_eq!(
            server.requests("/build/files/").len(),
            COUNT - usize::from(use_donor)
        );
        assert_eq!(outcome.reused_bytes, (SIZE * usize::from(use_donor)) as u64);
    }
}

#[test]
fn malformed_advisory_json_does_not_invalidate_individual_files() {
    for metadata in [
        serde_json::json!("bad metadata"),
        serde_json::json!([null, { "sha256": 1, "files": [0] }, { "files": [-1] }]),
    ] {
        let mut value = serde_json::to_value(Fixture::new(Body::Good).manifest).unwrap();
        value["smallPacks"] = metadata;
        let manifest: Manifest = serde_json::from_value(value).unwrap();
        assert!(manifest.small_packs.is_empty());
        assert_eq!(manifest.files.len(), COUNT);
    }
}

#[test]
fn cancelling_an_unverified_pack_writes_no_members_and_does_not_fall_back() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("game");
    let server = Fixture::new(Body::Slow).start(&root);
    let control = TransferControl::default();
    let error = TransferEngine::new()
        .unwrap()
        .run(&server.request, &control, &|event| {
            if event.stage == TransferStage::Downloading && event.network_bytes != 0 {
                assert_eq!(event.completed_files, 0);
                if control.is_cancelled() {
                    assert_eq!(event.completed_bytes, 0);
                } else {
                    assert!(event.completed_bytes > 0);
                    control.cancel();
                }
            }
        })
        .unwrap_err();
    assert!(matches!(error, TransferError::Cancelled));
    assert_eq!(server.requests("/build/packs/").len(), 1);
    assert!(server.requests("/build/files/").is_empty());
    assert!(!root.join("dir").exists());
    assert!(!root.join(".version").exists());
}

#[test]
fn stopping_a_pack_does_not_replace_the_first_file_error_with_cancellation() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("game");
    let mut fixture = Fixture::new(Body::PeerFailure);
    let contents = vec![0x55; SIZE + 1];
    fixture.manifest.files.push(ManifestFile {
        path: "standalone.bin".into(),
        size: contents.len() as u64,
        blake3: blake3::hash(&contents).to_hex().to_string(),
        ..Default::default()
    });
    fixture.contents.push(contents);
    let server = fixture.start(&root);
    let control = TransferControl::default();
    let error = TransferEngine::new()
        .unwrap()
        .run(&server.request, &control, &|_| {})
        .unwrap_err();
    assert!(matches!(error, TransferError::Io(_)), "{error:?}");
    assert!(!control.is_cancelled());
    assert_eq!(server.requests("/build/packs/").len(), 1);
    assert_eq!(server.requests("/build/files/").len(), 1);
    assert!(!root.join(".version").exists());
    assert!(!root.join("dir").exists());
}
