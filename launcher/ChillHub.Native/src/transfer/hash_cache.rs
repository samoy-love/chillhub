//! Optional acceleration for ordinary game updates; explicit verification never trusts it.
use super::{BUFFER, Result, TransferControl, atomic_write, hex};
use crate::manifest::{ManifestFile, open_regular};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::HashMap,
    fs,
    io::{self, Read, Write},
    path::{Path, PathBuf},
    time::{SystemTime, UNIX_EPOCH},
};

const MAX_BYTES: usize = 8 * 1024 * 1024;
const MAX_ENTRIES: usize = 32_768;

#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
struct Stamp {
    length: u64,
    modified_ns: u128,
    created_ns: Option<u128>,
}
pub(super) struct VerifiedStamp(Stamp);
impl VerifiedStamp {
    pub(super) fn still_matches(&self, path: &Path) -> bool {
        fs::metadata(path)
            .ok()
            .as_ref()
            .and_then(Stamp::read)
            .as_ref()
            == Some(&self.0)
    }
}
impl Stamp {
    fn read(metadata: &fs::Metadata) -> Option<Self> {
        Some(Self {
            length: metadata.len(),
            modified_ns: nanos(metadata.modified().ok()?)?,
            created_ns: metadata.created().ok().and_then(nanos),
        })
    }
}
fn nanos(time: SystemTime) -> Option<u128> {
    time.duration_since(UNIX_EPOCH).ok().map(|d| d.as_nanos())
}

#[derive(Clone, Serialize, Deserialize)]
struct Entry {
    stamp: Stamp,
    sha256: String,
    blake3: String,
    used: u128,
}
impl Entry {
    fn matches(&self, expected: &ManifestFile) -> bool {
        self.stamp.length == expected.size
            && (expected.blake3.is_empty() || self.blake3.eq_ignore_ascii_case(&expected.blake3))
            && expected
                .sha256
                .as_deref()
                .filter(|s| !s.is_empty())
                .is_none_or(|sha| self.sha256.eq_ignore_ascii_case(sha))
    }
}
#[derive(Serialize, Deserialize)]
struct Document {
    version: u8,
    root: String,
    entries: HashMap<String, Entry>,
}

pub(super) struct HashCache {
    path: Option<PathBuf>,
    document: Document,
    dirty: bool,
    #[cfg(test)]
    hashed: usize,
}
impl HashCache {
    pub(super) fn load(directory: Option<&Path>, root: &Path) -> Self {
        // Canonicalization removes alternative spellings; a missing installation is a miss.
        let root = fs::canonicalize(root).unwrap_or_else(|_| root.to_path_buf());
        let root = root.to_string_lossy().into_owned();
        #[cfg(windows)]
        let root = root.to_lowercase();
        let path = directory.map(|dir| dir.join(format!("{}.json", blake3::hash(root.as_bytes()))));
        let loaded = path.as_ref().and_then(|path| {
            let file = open_regular(path, false, false, false).ok()?;
            if file.metadata().ok()?.len() > MAX_BYTES as u64 {
                return None;
            }
            let mut bytes = Vec::new();
            file.take(MAX_BYTES as u64 + 1)
                .read_to_end(&mut bytes)
                .ok()?;
            if bytes.len() > MAX_BYTES {
                return None;
            }
            let doc: Document = serde_json::from_slice(&bytes).ok()?;
            if doc.version != 1
                || doc.root != root
                || doc.entries.len() > MAX_ENTRIES
                || doc.entries.iter().any(|(path, entry)| {
                    crate::manifest::validate_relative(path).is_err()
                        || !valid_digest(&entry.sha256)
                        || !valid_digest(&entry.blake3)
                        || (entry.sha256.is_empty() && entry.blake3.is_empty())
                })
            {
                return None;
            }
            Some(doc)
        });
        Self {
            path,
            document: loaded.unwrap_or_else(|| Document {
                version: 1,
                root,
                entries: HashMap::new(),
            }),
            dirty: false,
            #[cfg(test)]
            hashed: 0,
        }
    }

    pub(super) fn verify(
        &mut self,
        path: &Path,
        expected: &ManifestFile,
        force: bool,
        control: &TransferControl,
    ) -> Result<bool> {
        control.checkpoint()?;
        let key = expected.path.to_lowercase();
        let file = match open_regular(path, false, false, false) {
            Ok(file) => file,
            Err(e) if e.kind() == io::ErrorKind::NotFound => {
                self.invalidate(&expected.path);
                return Ok(false);
            }
            Err(e) => return Err(e.into()),
        };
        let metadata = file.metadata()?;
        let stamp = Stamp::read(&metadata);
        if metadata.len() != expected.size {
            self.invalidate(&expected.path);
            return Ok(false);
        }
        if !force
            && let (Some(stamp), Some(entry)) = (&stamp, self.document.entries.get_mut(&key))
            && &entry.stamp == stamp
            && entry.matches(expected)
        {
            entry.used = nanos(SystemTime::now()).unwrap_or(0);
            return Ok(true);
        }
        self.invalidate(&expected.path);
        #[cfg(test)]
        {
            self.hashed += 1;
        }
        let (valid, entry) = hash_open_file(file, expected, control)?;
        if valid && let Some(entry) = entry {
            self.document.entries.insert(key, entry);
            self.dirty = true;
        }
        Ok(valid)
    }

