use super::*;
use std::fs::FileTimes;

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
    let mut cache = HashCache::load(Some(&directory), &root);
    assert!(
        cache
            .verify(&root.join("game.dat"), &expected, false, &control)
            .unwrap()
    );
    assert!(cache.dirty);
    cache.save();
    let mut cache = HashCache::load(Some(&directory), &root);
    assert!(
        cache
            .verify(&root.join("game.dat"), &expected, false, &control)
            .unwrap()
    );
    assert!(!cache.dirty, "cache hits do not need to rewrite the digest");
}
#[test]
fn changed_file_and_new_manifest_digest_invalidate_cached_success() {
    let (_temp, root, directory, expected) = fixture();
    let control = TransferControl::default();
    let path = root.join("game.dat");
    let mut cache = HashCache::load(Some(&directory), &root);
    assert!(cache.verify(&path, &expected, false, &control).unwrap());
    let mut changed_manifest = expected.clone();
    changed_manifest.sha256 = Some("00".repeat(32));
    assert!(
        !cache
            .verify(&path, &changed_manifest, false, &control)
            .unwrap()
    );
    assert!(cache.document.entries.is_empty());
    assert!(cache.verify(&path, &expected, false, &control).unwrap());
    let before = fs::metadata(&path).unwrap().modified().unwrap();
    fs::write(&path, vec![20; expected.size as usize]).unwrap();
    fs::OpenOptions::new()
        .write(true)
        .open(&path)
        .unwrap()
        .set_times(FileTimes::new().set_modified(before + std::time::Duration::from_secs(1)))
        .unwrap();
    assert!(!cache.verify(&path, &expected, false, &control).unwrap());
    assert!(cache.document.entries.is_empty());
}
#[test]
fn explicit_verify_rehashes_corruption_even_if_size_and_time_are_restored() {
    let (_temp, root, directory, expected) = fixture();
    let control = TransferControl::default();
    let path = root.join("game.dat");
    let mut cache = HashCache::load(Some(&directory), &root);
    assert!(cache.verify(&path, &expected, false, &control).unwrap());
    let before = fs::metadata(&path).unwrap().modified().unwrap();
    fs::write(&path, vec![21; expected.size as usize]).unwrap();
    fs::OpenOptions::new()
        .write(true)
        .open(&path)
        .unwrap()
        .set_times(FileTimes::new().set_modified(before))
        .unwrap();
    assert!(cache.verify(&path, &expected, false, &control).unwrap());
    assert!(!cache.verify(&path, &expected, true, &control).unwrap());
    assert!(cache.document.entries.is_empty());
}
#[test]
fn corrupt_and_oversized_cache_never_trust_metadata_alone() {
    let (_temp, root, directory, expected) = fixture();
    let control = TransferControl::default();
    let mut cache = HashCache::load(Some(&directory), &root);
    assert!(
        cache
            .verify(&root.join("game.dat"), &expected, false, &control)
            .unwrap()
    );
    cache.save();
    let path = cache.path.unwrap();
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
        let mut cache = HashCache::load(Some(&directory), &root);
        assert!(
            cache
                .verify(&root.join("game.dat"), &expected, false, &control)
                .unwrap()
        );
        assert!(cache.dirty);
    }
}
#[test]
fn cache_is_per_root_and_invalidation_survives_restart() {
    let (temp, root, directory, expected) = fixture();
    let control = TransferControl::default();
    let mut cache = HashCache::load(Some(&directory), &root);
    assert!(
        cache
            .verify(&root.join("game.dat"), &expected, false, &control)
            .unwrap()
    );
    cache.save();
    cache.invalidate("game.dat");
    cache.save();
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
    let mut other = HashCache::load(Some(&directory), &second_root);
    assert_ne!(cache.path, other.path);
    assert!(
        !other
            .verify(&second_root.join("game.dat"), &expected, false, &control)
            .unwrap()
    );
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
    let mut cache = HashCache::load(Some(&directory), &root);
    cache.remember_verified(&path, &expected, verified);
    assert!(cache.document.entries.is_empty());
    let mut output = Bounded(Vec::new());
    output.write_all(&vec![0; MAX_BYTES]).unwrap();
    assert!(output.write_all(&[0]).is_err());
    assert_eq!(output.0.len(), MAX_BYTES);
}
