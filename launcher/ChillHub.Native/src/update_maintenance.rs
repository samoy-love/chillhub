//! Maintenance of app-owned update state. Unknown sessions are never deleted.
use super::*;
use fs2::FileExt;
use std::io;

pub fn update_attempt_count(paths: &UpdatePaths, version: &str) -> Result<u32, String> {
    let path = paths.config.join("update-attempts.json");
    check_no_links(&path).map_err(|e| e.to_string())?;
    let bytes = match fs::read(path) {
        Ok(bytes) if bytes.len() <= 4096 => bytes,
        Ok(_) => return Err("Не удалось прочитать историю обновления".into()),
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(0),
        Err(e) => return Err(format!("Не удалось прочитать историю обновления: {e}")),
    };
    let previous: UpdateAttempts = serde_json::from_slice(&bytes)
        .map_err(|_| "История обновления повреждена. Проверьте установку")?;
    Ok(if previous.version == version {
        previous.count
    } else {
        0
    })
}

pub fn ensure_update_allowed(paths: &UpdatePaths, version: &str) -> Result<(), String> {
    if update_attempt_count(paths, version)? >= 3 {
        return Err("Три попытки обновления не завершились. Сначала проверьте установку; повторная загрузка остановлена".into());
    }
    Ok(())
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct IntegrityReport {
    pub checked_files: usize,
    pub mismatches: Vec<String>,
}

/// Verifies the complete server manifest before repairing only the version marker.
/// No downloaded payload is executed or substituted by this operation.
pub fn verify_installed_release(
    release: &UpdateRelease,
    paths: &UpdatePaths,
    control: &TransferControl,
    progress: &(dyn Fn(TransferEvent) + Sync),
) -> Result<IntegrityReport, String> {
    paths.validate()?;
    trusted_url(&release.manifest_url)?;
    let engine = TransferEngine::for_update().map_err(|e| e.to_string())?;
    let manifest = engine
        .fetch_manifest(&release.manifest_url, control)
        .map_err(|e| e.to_string())?;
    if manifest.version != release.version
        || (!manifest.game_id.is_empty() && manifest.game_id != "launcher")
    {
        return Err("Манифест относится к другой версии".into());
    }
    verify_installed_manifest(paths, &manifest, control, progress)
}

fn verify_installed_manifest(
    paths: &UpdatePaths,
    manifest: &Manifest,
    control: &TransferControl,
    progress: &(dyn Fn(TransferEvent) + Sync),
) -> Result<IntegrityReport, String> {
    paths.validate()?;
    version_parts(&manifest.version)?;
    let _lock = ProcessLock::acquire(&update_mutex_name(&paths.install)?)?;
    let _install = DirectoryGuard::acquire(&paths.install, false).map_err(|e| e.to_string())?;
    let (files, _) = normalized_payload(manifest)?;
    let mut report = IntegrityReport {
        checked_files: files.len(),
        mismatches: Vec::new(),
    };
    let total_bytes = files.iter().map(|f| f.manifest.size).sum();
    let mut completed_bytes = 0;
    for (index, file) in files.iter().enumerate() {
        control.checkpoint().map_err(|e| e.to_string())?;
        progress(TransferEvent {
            stage: crate::transfer::TransferStage::Verifying,
            current_file: file.target.clone(),
            completed_bytes,
            total_bytes,
            network_bytes: 0,
            reused_bytes: 0,
            completed_files: index,
            total_files: files.len(),
        });
        if verify(&safe_join(&paths.install, &file.target)?, &file.manifest).is_err() {
            report.mismatches.push(file.target.clone());
        }
        completed_bytes += file.manifest.size;
    }
    control.checkpoint().map_err(|e| e.to_string())?;
    if report.mismatches.is_empty() {
        let marker = paths.install.join("launcher.version");
        let old_attributes = file_attributes(&marker)?;
        clear_readonly(&marker)?;
        if let Err(error) = atomic_write(&marker, manifest.version.as_bytes()) {
            let _ = restore_attributes(&marker, old_attributes);
            return Err(format!(
                "Файлы в порядке, но сохранить номер версии не удалось: {error}"
            ));
        }
        let attempts = paths.config.join("update-attempts.json");
        if attempts.exists() {
            remove_file(&attempts)?;
        }
    }
    progress(TransferEvent {
        stage: crate::transfer::TransferStage::Completed,
        current_file: String::new(),
        completed_bytes,
        total_bytes,
        network_bytes: 0,
        reused_bytes: 0,
        completed_files: files.len(),
        total_files: files.len(),
    });
    Ok(report)
}

pub(super) fn retry_io<T>(
    attempts: usize,
    delay: Duration,
    mut operation: impl FnMut() -> io::Result<T>,
) -> io::Result<T> {
    for attempt in 0..attempts.max(1) {
        match operation() {
            Ok(value) => return Ok(value),
            Err(error) => {
                // Sharing/lock violations are temporary. ACL denial, missing files,
                // disk-full and invalid data do not become valid by waiting.
                let transient = matches!(error.raw_os_error(), Some(32 | 33 | 1224))
                    || matches!(
                        error.kind(),
                        io::ErrorKind::WouldBlock | io::ErrorKind::Interrupted
                    );
                if !transient || attempt + 1 >= attempts {
                    return Err(error);
                }
                std::thread::sleep(delay.saturating_mul(1 << attempt.min(3)));
            }
        }
    }
    unreachable!()
}

pub(super) fn file_attributes(path: &Path) -> Result<Option<u32>, String> {
    check_no_links(path).map_err(|e| e.to_string())?;
    let meta = match fs::metadata(path) {
        Ok(meta) => meta,
        Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(e.to_string()),
    };
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        Ok(Some(meta.file_attributes()))
    }
    #[cfg(not(windows))]
    {
        Ok(Some(u32::from(meta.permissions().readonly())))
    }
}

