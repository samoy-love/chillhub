//! Verified payloads for locked files, recovered against the next transfer plan.
use super::*;
const DIRECTORY: &str = ".chillhub-reboot";
static JOURNAL_LOCK: Mutex<()> = Mutex::new(());

#[derive(Serialize, Deserialize)]
struct Entry {
    payload: String,
    file: ManifestFile,
    game_id: String,
    version: String,
    scope: SyncScope,
}

pub(super) fn is_locked(error: &TransferError) -> bool {
    matches!(error, TransferError::Io(error) if matches!(error.raw_os_error(), Some(32 | 33)))
}

pub(super) fn save_pending(
    request: &TransferRequest,
    part: &Path,
    file: &ManifestFile,
) -> Result<()> {
    let root = &request.root;
    let _lock = JOURNAL_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let directory = path_in(root, DIRECTORY)?;
    let _guard = DirectoryGuard::acquire(&directory, true)?;
    let id = uuid::Uuid::new_v4().to_string();
    let payload = format!("{DIRECTORY}/{id}.bin");
    let saved = path_in(root, &payload)?;
    let journal = directory.join(format!("{id}.json"));
    let entry = Entry {
        payload,
        // Block signatures are only a download optimization. A large game's
        // signatures can exceed the bounded recovery journal by themselves;
        // recovery needs only the whole-file identity and hashes.
        file: ManifestFile {
            blocks: None,
            ..file.clone()
        },
        game_id: request.game_id.clone(),
        version: request.version.clone(),
        scope: request.scope,
    };
    // Write intent before moving anything. Recovery tolerates a missing payload:
    // the original .part remains available if interruption happened before rename.
    atomic_write(
        &journal,
        &serde_json::to_vec(&entry).map_err(|e| TransferError::Protocol(e.to_string()))?,
    )?;
    atomic_replace(part, &saved)?;
    // Do not register a name-only Windows boot move. These user-writable paths
    // may change while the launcher is stopped, and cancellation must not leave
    // an OS operation behind. A requested retry revalidates ownership and hashes.
    Err(TransferError::ReplacementDeferred(file.path.clone()))
}

pub(super) fn recover(
    request: &TransferRequest,
    expected: &[ManifestFile],
    control: &TransferControl,
) -> Result<bool> {
    let root = &request.root;
    let directory = path_in(root, DIRECTORY)?;
    if !directory.try_exists()? {
        return Ok(false);
    }
    let _guard = DirectoryGuard::acquire(root, false)?;
    let _lock = acquire_transfer_lock(root)?;
    ensure_not_running(root)?;
    let _journal_lock = JOURNAL_LOCK.lock().unwrap_or_else(|e| e.into_inner());
    let _directory_guard = DirectoryGuard::acquire(&directory, false)?;
    let mut recovered = false;
    for entry in fs::read_dir(&directory)? {
        control.checkpoint()?;
        let path = entry?.path();
        if path.extension().is_none_or(|e| e != "json") {
            continue;
        }
        let id = path
            .file_stem()
            .and_then(|stem| stem.to_str())
            .filter(|stem| uuid::Uuid::parse_str(stem).is_ok())
            .ok_or_else(|| TransferError::InvalidManifest("invalid deferred entry name".into()))?;
        let mut file = open_regular(&path, false, false, false)?;
        manifest::reject_hard_links(&file)?;
        if file.metadata()?.len() > 64 * 1024 {
            return Err(TransferError::InvalidManifest(
                "deferred entry too large".into(),
            ));
        }
        let mut bytes = Vec::new();
        (&mut file).take(64 * 1024 + 1).read_to_end(&mut bytes)?;
        if bytes.len() > 64 * 1024 {
            return Err(TransferError::InvalidManifest(
                "deferred entry too large".into(),
            ));
        }
        let entry: Entry = serde_json::from_slice(&bytes)
            .map_err(|e| TransferError::InvalidManifest(e.to_string()))?;
        manifest::validate_relative(&entry.payload).map_err(TransferError::UnsafePath)?;
        if entry.payload != format!("{DIRECTORY}/{id}.bin") {
            return Err(TransferError::UnsafePath(entry.payload));
        }
        manifest::validate_manifest(&Manifest {
            files: vec![entry.file.clone()],
            ..Default::default()
        })
        .map_err(TransferError::InvalidManifest)?;
        if manifest::is_service_path(&entry.file.path) {
            return Err(TransferError::UnsafePath(entry.file.path));
        }
        if entry.scope != request.scope || !entry.game_id.eq_ignore_ascii_case(&request.game_id) {
            return Err(TransferError::PendingReplacement);
        }
        recovered = true;
        let payload = path_in(root, &entry.payload)?;
        let current = expected.iter().find(|file| {
            entry.version == request.version
                && file.path.eq_ignore_ascii_case(&entry.file.path)
                && file.size == entry.file.size
                && file.blake3.eq_ignore_ascii_case(&entry.file.blake3)
                && file.sha256.as_ref().map(|hash| hash.to_ascii_lowercase())
                    == entry
                        .file
                        .sha256
                        .as_ref()
                        .map(|hash| hash.to_ascii_lowercase())
        });
        let Some(current) = current else {
            // A new version supersedes the saved bytes. Do not restore content
            // that is no longer part of the current requested transfer plan.
            cleanup::remove_pending_file(&payload)?;
            drop(file);
            cleanup::remove_pending_file(&path)?;
            continue;
        };
        let destination = path_in(root, &current.path)?;
        if payload.try_exists()? {
            if verify_file(&payload, &entry.file, control)? {
                // Closing the locking program permits a verified immediate retry.
                match atomic_replace(&payload, &destination) {
                    Ok(()) => {}
                    Err(error) if is_locked(&error) => {
                        return Err(TransferError::ReplacementDeferred(entry.file.path));
                    }
                    Err(error) => return Err(error),
                }
            } else {
                // A corrupt cached payload must not prevent a fresh download.
                // Remove only the validated journal's private directory entry.
                control.checkpoint()?;
                cleanup::remove_pending_file(&payload)?;
            }
        }
        // Missing payload can follow a pre-rename crash or an interrupted recovery.
        // The normal transfer plan verifies the destination before publishing a version.
        drop(file);
        cleanup::remove_pending_file(&path)?;
    }
    Ok(recovered)
}

#[cfg(test)]
#[path = "deferred_tests.rs"]
mod tests;
