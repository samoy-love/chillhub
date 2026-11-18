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
fn cleanup_removes_only_finished_sessions_owned_by_this_installation() {
    let temp = tempfile::tempdir().unwrap();
    let staged = super::super::tests::package(temp.path());
    let mut job = UpdateJob {
        parent_pid: 1,
        parent_executable: staged.paths.install.join(LAUNCHER_NAME),
        restart_args: vec![],
        mode: UpdateMode::Apply,
        token: uuid::Uuid::new_v4().to_string(),
        staged: staged.clone(),
    };
    let work = prepare_worker(&job).unwrap();
    let journal_path = staged.session.join("journal.json");
    let finished = Journal {
        committed: true,
        recovered: false,
        files: vec![],
    };
    let unfinished = Journal {
        committed: false,
        recovered: false,
        files: vec![],
    };
    let put_journal = |journal: &Journal| {
        fs::write(&journal_path, serde_json::to_vec(journal).unwrap()).unwrap();
    };
    let sibling = staged.paths.staging.join("owned-by-another-install");
    fs::create_dir(&sibling).unwrap();
    job.staged.session = sibling.clone();
    job.staged.paths.install = temp.path().join("other-install");
    fs::create_dir(&job.staged.paths.install).unwrap();
    fs::create_dir(sibling.join("work")).unwrap();
    fs::write(
        sibling.join("work/job.json"),
        serde_json::to_vec(&job).unwrap(),
    )
    .unwrap();
    fs::write(
        sibling.join("journal.json"),
        serde_json::to_vec(&finished).unwrap(),
    )
    .unwrap();
    let loose = staged.paths.staging.join("personal-note.txt");
    fs::write(&loose, "keep").unwrap();

    assert_eq!(cleanup_completed_sessions(&staged.paths).unwrap(), 0);
    assert!(
        work.job_file.is_file(),
        "prepared session has no journal yet"
    );
    put_journal(&unfinished);
    assert_eq!(cleanup_completed_sessions(&staged.paths).unwrap(), 0);
    assert!(
        staged.session.is_dir(),
        "interrupted session still needs recovery"
    );

    put_journal(&finished);
    assert_eq!(cleanup_completed_sessions(&staged.paths).unwrap(), 1);
    assert!(!staged.session.exists());
    assert!(sibling.join("work/job.json").is_file());
    assert_eq!(fs::read(loose).unwrap(), b"keep");
}

#[test]
fn corrupt_attempt_history_never_silently_enables_an_update() {
    let temp = tempfile::tempdir().unwrap();
    let staged = super::super::tests::package(temp.path());
    fs::create_dir(&staged.paths.config).unwrap();
    let attempts = staged.paths.config.join("update-attempts.json");
    assert_eq!(
        update_attempt_count(&staged.paths, &staged.version).unwrap(),
        0
    );
    fs::write(&attempts, br#"{"version":"1.8.0","count":3}"#).unwrap();
    assert_eq!(
        update_attempt_count(&staged.paths, &staged.version).unwrap(),
        0
    );

    for (data, expected) in [
        (b"{".to_vec(), "История обновления повреждена"),
        (vec![b' '; 4097], "Не удалось прочитать историю обновления"),
    ] {
        fs::write(&attempts, &data).unwrap();
        let error = ensure_update_allowed(&staged.paths, &staged.version).unwrap_err();
        assert!(error.contains(expected), "{error}");
        assert_eq!(fs::read(&attempts).unwrap(), data);
    }
    fs::remove_file(&attempts).unwrap();
    fs::create_dir(&attempts).unwrap();
    assert!(ensure_update_allowed(&staged.paths, &staged.version).is_err());
    assert!(attempts.is_dir());
}

#[test]
fn cleanup_removes_known_legacy_artifacts_and_keeps_unpaired_siblings() {
    let temp = tempfile::tempdir().unwrap();
    let install = temp.path().join("launcher");
    fs::create_dir_all(install.join("updater/old")).unwrap();
    fs::create_dir(install.join("Data")).unwrap();
    for file in [
        "filelist.txt",
        "emptydirs.txt",
        "deletelist.txt",
        "apply-update.log",
        "apply-update.cmd",
        "updater/old/launcher.exe",
        "Data/art.png",
        "Data/art.png.chtmp",
        "Data/art.png.chbak",
        "Data/personal.chbak",
        "Data/filelist.txt",
    ] {
        fs::write(install.join(file), file).unwrap();
    }
    cleanup_legacy_artifacts(&install);

    for removed in [
        "filelist.txt",
        "emptydirs.txt",
        "deletelist.txt",
        "apply-update.log",
        "apply-update.cmd",
        "updater",
        "Data/art.png.chtmp",
        "Data/art.png.chbak",
    ] {
        assert!(!install.join(removed).exists(), "{removed}");
    }
    for kept in ["Data/art.png", "Data/personal.chbak", "Data/filelist.txt"] {
        assert_eq!(fs::read_to_string(install.join(kept)).unwrap(), kept);
    }
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
        legacy_fingerprint: None,
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
