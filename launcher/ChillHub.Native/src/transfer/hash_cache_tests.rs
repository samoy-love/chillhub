use super::*;
use std::fs::FileTimes;

fn verify(
    cache: &Mutex<HashCache>,
    path: &Path,
    expected: &ManifestFile,
    force: bool,
    control: &TransferControl,
) -> bool {
    HashCache::verify(cache, path, expected, force, control, &mut vec![0; BUFFER])
        .unwrap()
        .0
}
fn expected(data: &[u8]) -> ManifestFile {
    ManifestFile {
        path: "game.dat".into(),
        size: data.len() as u64,
        sha256: Some(hex(&Sha256::digest(data))),
        blake3: blake3::hash(data).to_hex().to_string(),
        ..Default::default()
    }
}
fn fixture() -> (tempfile::TempDir, PathBuf, PathBuf, ManifestFile) {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("game");
    fs::create_dir(&root).unwrap();
    let data = vec![19; 1024 * 1024];
    fs::write(root.join("game.dat"), &data).unwrap();
    let cache = temp.path().join("cache");
    (temp, root, cache, expected(&data))
}
#[test]
fn unchanged_file_is_hashed_once_and_cached_across_restart() {
    let (_temp, root, directory, expected) = fixture();
    let control = TransferControl::default();
    let cache = Mutex::new(HashCache::load(Some(&directory), &root));
    assert!(verify(
        &cache,
        &root.join("game.dat"),
        &expected,
        false,
        &control
    ));
    assert!(cache.lock().unwrap().dirty);
    cache.lock().unwrap().save();
    let cache = Mutex::new(HashCache::load(Some(&directory), &root));
    assert!(verify(
        &cache,
        &root.join("game.dat"),
        &expected,
        false,
        &control
    ));
    assert!(
        !cache.lock().unwrap().dirty,
        "cache hits do not need to rewrite the digest"
    );
}
#[test]
fn changed_file_and_new_manifest_digest_invalidate_cached_success() {
    let (_temp, root, directory, expected) = fixture();
    let control = TransferControl::default();
    let path = root.join("game.dat");
    let cache = Mutex::new(HashCache::load(Some(&directory), &root));
    assert!(verify(&cache, &path, &expected, false, &control));
    let mut changed_manifest = expected.clone();
    changed_manifest.sha256 = Some("00".repeat(32));
    assert!(!verify(&cache, &path, &changed_manifest, false, &control));
    assert!(cache.lock().unwrap().document.entries.is_empty());
    assert!(verify(&cache, &path, &expected, false, &control));
    let before = fs::metadata(&path).unwrap().modified().unwrap();
    fs::write(&path, vec![20; expected.size as usize]).unwrap();
    fs::OpenOptions::new()
        .write(true)
        .open(&path)
        .unwrap()
        .set_times(FileTimes::new().set_modified(before + std::time::Duration::from_secs(1)))
        .unwrap();
    assert!(!verify(&cache, &path, &expected, false, &control));
    assert!(cache.lock().unwrap().document.entries.is_empty());
}
#[test]
fn explicit_verify_rehashes_corruption_even_if_size_and_time_are_restored() {
    let (_temp, root, directory, expected) = fixture();
    let control = TransferControl::default();
    let path = root.join("game.dat");
    let cache = Mutex::new(HashCache::load(Some(&directory), &root));
    assert!(verify(&cache, &path, &expected, false, &control));
    let before = fs::metadata(&path).unwrap().modified().unwrap();
    fs::write(&path, vec![21; expected.size as usize]).unwrap();
    fs::OpenOptions::new()
        .write(true)
        .open(&path)
        .unwrap()
        .set_times(FileTimes::new().set_modified(before))
        .unwrap();
    assert!(verify(&cache, &path, &expected, false, &control));
    assert!(!verify(&cache, &path, &expected, true, &control));
    assert!(cache.lock().unwrap().document.entries.is_empty());
}
#[test]
fn corrupt_and_oversized_cache_never_trust_metadata_alone() {
    let (_temp, root, directory, expected) = fixture();
    let control = TransferControl::default();
    let cache = Mutex::new(HashCache::load(Some(&directory), &root));
    assert!(verify(
        &cache,
        &root.join("game.dat"),
        &expected,
        false,
        &control
    ));
    cache.lock().unwrap().save();
    let path = cache.lock().unwrap().path.clone().unwrap();
    for oversized in [false, true] {
        fs::write(&path, b"{broken").unwrap();
        if oversized {
            fs::OpenOptions::new()
                .write(true)
                .open(&path)
                .unwrap()
                .set_len(MAX_BYTES as u64 + 1)
                .unwrap();
        }
        let cache = Mutex::new(HashCache::load(Some(&directory), &root));
        assert!(verify(
            &cache,
            &root.join("game.dat"),
            &expected,
            false,
            &control
        ));
        assert!(cache.lock().unwrap().dirty);
    }
}
#[test]
fn cache_is_per_root_and_invalidation_survives_restart() {
    let (temp, root, directory, expected) = fixture();
    let control = TransferControl::default();
    let cache = Mutex::new(HashCache::load(Some(&directory), &root));
    assert!(verify(
        &cache,
        &root.join("game.dat"),
        &expected,
        false,
        &control
    ));
    cache.lock().unwrap().save();
    cache.lock().unwrap().invalidate("game.dat");
    cache.lock().unwrap().save();
    assert!(
        HashCache::load(Some(&directory), &root)
            .document
            .entries
            .is_empty()
    );
    let second_root = temp.path().join("steam");
    fs::create_dir(&second_root).unwrap();
    fs::write(
        second_root.join("game.dat"),
        vec![22; expected.size as usize],
    )
    .unwrap();
    let other = Mutex::new(HashCache::load(Some(&directory), &second_root));
    assert_ne!(cache.lock().unwrap().path, other.lock().unwrap().path);
    assert!(!verify(
        &other,
        &second_root.join("game.dat"),
        &expected,
        false,
        &control
    ));
}
#[test]
fn cache_write_is_bounded_and_changed_promoted_file_is_not_recorded() {
    let (_temp, root, directory, expected) = fixture();
    let control = TransferControl::default();
    let path = root.join("game.dat");
    let verified = verify_for_promotion(&path, &expected, &control).unwrap().1;
    let before = fs::metadata(&path).unwrap().modified().unwrap();
    fs::OpenOptions::new()
        .write(true)
        .open(&path)
        .unwrap()
        .set_times(FileTimes::new().set_modified(before + std::time::Duration::from_secs(1)))
        .unwrap();
    let cache = Mutex::new(HashCache::load(Some(&directory), &root));
    cache
        .lock()
        .unwrap()
        .remember_verified(&path, &expected, verified);
    assert!(cache.lock().unwrap().document.entries.is_empty());
    let mut output = Bounded(Vec::new());
    output.write_all(&vec![0; MAX_BYTES]).unwrap();
    assert!(output.write_all(&[0]).is_err());
    assert_eq!(output.0.len(), MAX_BYTES);
}

#[test]
fn streaming_hashes_require_every_published_digest() {
    let bytes = b"exact bytes from a resumed prefix and an HTTP body";
    for (sha, blake, valid) in [
        (true, true, true),
        (false, true, true),
        (true, false, true),
        (true, true, false),
    ] {
        let mut file = expected(bytes);
        if !sha {
            file.sha256 = None;
        }
        if !blake {
            file.blake3.clear();
        }
        if !valid {
            file.sha256 = Some("00".repeat(32));
        }
        let mut digests = FileHashes::new(&file);
        assert_eq!(digests.sha.is_some(), sha);
        assert_eq!(digests.blake.is_some(), blake);
        digests.update(&bytes[..9]);
        digests.update(&bytes[9..]);
        assert_eq!(digests.matches(&file).is_some(), valid);
    }
}