pub(super) fn restore_attributes(path: &Path, attributes: Option<u32>) -> Result<(), String> {
    let Some(attributes) = attributes else {
        return Ok(());
    };
    check_no_links(path).map_err(|e| e.to_string())?;
    #[cfg(windows)]
    {
        use std::os::windows::ffi::OsStrExt;
        use windows_sys::Win32::Storage::FileSystem::SetFileAttributesW;
        let path: Vec<_> = path.as_os_str().encode_wide().chain(Some(0)).collect();
        if unsafe { SetFileAttributesW(path.as_ptr(), attributes) } == 0 {
            return Err(io::Error::last_os_error().to_string());
        }
    }
    #[cfg(not(windows))]
    {
        let mut permissions = fs::metadata(path).map_err(|e| e.to_string())?.permissions();
        permissions.set_readonly(attributes != 0);
        fs::set_permissions(path, permissions).map_err(|e| e.to_string())?;
    }
    Ok(())
}

pub(super) fn clear_readonly(path: &Path) -> Result<(), String> {
    if let Some(attributes) = file_attributes(path)?
        && attributes & 1 != 0
    {
        let cleared = attributes & !1;
        #[cfg(windows)]
        let cleared = if cleared == 0 { 0x80 } else { cleared };
        restore_attributes(path, Some(cleared))?;
    }
    Ok(())
}

pub(super) fn remove_file(path: &Path) -> Result<(), String> {
    clear_readonly(path)?;
    retry_io(5, Duration::from_millis(200), || fs::remove_file(path)).map_err(|e| e.to_string())
}

#[derive(Serialize, Deserialize)]
struct StageProof {
    schema: u32,
    session: PathBuf,
    install: PathBuf,
    version: String,
    phase: String,
}

