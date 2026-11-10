//! Read-only background integrity checks. Cached hashes avoid re-reading unchanged game data.
use crate::{
    config::ConfigStore,
    launch,
    manifest::{self, Manifest},
    model::GameRecord,
    net::ApiClient,
    paths::AppPaths,
    transfer::{TransferControl, TransferEngine, TransferRequest, atomic_write},
};
use fs2::FileExt;
use serde::{Deserialize, Serialize};
use std::{
    collections::{HashMap, HashSet},
    fs,
    io::Read,
    path::Path,
    sync::{Mutex, OnceLock},
    time::UNIX_EPOCH,
};

const MAX_STATE_BYTES: u64 = 32 * 1024 * 1024;
#[derive(Clone, Serialize, Deserialize)]
struct Status {
    version: String,
    api_base: String,
    fingerprint: String,
    needs_repair: bool,
}
#[derive(Serialize, Deserialize)]
struct Stored {
    schema: u8,
    root: String,
    status: Status,
    manifest: Manifest,
}
#[derive(Default)]
struct Memory {
    states: HashMap<String, Status>,
    checking: HashSet<String>,
}
fn memory() -> &'static Mutex<Memory> {
    static MEMORY: OnceLock<Mutex<Memory>> = OnceLock::new();
    MEMORY.get_or_init(Mutex::default)
}
fn key(root: &Path) -> String {
    let key = root.to_string_lossy().into_owned();
    #[cfg(windows)]
    let key = key.to_lowercase();
    key
}

pub fn needs_repair(root: &Path, version: &str) -> bool {
    memory()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .states
        .get(&key(root))
        .is_some_and(|state| state.version == version && state.needs_repair)
}
pub fn invalidate(root: &Path) {
    memory()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .states
        .remove(&key(root));
}
struct Checking(String);
impl Drop for Checking {
    fn drop(&mut self) {
        memory()
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .checking
            .remove(&self.0);
    }
}

