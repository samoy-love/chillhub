//! Optional acceleration for ordinary game updates; explicit verification never trusts it.
use super::{BUFFER, Result, TransferControl, allocated_bytes, atomic_write, hex};
use crate::manifest::{ManifestFile, open_regular};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::HashMap,
    fs,
    io::{self, Read, Write},
    path::{Path, PathBuf},
    sync::Mutex,
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
}

pub(super) struct ReplacementSize {
    pub size: u64,
    pub allocated: u64,
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
        }
    }

    pub(super) fn verify(
        cache: &Mutex<Self>,
        path: &Path,
        expected: &ManifestFile,
        force: bool,
        control: &TransferControl,
        buffer: &mut [u8],
    ) -> Result<(bool, Option<ReplacementSize>)> {
        control.checkpoint()?;
        let key = expected.path.to_lowercase();
        let mut file = match open_regular(path, false, false, false) {
            Ok(file) => file,
            Err(e) if e.kind() == io::ErrorKind::NotFound => {
                cache
                    .lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .invalidate(&expected.path);
                return Ok((false, None));
            }
            Err(e) => return Err(e.into()),
        };
        let metadata = file.metadata()?;
        let stamp = Stamp::read(&metadata);
        {
            let mut cache = cache.lock().unwrap_or_else(|e| e.into_inner());
            if !force
                && let (Some(stamp), Some(entry)) = (&stamp, cache.document.entries.get_mut(&key))
                && &entry.stamp == stamp
                && entry.matches(expected)
            {
                entry.used = nanos(SystemTime::now()).unwrap_or(0);
                return Ok((true, None));
            }
            cache.invalidate(&expected.path);
        }
        let (valid, entry) = hash_open_file(&mut file, expected, control, buffer)?;
        if valid && let Some(entry) = entry {
            let mut cache = cache.lock().unwrap_or_else(|e| e.into_inner());
            if cache.path.is_some() {
                cache.document.entries.insert(key, entry);
                cache.dirty = true;
            }
        }
        let replaced = (!valid).then(|| ReplacementSize {
            size: metadata.len(),
            allocated: allocated_bytes(&file, &metadata),
        });
        Ok((valid, replaced))
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
        cache: &Mutex<Self>,
        path: &Path,
        expected: &ManifestFile,
        verified: Option<VerifiedStamp>,
    ) {
        let Some(verified) = verified else {
            return;
        };
        // Query the destination before taking the shared cache lock so one
        // worker's filesystem IO does not serialize the whole batch.
        if !verified.still_matches(path) {
            return;
        }
        let mut cache = cache.lock().unwrap_or_else(|e| e.into_inner());
        if cache.path.is_none() {
            return;
        }
        cache.document.entries.insert(
            expected.path.to_lowercase(),
            Entry {
                stamp: verified.0,
                sha256: expected.sha256.clone().unwrap_or_default(),
                blake3: expected.blake3.clone(),
                used: nanos(SystemTime::now()).unwrap_or(0),
            },
        );
        cache.dirty = true;
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
    let mut file = match open_regular(path, false, false, false) {
        Ok(file) => file,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok((false, None)),
        Err(e) => return Err(e.into()),
    };
    hash_open_file(&mut file, expected, control, &mut vec![0; BUFFER])
        .map(|(valid, entry)| (valid, entry.map(|entry| VerifiedStamp(entry.stamp))))
}
fn hash_open_file(
    file: &mut fs::File,
    expected: &ManifestFile,
    control: &TransferControl,
    buffer: &mut [u8],
) -> Result<(bool, Option<Entry>)> {
    let metadata = file.metadata()?;
    if metadata.len() != expected.size {
        return Ok((false, None));
    }
    let before = Stamp::read(&metadata);
    let mut hashes = FileHashes::new(expected);
    loop {
        control.checkpoint()?;
        let n = file.read(buffer)?;
        if n == 0 {
            break;
        }
        hashes.update(&buffer[..n]);
    }
    // A writer racing the hash never seeds a trusted cache record.
    let after = Stamp::read(&file.metadata()?);
    if before != after {
        return Ok((false, None));
    }
    let Some((sha256, blake3)) = hashes.matches(expected) else {
        return Ok((false, None));
    };
    Ok((
        true,
        after.map(|stamp| Entry {
            stamp,
            sha256,
            blake3,
            used: nanos(SystemTime::now()).unwrap_or(0),
        }),
    ))
}

pub(super) struct FileHashes {
    sha: Option<Sha256>,
    blake: Option<blake3::Hasher>,
}
impl FileHashes {
    pub(super) fn new(expected: &ManifestFile) -> Self {
        Self {
            sha: expected
                .sha256
                .as_ref()
                .is_some_and(|s| !s.is_empty())
                .then(Sha256::new),
            blake: (!expected.blake3.is_empty()).then(blake3::Hasher::new),
        }
    }

    pub(super) fn update(&mut self, bytes: &[u8]) {
        if let Some(sha) = &mut self.sha {
            sha.update(bytes);
        }
        if let Some(blake) = &mut self.blake {
            blake.update(bytes);
        }
    }

    fn matches(self, expected: &ManifestFile) -> Option<(String, String)> {
        let sha256 = self.sha.map(|sha| hex(&sha.finalize())).unwrap_or_default();
        let blake3 = self
            .blake
            .map(|blake| blake.finalize().to_hex().to_string())
            .unwrap_or_default();
        (sha256.eq_ignore_ascii_case(expected.sha256.as_deref().unwrap_or_default())
            && blake3.eq_ignore_ascii_case(&expected.blake3))
        .then_some((sha256, blake3))
    }

    pub(super) fn seal(
        self,
        file: fs::File,
        path: &Path,
        expected: &ManifestFile,
        control: &TransferControl,
    ) -> Result<Option<VerifiedStamp>> {
        use super::TransferError;
        control.checkpoint()?;
        if self.matches(expected).is_none() || file.metadata()?.len() != expected.size {
            return Err(TransferError::Integrity(expected.path.clone()));
        }
        file.sync_all()?;
        let stamp = Stamp::read(&file.metadata()?).map(VerifiedStamp);
        drop(file);
        if stamp
            .as_ref()
            .is_some_and(|stamp| stamp.still_matches(path))
        {
            return Ok(stamp);
        }
        // Some filesystems update metadata as the writer closes. Recheck the
        // contents before trusting a file whose stamp can't be carried across.
        let (valid, stamp) = verify_for_promotion(path, expected, control)?;
        if !valid {
            return Err(TransferError::Integrity(expected.path.clone()));
        }
        Ok(stamp)
    }
}

#[cfg(test)]
#[path = "hash_cache_tests.rs"]
mod tests;
