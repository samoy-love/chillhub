use super::*;
fn request(root: &Path) -> TransferRequest {
    TransferRequest {
        root: root.to_owned(),
        game_id: "game".into(),
        version: "1".into(),
        ..Default::default()
    }
}
fn expected() -> Manifest {
    Manifest {
        files: vec![ManifestFile {
            path: "game.dll".into(),
            size: 3,
            blake3: blake3::hash(b"new").to_hex().to_string(),
            ..Default::default()
        }],
        ..Default::default()
    }
}
fn stage(
    root: &Path,
    part: &Path,
    file: &ManifestFile,
    scheduler: impl FnOnce(&Path, &Path) -> io::Result<()>,
) -> Result<()> {
    super::stage(&request(root), part, file, scheduler)
}
fn recover(root: &Path, control: &TransferControl) -> Result<bool> {
    super::recover(&request(root), &expected().files, control)
}
fn fixture() -> (tempfile::TempDir, PathBuf, ManifestFile) {
    let temp = tempfile::tempdir().unwrap();
    let part = temp.path().join("game.dll.part");
    fs::write(&part, b"new").unwrap();
    fs::write(temp.path().join("game.dll"), b"old").unwrap();
    let file = ManifestFile {
        path: "game.dll".into(),
        size: 3,
        blake3: blake3::hash(b"new").to_hex().to_string(),
        ..Default::default()
    };
    (temp, part, file)
}
#[test]
fn scheduled_payload_is_unique_and_recovered_before_next_plan() {
    let (temp, part, file) = fixture();
    let result = stage(temp.path(), &part, &file, |source, target| {
        assert_ne!(source, part);
        assert_eq!(fs::read(source).unwrap(), b"new");
        assert_eq!(target, temp.path().join("game.dll"));
        Ok(())
    });
    assert!(matches!(result, Err(TransferError::RebootRequired(_))));
    assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
    recover(temp.path(), &TransferControl::default()).unwrap();
    assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"new");
    recover(temp.path(), &TransferControl::default()).unwrap();
    assert!(!part.exists());
}
#[test]
fn denied_scheduler_restores_partial_and_does_not_report_success() {
    let (temp, part, file) = fixture();
    assert!(matches!(
        stage(temp.path(), &part, &file, |_, _| Err(
            io::Error::from_raw_os_error(5)
        )),
        Err(TransferError::Io(_))
    ));
    assert_eq!(fs::read(part).unwrap(), b"new");
    assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
    recover(temp.path(), &TransferControl::default()).unwrap();
    assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
}
#[test]
fn tampered_pending_payload_cannot_be_promoted() {
    let (temp, part, file) = fixture();
    let _ = stage(temp.path(), &part, &file, |source, _| {
        fs::write(source, b"bad").unwrap();
        Ok(())
    });
    assert!(matches!(
        recover(temp.path(), &TransferControl::default()),
        Err(TransferError::Integrity(_))
    ));
    assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
}
#[test]
fn only_windows_sharing_errors_are_deferred() {
    for code in [32, 33] {
        assert!(is_locked(&TransferError::Io(io::Error::from_raw_os_error(
            code
        ))));
    }
    for code in [5, 112, 225] {
        assert!(!is_locked(&TransferError::Io(
            io::Error::from_raw_os_error(code)
        )));
    }
    assert!(!is_locked(&TransferError::Cancelled));
}

fn journal_path(root: &Path) -> PathBuf {
    fs::read_dir(root.join(DIRECTORY))
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .find(|path| path.extension().is_some_and(|ext| ext == "json"))
        .unwrap()
}

#[test]
fn missing_payload_after_boot_leaves_destination_for_regular_verification() {
    let (temp, part, file) = fixture();
    let mut saved = None;
    let _ = stage(temp.path(), &part, &file, |source, _| {
        saved = Some(source.to_owned());
        Ok(())
    });
    let journal = journal_path(temp.path());
    fs::remove_file(saved.unwrap()).unwrap();
    recover(temp.path(), &TransferControl::default()).unwrap();
    assert!(!journal.exists());
    assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
    assert!(!temp.path().join("game.version").exists());
}

#[test]
fn cancellation_and_existing_transfer_leave_pending_files_untouched() {
    let (temp, part, file) = fixture();
    let _ = stage(temp.path(), &part, &file, |_, _| Ok(()));
    let journal = journal_path(temp.path());
    let control = TransferControl::default();
    control.cancel();
    assert!(matches!(
        recover(temp.path(), &control),
        Err(TransferError::Cancelled)
    ));
    assert!(journal.exists());
    let lock = open_regular(&temp.path().join(".chillhub.lock"), true, true, false).unwrap();
    lock.lock_exclusive().unwrap();
    let result = recover(temp.path(), &TransferControl::default());
    assert!(matches!(result, Err(TransferError::Busy)), "{result:?}");
    assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
}

