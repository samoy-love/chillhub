//! Immutable verified payloads scheduled by Windows, recovered before replanning.
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

pub(super) fn schedule(request: &TransferRequest, part: &Path, file: &ManifestFile) -> Result<()> {
    stage(request, part, file, schedule_windows)
}

fn stage(
    request: &TransferRequest,
    part: &Path,
    file: &ManifestFile,
    schedule: impl FnOnce(&Path, &Path) -> io::Result<()>,
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
        file: file.clone(),
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
    let destination = path_in(root, &file.path)?;
    if let Err(error) = schedule(&saved, &destination) {
        // Normal user installations may lack the right to schedule a reboot move.
        // Restore the verified partial so retry does not redownload it.
        atomic_replace(&saved, part)?;
        remove_regular(&journal)?;
        return Err(TransferError::Io(error));
    }
    Err(TransferError::RebootRequired(file.path.clone()))
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
            // A new version supersedes the saved bytes. Remove the unique
            // source so a previously registered Windows move becomes a no-op.
            if payload.try_exists()? {
                remove_regular(&payload)?;
            }
            drop(file);
            remove_regular(&path)?;
            continue;
        };
        let destination = path_in(root, &current.path)?;
        if payload.try_exists()? {
            if !verify_file(&payload, &entry.file, control)? {
                return Err(TransferError::Integrity(entry.file.path));
            }
            // Closing the locking program permits an immediate retry. Consuming
            // this unique payload makes any pending Windows rename a harmless no-op.
            match atomic_replace(&payload, &destination) {
                Ok(()) => {}
                Err(error) if is_locked(&error) => {
                    return Err(TransferError::RebootRequired(entry.file.path));
                }
                Err(error) => return Err(error),
            }
        }
        // Missing payload is either a completed boot move or a pre-rename crash.
        // The normal transfer plan verifies the destination before publishing a version.
        drop(file);
        remove_regular(&path)?;
    }
    Ok(recovered)
}

#[cfg(windows)]
fn schedule_windows(source: &Path, destination: &Path) -> io::Result<()> {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Storage::FileSystem::{
        MOVEFILE_DELAY_UNTIL_REBOOT, MOVEFILE_REPLACE_EXISTING, MoveFileExW,
    };
    let source: Vec<u16> = source.as_os_str().encode_wide().chain([0]).collect();
    let destination: Vec<u16> = destination.as_os_str().encode_wide().chain([0]).collect();
    if unsafe {
        MoveFileExW(
            source.as_ptr(),
            destination.as_ptr(),
            MOVEFILE_DELAY_UNTIL_REBOOT | MOVEFILE_REPLACE_EXISTING,
        )
    } == 0
    {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}
#[cfg(not(windows))]
fn schedule_windows(_: &Path, _: &Path) -> io::Result<()> {
    Err(io::Error::new(
        io::ErrorKind::Unsupported,
        "Reboot replacement requires Windows",
    ))
}

#[cfg(test)]
#[path = "deferred_tests.rs"]
mod tests;