/// The lock is outside the removable session, so it remains held during deletion.
pub(super) struct SessionLease {
    _file: fs::File,
}
impl SessionLease {
    pub(super) fn acquire(paths: &UpdatePaths, session: &Path) -> Result<Self, String> {
        if !same_path(session.parent().ok_or("Нет папки сессий")?, &paths.staging) {
            return Err("Сессия находится вне папки обновлений".into());
        }
        let key = format!("{:x}", Sha256::digest(path_key(session)?.as_bytes()));
        let leases = paths.staging.join(".leases");
        let _guard = DirectoryGuard::acquire(&leases, true).map_err(|e| e.to_string())?;
        let path = leases.join(format!("{key}.lock"));
        check_no_links(&path).map_err(|e| e.to_string())?;
        let file = fs::OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(path)
            .map_err(|e| e.to_string())?;
        file.try_lock_exclusive()
            .map_err(|_| "Эта сессия обновления используется")?;
        Ok(Self { _file: file })
    }
}

pub(super) struct StageLease {
    proof: StageProof,
    _lease: SessionLease,
}
impl StageLease {
    pub(super) fn begin(
        paths: &UpdatePaths,
        session: &Path,
        version: &str,
    ) -> Result<Self, String> {
        let lease = SessionLease::acquire(paths, session)?;
        let _guard = DirectoryGuard::acquire(session, true).map_err(|e| e.to_string())?;
        let proof = StageProof {
            schema: 1,
            session: session.into(),
            install: paths.install.clone(),
            version: version.into(),
            phase: "downloading".into(),
        };
        atomic_write(
            &session.join("stage-owner.json"),
            &serde_json::to_vec(&proof).map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
        Ok(Self {
            proof,
            _lease: lease,
        })
    }
    pub(super) fn prepared(mut self) -> Result<(), String> {
        self.proof.phase = "prepared".into();
        atomic_write(
            &self.proof.session.join("stage-owner.json"),
            &serde_json::to_vec(&self.proof).map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())
    }
}

pub(super) fn reuse_verified_payload(
    paths: &UpdatePaths,
    files: &[StagedFile],
    payload: &Path,
    control: &TransferControl,
    progress: &(dyn Fn(TransferEvent) + Sync),
) -> Result<(), String> {
    let mut donors = Vec::new();
    let mut leases = Vec::new();
    if let Ok(entries) = fs::read_dir(&paths.staging) {
        for entry in entries.flatten().take(128) {
            let session = entry.path();
            if payload.parent().is_some_and(|p| same_path(p, &session)) {
                continue;
            }
            let Some(proof) = read_small::<StageProof>(&session.join("stage-owner.json")) else {
                continue;
            };
            if proof.schema != 1
                || !same_path(&proof.install, &paths.install)
                || !same_path(&proof.session, &session)
            {
                continue;
            }
            let Ok(lease) = SessionLease::acquire(paths, &session) else {
                continue;
            };
            donors.push(session.join("payload"));
            leases.push(lease);
        }
    }
    let total_bytes = files.iter().map(|f| f.manifest.size).sum();
    let mut reused = 0;
    let mut checked = 0;
    for (index, file) in files.iter().enumerate() {
        control.checkpoint().map_err(|e| e.to_string())?;
        let mut candidates = vec![safe_join(&paths.install, &file.target)?];
        for donor in &donors {
            candidates.push(safe_join(donor, &file.source)?);
        }
        for source in candidates {
            if verify(&source, &file.manifest).is_err() {
                continue;
            }
            let target = safe_join(payload, &file.source)?;
            copy_synced(&source, &target)?;
            // Donors are untrusted until the complete copied bytes match.
            if verify(&target, &file.manifest).is_ok() {
                reused += file.manifest.size;
                break;
            }
            remove_file(&target)?;
        }
        checked += file.manifest.size;
        progress(TransferEvent {
            stage: crate::transfer::TransferStage::Checking,
            current_file: file.target.clone(),
            completed_bytes: checked,
            total_bytes,
            network_bytes: 0,
            reused_bytes: reused,
            completed_files: index + 1,
            total_files: files.len(),
        });
    }
    drop(leases);
    Ok(())
}

/// Returns successfully reclaimed sessions. Unknown directories, prepared jobs
/// and interrupted journals remain available for explicit recovery.
pub fn cleanup_completed_sessions(paths: &UpdatePaths) -> Result<usize, String> {
    paths.validate()?;
    if !paths.staging.is_dir() {
        return Ok(0);
    }
    let _guard = DirectoryGuard::acquire(&paths.staging, false).map_err(|e| e.to_string())?;
    let mut removed = 0;
    for item in fs::read_dir(&paths.staging).map_err(|e| e.to_string())? {
        let item = item.map_err(|e| e.to_string())?;
        if !item.file_type().map_err(|e| e.to_string())?.is_dir() {
            continue;
        }
        let session = item.path();
        if session.file_name().is_some_and(|n| n == ".leases") {
            continue;
        }
        if check_no_links(&session).is_err() {
            continue;
        }
        let Ok(_lease) = SessionLease::acquire(paths, &session) else {
            continue;
        };
        #[cfg(windows)]
        if !matches!(crate::playtime::processes_in(&session), Ok(processes) if processes.is_empty())
        {
            continue;
        }
        let removable = if session.join("journal.json").is_file() {
            let journal: Option<Journal> = read_small(&session.join("journal.json"));
            let job: Option<UpdateJob> = read_small(&session.join("work/job.json"));
            matches!((journal, job), (Some(journal), Some(job)) if
                (journal.committed || journal.recovered) &&
                validate_staged(&job.staged).is_ok() &&
                same_path(&job.staged.paths.install, &paths.install) &&
                same_path(&job.staged.session, &session))
        } else if !session.join("work/job.json").exists() {
            read_small::<StageProof>(&session.join("stage-owner.json")).is_some_and(|proof| {
                proof.schema == 1
                    && proof.phase == "downloading"
                    && same_path(&proof.session, &session)
                    && same_path(&proof.install, &paths.install)
                    && version_parts(&proof.version).is_ok()
            })
        } else {
            false
        };
        if removable && remove_owned_tree(&session).is_ok() {
            removed += 1;
        }
    }
    Ok(removed)
}

fn read_small<T: serde::de::DeserializeOwned>(path: &Path) -> Option<T> {
    let mut file = crate::manifest::open_regular(path, false, false, false).ok()?;
    if file.metadata().ok()?.len() > 16 * 1024 * 1024 {
        return None;
    }
    let mut bytes = Vec::new();
    file.read_to_end(&mut bytes).ok()?;
    serde_json::from_slice(&bytes).ok()
}

fn remove_owned_tree(path: &Path) -> Result<(), String> {
    let guard = DirectoryGuard::acquire(path, false).map_err(|e| e.to_string())?;
    for entry in fs::read_dir(path).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        check_no_links(&entry.path()).map_err(|e| e.to_string())?;
        if entry.file_type().map_err(|e| e.to_string())?.is_dir() {
            remove_owned_tree(&entry.path())?;
        } else {
            remove_file(&entry.path())?;
        }
    }
    drop(guard);
    fs::remove_dir(path).map_err(|e| e.to_string())
}