/// Blocking; call only from a background worker. Network failures leave the previous result intact.
pub fn check(game: &GameRecord, paths: &AppPaths, api: &ApiClient) -> Result<bool, String> {
    let root = paths.game_root(&game.game_id)?;
    let config = ConfigStore::new(paths.clone()).load_readonly()?;
    let version = config
        .selected_version(&game.game_id)
        .unwrap_or(&game.latest_version)
        .to_owned();
    let prior = needs_repair(&root, &version);
    if version.is_empty()
        || !root.is_dir()
        || launch::read_marker(&root, ".version") != version
        || root.join(".updating").exists()
    {
        return Ok(prior);
    }
    let root_key = key(&root);
    {
        let mut memory = memory().lock().unwrap_or_else(|e| e.into_inner());
        if !memory.checking.insert(root_key.clone()) {
            return Ok(prior);
        }
    }
    let _checking = Checking(root_key.clone());
    // Do not race a game or the installer. Holding this handle blocks a launch through Chill Hub.
    let _lock = if root.join(".chillhub.lock").exists() {
        let file = manifest::open_regular(&root.join(".chillhub.lock"), false, false, false)
            .map_err(|e| e.to_string())?;
        if file.try_lock_exclusive().is_err() {
            return Ok(prior);
        }
        Some(file)
    } else {
        None
    };
    if !crate::playtime::processes_in(&root)?.is_empty() {
        return Ok(prior);
    }
    let fingerprint = fingerprint(&root)?;
    let known = memory()
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .states
        .get(&root_key)
        .cloned();
    if let Some(status) = known
        && status.version == version
        && status.api_base == api.base.as_str()
        && status.fingerprint == fingerprint
    {
        return Ok(status.needs_repair);
    }
    let state_path = paths
        .data_dir
        .join("integrity")
        .join(format!("{}.json", blake3::hash(root_key.as_bytes())));
    let stored = read_state(
        &state_path,
        &root_key,
        &game.game_id,
        &version,
        api.base.as_str(),
    );
    if let Some(stored) = &stored
        && stored.status.fingerprint == fingerprint
    {
        remember(&root_key, stored.status.clone());
        return Ok(stored.status.needs_repair);
    }
    let engine = TransferEngine::new()
        .map_err(|e| e.to_string())?
        .with_hash_cache_dir(paths.data_dir.join("hashcache"));
    let manifest_url = if version == game.latest_version {
        api.resolve(&game.manifest_url)
    } else {
        api.game_manifest_url(&game.game_id, &version)
    }
    .map_err(|e| e.to_string())?;
    let control = TransferControl::default();
    let manifest = match stored {
        Some(stored) => stored.manifest,
        None => engine
            .fetch_manifest(manifest_url.as_str(), &control)
            .map_err(|e| e.to_string())?,
    };
    let request = TransferRequest {
        game_id: game.game_id.clone(),
        version: version.clone(),
        root: root.clone(),
        manifest_url: manifest_url.to_string(),
        content_base_url: api
            .game_content_base(&game.game_id, &version)
            .map_err(|e| e.to_string())?
            .to_string(),
        foreign_paths: launch::installed_modpack_paths(&root)?
            .iter()
            .map(|p| p.to_string_lossy().replace('\\', "/"))
            .collect(),
        ..Default::default()
    };
    let plan = engine
        .plan_manifest(&request, manifest, &control, &|_| {})
        .map_err(|e| e.to_string())?;
    if root.join(".updating").exists()
        || launch::read_marker(&root, ".version") != version
        || fingerprint != self::fingerprint(&root)?
    {
        return Ok(prior);
    }
    // Extra user files are not damaged game content and are never deleted by a background check.
    let status = Status {
        version,
        api_base: api.base.to_string(),
        fingerprint,
        needs_repair: !plan.downloads.is_empty(),
    };
    let answer = status.needs_repair;
    remember(&root_key, status.clone());
    let stored = Stored {
        schema: 1,
        root: root_key,
        status,
        manifest: plan.manifest,
    };
    // The manifest was already limited on input; enforce the state limit while serializing too.
    let mut bytes = Bounded(Vec::new());
    if serde_json::to_writer(&mut bytes, &stored).is_ok() {
        let _ = atomic_write(&state_path, &bytes.0);
    }
    Ok(answer)
}
fn remember(root: &str, status: Status) {
    let mut memory = memory().lock().unwrap_or_else(|e| e.into_inner());
    if memory.states.len() >= 256 && !memory.states.contains_key(root) {
        memory.states.clear();
    }
    memory.states.insert(root.to_owned(), status);
}
fn read_state(
    path: &Path,
    root: &str,
    game: &str,
    version: &str,
    api_base: &str,
) -> Option<Stored> {
    let file = manifest::open_regular(path, false, false, false).ok()?;
    if file.metadata().ok()?.len() > MAX_STATE_BYTES {
        return None;
    }
    let mut bytes = Vec::new();
    file.take(MAX_STATE_BYTES + 1)
        .read_to_end(&mut bytes)
        .ok()?;
    if bytes.len() as u64 > MAX_STATE_BYTES {
        return None;
    }
    let stored: Stored = serde_json::from_slice(&bytes).ok()?;
    if stored.schema != 1
        || stored.root != root
        || stored.status.version != version
        || stored.status.api_base != api_base
        || (!stored.manifest.game_id.is_empty() && stored.manifest.game_id != game)
        || (!stored.manifest.version.is_empty() && stored.manifest.version != version)
        || manifest::validate_manifest(&stored.manifest).is_err()
    {
        return None;
    }
    Some(stored)
}
fn fingerprint(root: &Path) -> Result<String, String> {
    manifest::check_no_links(root).map_err(|e| e.to_string())?;
    let mut hash = blake3::Hasher::new();
    for (index, entry) in walkdir::WalkDir::new(root)
        .follow_links(false)
        .sort_by_file_name()
        .min_depth(1)
        .into_iter()
        .enumerate()
    {
        if index > 200_000 {
            return Err("В папке слишком много файлов для фоновой проверки".into());
        }
        let entry = entry.map_err(|e| e.to_string())?;
        let meta = fs::symlink_metadata(entry.path()).map_err(|e| e.to_string())?;
        if manifest::is_reparse(&meta) {
            return Err("Папка игры содержит ссылку".into());
        }
        if !meta.is_file() {
            continue;
        }
        let path = entry
            .path()
            .strip_prefix(root)
            .map_err(|e| e.to_string())?
            .to_string_lossy()
            .replace('\\', "/");
        if manifest::is_service_path(&path) || path.starts_with(".staging/") {
            continue;
        }
        hash.update(path.as_bytes());
        hash.update(&[0]);
        hash.update(&meta.len().to_le_bytes());
        let modified = meta
            .modified()
            .map_err(|e| e.to_string())?
            .duration_since(UNIX_EPOCH)
            .map_err(|e| e.to_string())?
            .as_nanos();
        hash.update(&modified.to_le_bytes());
    }
    Ok(hash.finalize().to_hex().to_string())
}
struct Bounded(Vec<u8>);
impl std::io::Write for Bounded {
    fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
        if self.0.len().saturating_add(bytes.len()) as u64 > MAX_STATE_BYTES {
            return Err(std::io::Error::other("integrity state exceeds limit"));
        }
        self.0.extend_from_slice(bytes);
        Ok(bytes.len())
    }
    fn flush(&mut self) -> std::io::Result<()> {
        Ok(())
    }
}