#[test]
fn journal_cannot_promote_paths_outside_its_payload_directory_or_into_service_files() {
    for (payload, target) in [
        ("../escape.bin", "game.dll"),
        ("other.bin", "game.dll"),
        (".chillhub-reboot/nested/file.bin", "game.dll"),
        (".chillhub-reboot/file.bin", "../outside.dll"),
        (".chillhub-reboot/file.bin", ".chillhub.lock"),
        (".chillhub-reboot/file.bin", ".chillhub-reboot/other.bin"),
    ] {
        let (temp, part, mut file) = fixture();
        let _ = stage(temp.path(), &part, &file, |_, _| Ok(()));
        file.path = target.into();
        fs::write(
            journal_path(temp.path()),
            serde_json::to_vec(&Entry {
                payload: payload.into(),
                file,
                game_id: "game".into(),
                version: "1".into(),
                scope: SyncScope::WholeRoot,
            })
            .unwrap(),
        )
        .unwrap();
        assert!(
            recover(temp.path(), &TransferControl::default()).is_err(),
            "{payload} -> {target}"
        );
        assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
    }
}

#[test]
fn malformed_or_oversized_journal_fails_closed() {
    for bytes in [b"{broken".to_vec(), vec![b' '; 64 * 1024 + 1]] {
        let (temp, part, file) = fixture();
        let _ = stage(temp.path(), &part, &file, |_, _| Ok(()));
        fs::write(journal_path(temp.path()), bytes).unwrap();
        assert!(matches!(
            recover(temp.path(), &TransferControl::default()),
            Err(TransferError::InvalidManifest(_))
        ));
        assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
    }
}

#[test]
fn invalid_manifest_digest_in_journal_is_rejected_before_payload_recovery() {
    let (temp, part, _) = fixture();
    let file = ManifestFile {
        path: "game.dll".into(),
        size: 3,
        blake3: "not-a-digest".into(),
        ..Default::default()
    };
    let _ = stage(temp.path(), &part, &file, |_, _| Ok(()));
    let journal = journal_path(temp.path());
    assert!(matches!(
        recover(temp.path(), &TransferControl::default()),
        Err(TransferError::InvalidManifest(_))
    ));
    assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
    assert!(journal.exists());
}

#[test]
fn superseded_versions_and_digests_never_promote_old_payloads() {
    for change in ["version", "digest", "size", "removed"] {
        let (temp, part, file) = fixture();
        let _ = stage(temp.path(), &part, &file, |_, _| Ok(()));
        let mut requested = request(temp.path());
        let mut manifest = expected();
        match change {
            "version" => requested.version = "2".into(),
            "digest" => manifest.files[0].blake3 = blake3::hash(b"v02").to_hex().to_string(),
            "size" => manifest.files[0].size += 1,
            _ => manifest.files.clear(),
        }
        assert!(super::recover(&requested, &manifest.files, &TransferControl::default()).unwrap());
        assert_eq!(
            fs::read(temp.path().join("game.dll")).unwrap(),
            b"old",
            "{change}"
        );
        // No source remains for an old registered boot move.
        assert_eq!(
            fs::read_dir(temp.path().join(DIRECTORY)).unwrap().count(),
            0
        );
    }
}

#[test]
fn another_game_or_modpack_cannot_replay_a_pending_base_update() {
    for change_scope in [false, true] {
        let (temp, part, file) = fixture();
        let _ = stage(temp.path(), &part, &file, |_, _| Ok(()));
        let mut requested = request(temp.path());
        if change_scope {
            requested.scope = SyncScope::OwnFilesOnly;
        } else {
            requested.game_id = "another-game".into();
        }
        assert!(matches!(
            super::recover(&requested, &expected().files, &TransferControl::default()),
            Err(TransferError::PendingReplacement)
        ));
        assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
        assert!(journal_path(temp.path()).exists());
    }
}

#[test]
fn unavailable_current_manifest_cannot_apply_a_pending_replacement() {
    let (temp, part, file) = fixture();
    let _ = stage(temp.path(), &part, &file, |_, _| Ok(()));
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let mut requested = request(temp.path());
    requested.manifest_url = format!("http://{}/manifest.json", server.server_addr());
    requested.content_base_url = format!("http://{}/content/", server.server_addr());
    let response = std::thread::spawn(move || {
        let request = server
            .recv_timeout(Duration::from_secs(10))
            .unwrap()
            .unwrap();
        request.respond(tiny_http::Response::empty(503)).unwrap();
    });
    assert!(
        TransferEngine::new()
            .unwrap()
            .prepare_plan(&requested, &TransferControl::default(), &|_| {})
            .is_err()
    );
    response.join().unwrap();
    assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
    assert!(journal_path(temp.path()).exists());
}

#[test]
fn newly_protected_files_retire_pending_payload_without_overwriting_settings() {
    for foreign in [false, true] {
        let (temp, part, file) = fixture();
        let _ = stage(temp.path(), &part, &file, |_, _| Ok(()));
        let mut requested = request(temp.path());
        requested.manifest_url = "http://127.0.0.1/manifest".into();
        requested.content_base_url = "http://127.0.0.1/content/".into();
        if foreign {
            requested.foreign_paths = vec![file.path.clone()];
        } else {
            requested.preserve_paths = vec![file.path.clone()];
        }
        let control = TransferControl::default();
        let plan = TransferEngine::new()
            .unwrap()
            .plan_manifest(&requested, expected(), &control, &|_| {})
            .unwrap();
        assert!(plan.downloads.is_empty());
        assert!(super::recover(&requested, &plan.downloads, &control).unwrap());
        assert_eq!(fs::read(temp.path().join("game.dll")).unwrap(), b"old");
        assert_eq!(
            fs::read_dir(temp.path().join(DIRECTORY)).unwrap().count(),
            0
        );
    }
}