pub(super) fn cleanup_legacy_artifacts(install: &Path) {
    for name in [
        "filelist.txt",
        "emptydirs.txt",
        "deletelist.txt",
        "apply-update.log",
        "apply-update.cmd",
    ] {
        let path = install.join(name);
        if path.is_file() {
            let _ = remove_file(&path);
        }
    }
    let updater = install.join("updater");
    if updater.is_dir() {
        let _ = remove_owned_tree(&updater);
    }
    // Old transaction suffixes are restricted to known, still-existing siblings.
    for entry in walkdir::WalkDir::new(install)
        .follow_links(false)
        .into_iter()
        .filter_map(Result::ok)
    {
        if !entry.file_type().is_file() {
            continue;
        }
        let name = entry.file_name().to_string_lossy();
        if let Some(base) = name
            .strip_suffix(".chtmp")
            .or_else(|| name.strip_suffix(".chbak"))
            && entry.path().with_file_name(base).is_file()
        {
            let _ = remove_file(entry.path());
        }
    }
}

#[cfg(windows)]
pub(super) fn refresh_shell_icons(exe: &Path) {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::UI::Shell::{
        SHCNE_ASSOCCHANGED, SHCNE_UPDATEITEM, SHCNF_IDLIST, SHCNF_PATHW, SHChangeNotify,
    };
    let path: Vec<_> = exe.as_os_str().encode_wide().chain(Some(0)).collect();
    unsafe {
        SHChangeNotify(
            SHCNE_UPDATEITEM as i32,
            SHCNF_PATHW,
            path.as_ptr().cast(),
            std::ptr::null(),
        );
        SHChangeNotify(
            SHCNE_ASSOCCHANGED as i32,
            SHCNF_IDLIST,
            std::ptr::null(),
            std::ptr::null(),
        );
    }
}
#[cfg(not(windows))]
pub(super) fn refresh_shell_icons(_: &Path) {}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(windows)]
    #[test]
    fn session_lease_is_exclusive_across_short_and_canonical_paths() {
        let temp = tempfile::tempdir().unwrap();
        let staged = super::super::tests::package(temp.path());
        let mut short_paths = staged.paths.clone();
        short_paths.staging = crate::paths::short_test_path(&staged.paths.staging);
        let short_session = short_paths
            .staging
            .join(staged.session.file_name().unwrap());
        let mut long_paths = staged.paths.clone();
        long_paths.staging = fs::canonicalize(&staged.paths.staging).unwrap();
        let long_session = long_paths.staging.join(staged.session.file_name().unwrap());
        let first = SessionLease::acquire(&short_paths, &short_session).unwrap();
        assert!(SessionLease::acquire(&long_paths, &long_session).is_err());
        drop(first);
        assert!(SessionLease::acquire(&long_paths, &long_session).is_ok());
    }

    #[test]
    fn retries_only_temporary_io_and_stops_at_bound() {
        let mut calls = 0;
        let result = retry_io(3, Duration::ZERO, || {
            calls += 1;
            if calls < 3 {
                Err(io::Error::from_raw_os_error(32))
            } else {
                Ok(7)
            }
        });
        assert_eq!(result.unwrap(), 7);
        assert_eq!(calls, 3);
        let mut calls = 0;
        assert!(
            retry_io(5, Duration::ZERO, || {
                calls += 1;
                Err::<(), _>(io::Error::from_raw_os_error(5))
            })
            .is_err()
        );
        assert_eq!(calls, 1);
    }

    #[test]
    fn loop_guard_and_integrity_repair_require_every_file() {
        let temp = tempfile::tempdir().unwrap();
        let staged = super::super::tests::package(temp.path());
        atomic_write(
            &staged.paths.config.join("update-attempts.json"),
            br#"{"version":"1.8.1","count":3}"#,
        )
        .unwrap();
        assert!(ensure_update_allowed(&staged.paths, "1.8.1").is_err());
        let manifest = Manifest {
            game_id: "launcher".into(),
            version: staged.version.clone(),
            files: staged.files.iter().map(|f| f.manifest.clone()).collect(),
            ..Default::default()
        };
        assert!(
            !verify_installed_manifest(
                &staged.paths,
                &manifest,
                &TransferControl::default(),
                &|_| {}
            )
            .unwrap()
            .mismatches
            .is_empty()
        );
        assert_eq!(update_attempt_count(&staged.paths, "1.8.1").unwrap(), 3);
        assert!(!staged.paths.install.join("launcher.version").exists());
        for file in &staged.files {
            fs::copy(
                staged.session.join("payload").join(&file.source),
                staged.paths.install.join(&file.target),
            )
            .unwrap();
        }
        assert!(
            verify_installed_manifest(
                &staged.paths,
                &manifest,
                &TransferControl::default(),
                &|_| {}
            )
            .unwrap()
            .mismatches
            .is_empty()
        );
        assert_eq!(update_attempt_count(&staged.paths, "1.8.1").unwrap(), 0);
        assert_eq!(
            fs::read_to_string(staged.paths.install.join("launcher.version")).unwrap(),
            "1.8.1"
        );
    }

    #[test]
    fn cleanup_keeps_unknown_active_and_prepared_sessions() {
        let temp = tempfile::tempdir().unwrap();
        let staged = super::super::tests::package(temp.path());
        let downloading = staged.paths.staging.join("owned-failed");
        let lease = StageLease::begin(&staged.paths, &downloading, "1.8.2").unwrap();
        fs::write(downloading.join("partial.bin"), "partial").unwrap();
        assert_eq!(cleanup_completed_sessions(&staged.paths).unwrap(), 0);
        drop(lease);
        assert_eq!(cleanup_completed_sessions(&staged.paths).unwrap(), 1);
        assert!(staged.session.exists());
        let prepared = staged.paths.staging.join("owned-prepared");
        StageLease::begin(&staged.paths, &prepared, "1.8.2")
            .unwrap()
            .prepared()
            .unwrap();
        assert_eq!(cleanup_completed_sessions(&staged.paths).unwrap(), 0);
        assert!(prepared.exists());
    }

    #[test]
    fn readonly_replacement_rollback_restores_attributes() {
        let temp = tempfile::tempdir().unwrap();
        let mut staged = super::super::tests::package(temp.path());
        let target = staged.paths.install.join(LAUNCHER_NAME);
        restore_attributes(&target, Some(1)).unwrap();
        fs::write(staged.paths.install.join("occupied"), "keep").unwrap();
        staged.empty_dirs.push("occupied".into());
        assert!(apply_transaction(&staged, &|_, _| {}).is_err());
        assert_eq!(fs::read_to_string(&target).unwrap(), "old");
        assert!(fs::metadata(&target).unwrap().permissions().readonly());
        clear_readonly(&target).unwrap();
    }

    #[test]
    fn outcome_acknowledgement_keeps_newer_unseen_failure() {
        let temp = tempfile::tempdir().unwrap();
        let staged = super::super::tests::package(temp.path());
        let first = UpdateOutcome {
            version: "1.8.1".into(),
            success: false,
            message: "first".into(),
        };
        write_outcome(&staged.paths, &first).unwrap();
        let shown = previous_outcome(&staged.paths).unwrap();
        let next = UpdateOutcome {
            message: "second".into(),
            ..first
        };
        write_outcome(&staged.paths, &next).unwrap();
        assert!(!acknowledge_outcome(&staged.paths, &shown).unwrap());
        assert_eq!(previous_outcome(&staged.paths), Some(next.clone()));
        assert!(acknowledge_outcome(&staged.paths, &next).unwrap());
        assert!(previous_outcome(&staged.paths).is_none());
    }

    #[test]
    fn payload_reuses_only_full_hash_matches_from_installed_and_owned_staging() {
        let temp = tempfile::tempdir().unwrap();
        let staged = super::super::tests::package(temp.path());
        let donor = staged.paths.staging.join("old-stage");
        let lease = StageLease::begin(&staged.paths, &donor, "1.8.2").unwrap();
        fs::create_dir_all(donor.join("payload")).unwrap();
        fs::copy(
            staged.session.join("payload").join(UPDATER_NAME),
            donor.join("payload").join(UPDATER_NAME),
        )
        .unwrap();
        drop(lease);
        fs::copy(
            staged.session.join("payload").join(LAUNCHER_NAME),
            staged.paths.install.join(LAUNCHER_NAME),
        )
        .unwrap();
        let payload = staged.paths.staging.join("new-stage/payload");
        fs::create_dir_all(&payload).unwrap();
        reuse_verified_payload(
            &staged.paths,
            &staged.files,
            &payload,
            &TransferControl::default(),
            &|_| {},
        )
        .unwrap();
        assert!(payload.join(LAUNCHER_NAME).exists());
        assert!(payload.join(UPDATER_NAME).exists());
        assert!(!payload.join("ChillHub.SecurityHelper.exe").exists());
    }
}