    pub(super) fn invalidate(&mut self, relative: &str) {
        self.dirty |= self
            .document
            .entries
            .remove(&relative.to_lowercase())
            .is_some();
    }

    // Called only after the complete .part file passed hashes and was atomically promoted.
    pub(super) fn remember_verified(
        &mut self,
        path: &Path,
        expected: &ManifestFile,
        verified: Option<VerifiedStamp>,
    ) {
        let Some(verified) = verified else {
            return;
        };
        if !verified.still_matches(path) {
            return;
        }
        self.document.entries.insert(
            expected.path.to_lowercase(),
            Entry {
                stamp: verified.0,
                sha256: expected.sha256.clone().unwrap_or_default(),
                blake3: expected.blake3.clone(),
                used: nanos(SystemTime::now()).unwrap_or(0),
            },
        );
        self.dirty = true;
    }

    pub(super) fn save(&mut self) {
        if !self.dirty {
            return;
        }
        let Some(path) = &self.path else {
            return;
        };
        let mut order: Vec<_> = self
            .document
            .entries
            .iter()
            .map(|(key, entry)| (key.clone(), entry.used))
            .collect();
        order.sort_unstable_by_key(|(_, used)| *used);
        for (key, _) in order.iter().take(order.len().saturating_sub(MAX_ENTRIES)) {
            self.document.entries.remove(key);
        }
        // The output is bounded while serializing, before a large allocation can occur.
        loop {
            let mut bytes = Bounded(Vec::new());
            if serde_json::to_writer(&mut bytes, &self.document).is_ok() {
                if atomic_write(path, &bytes.0).is_ok() {
                    self.dirty = false;
                }
                return; // Cache write failure only loses acceleration, never the installation.
            }
            let remove = (self.document.entries.len() / 2).max(1);
            let keys: Vec<_> = self.document.entries.keys().take(remove).cloned().collect();
            if keys.is_empty() {
                return;
            }
            for key in keys {
                self.document.entries.remove(&key);
            }
        }
    }
}
fn valid_digest(digest: &str) -> bool {
    digest.is_empty() || (digest.len() == 64 && digest.bytes().all(|c| c.is_ascii_hexdigit()))
}
struct Bounded(Vec<u8>);
impl Write for Bounded {
    fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
        if self.0.len().saturating_add(bytes.len()) > MAX_BYTES {
            return Err(io::Error::other("hash cache exceeds limit"));
        }
        self.0.extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

pub(super) fn verify_uncached(
    path: &Path,
    expected: &ManifestFile,
    control: &TransferControl,
) -> Result<bool> {
    verify_for_promotion(path, expected, control).map(|(valid, _)| valid)
}
pub(super) fn verify_for_promotion(
    path: &Path,
    expected: &ManifestFile,
    control: &TransferControl,
) -> Result<(bool, Option<VerifiedStamp>)> {
    let file = match open_regular(path, false, false, false) {
        Ok(file) => file,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok((false, None)),
        Err(e) => return Err(e.into()),
    };
    hash_open_file(file, expected, control)
        .map(|(valid, entry)| (valid, entry.map(|entry| VerifiedStamp(entry.stamp))))
}
fn hash_open_file(
    mut file: fs::File,
    expected: &ManifestFile,
    control: &TransferControl,
) -> Result<(bool, Option<Entry>)> {
    let metadata = file.metadata()?;
    if metadata.len() != expected.size {
        return Ok((false, None));
    }
    let before = Stamp::read(&metadata);
    let mut sha = Sha256::new();
    let mut blake = blake3::Hasher::new();
    let mut buffer = vec![0; BUFFER];
    loop {
        control.checkpoint()?;
        let n = file.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        sha.update(&buffer[..n]);
        blake.update(&buffer[..n]);
    }
    let sha256 = hex(&sha.finalize());
    let blake3 = blake.finalize().to_hex().to_string();
    let valid = (expected.blake3.is_empty() || blake3.eq_ignore_ascii_case(&expected.blake3))
        && expected
            .sha256
            .as_deref()
            .filter(|s| !s.is_empty())
            .is_none_or(|s| sha256.eq_ignore_ascii_case(s));
    // A writer racing the hash never seeds a trusted cache record.
    let after = Stamp::read(&file.metadata()?);
    if before != after {
        return Ok((false, None));
    }
    Ok((
        valid,
        after.map(|stamp| Entry {
            stamp,
            sha256,
            blake3,
            used: nanos(SystemTime::now()).unwrap_or(0),
        }),
    ))
}

#[cfg(test)]
mod tests {
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
        assert_eq!(cache.hashed, 1);
        cache.save();
        let mut cache = HashCache::load(Some(&directory), &root);
        assert!(
            cache
                .verify(&root.join("game.dat"), &expected, false, &control)
                .unwrap()
        );
        assert_eq!(
            cache.hashed, 0,
            "unchanged gigabytes must not be read again"
        );
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
        assert_eq!(cache.hashed, 2);
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
        assert_eq!(cache.hashed, 4);
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
        assert!(!cache.verify(&path, &expected, true, &control).unwrap());
        assert_eq!(cache.hashed, 2);
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
            assert_eq!(cache.hashed, 1);
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
}
