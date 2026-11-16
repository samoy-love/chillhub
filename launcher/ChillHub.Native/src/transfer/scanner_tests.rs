use super::*;
const BLOCK: usize = 64 * 1024;

#[cfg(windows)]
#[test]
fn antivirus_evidence_does_not_invent_quarantine_for_permissions_or_busy_files() {
    for code in [225, 226] {
        assert!(
            matches!(antivirus_error(io::Error::from_raw_os_error(code).into(), "mod.dll"), TransferError::AntivirusBlocked(path) if path == "mod.dll")
        );
    }
    for code in [5, 32, 33] {
        assert!(matches!(
            antivirus_error(io::Error::from_raw_os_error(code).into(), "mod.dll"),
            TransferError::Io(_)
        ));
    }
}

#[test]
fn noncanonical_local_names_are_never_cleanup_candidates() {
    let temp = tempfile::tempdir().unwrap();
    let canonical = fs::canonicalize(temp.path()).unwrap();
    let unusual = canonical.join("user-file.");
    fs::write(&unusual, b"preserve this user file").unwrap();
    fs::write(canonical.join("old.dll"), b"ordinary cleanup candidate").unwrap();
    let candidates = collect_files(&canonical).unwrap();
    assert_eq!(candidates, vec!["old.dll"]);
    assert_eq!(fs::read(&unusual).unwrap(), b"preserve this user file");
    assert!(manifest::validate_relative("user-file.").is_err());
}

#[test]
fn disk_full_is_classified_without_mislabeling_access_denied() {
    let temp = tempfile::tempdir().unwrap();
    let error = disk_error(
        io::Error::from(io::ErrorKind::StorageFull).into(),
        temp.path(),
        2 * 1024 * 1024,
    );
    assert!(matches!(error, TransferError::NoSpace { .. }));
    assert!(error.to_string().contains("2,0 МБ"));
    assert!(error.to_string().contains(&drive_label(temp.path())));
    assert!(matches!(
        disk_error(
            io::Error::from(io::ErrorKind::PermissionDenied).into(),
            temp.path(),
            123
        ),
        TransferError::Io(_)
    ));
    #[cfg(windows)]
    for code in [39, 112] {
        assert!(
            matches!(disk_error(io::Error::from_raw_os_error(code).into(), temp.path(), 0), TransferError::NoSpace { required, available, .. } if required > available)
        );
    }
}

#[cfg(windows)]
#[test]
fn readonly_replacement_is_atomic_and_failed_replacement_preserves_permissions() {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::{FILE_SHARE_READ, FILE_SHARE_WRITE};
    let temp = tempfile::tempdir().unwrap();
    let source = temp.path().join("file.part");
    let target = temp.path().join("file.dll");
    fs::write(&source, b"verified new bytes").unwrap();
    fs::write(&target, b"old bytes").unwrap();
    let mut permissions = fs::metadata(&target).unwrap().permissions();
    permissions.set_readonly(true);
    fs::set_permissions(&target, permissions).unwrap();
    let held = fs::OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
        .open(&target)
        .unwrap();
    assert!(atomic_replace(&source, &target).is_err());
    assert_eq!(fs::read(&target).unwrap(), b"old bytes");
    assert!(fs::metadata(&target).unwrap().permissions().readonly());
    assert!(source.is_file());
    drop(held);
    atomic_replace(&source, &target).unwrap();
    assert_eq!(fs::read(&target).unwrap(), b"verified new bytes");
    assert!(!source.exists());
}

fn blocks(bytes: &[u8]) -> Blocks {
    let mut digests = Vec::new();
    for chunk in bytes.chunks(BLOCK) {
        digests.extend_from_slice(&rolling(chunk).to_le_bytes());
        digests.extend_from_slice(&Sha256::digest(chunk)[..8]);
    }
    Blocks {
        block_size: BLOCK,
        file_size: bytes.len() as u64,
        digests,
    }
}

#[test]
fn resumed_partial_retains_only_complete_verified_prefix_including_short_tail() {
    let temp = tempfile::tempdir().unwrap();
    let part = temp.path().join("game.bin.part");
    let wanted: Vec<u8> = (0..BLOCK * 2 + 17)
        .map(|index| (index % 251) as u8)
        .collect();
    let block_map = blocks(&wanted);
    for (length, good) in [
        (0, 0),
        (BLOCK - 1, 0),
        (BLOCK, 1),
        (BLOCK + 7, 1),
        (BLOCK * 2, 2),
        (wanted.len() - 1, 2),
        (wanted.len(), 3),
    ] {
        fs::write(&part, &wanted[..length]).unwrap();
        assert_eq!(
            block_map
                .verify_prefix(&part, &TransferControl::default())
                .unwrap(),
            good,
            "partial length {length}"
        );
        assert_eq!(
            fs::read(&part).unwrap(),
            wanted[..block_map.offset(good) as usize]
        );
    }
    let mut overlong = wanted.clone();
    overlong.extend_from_slice(b"unverified suffix");
    fs::write(&part, overlong).unwrap();
    assert_eq!(
        block_map
            .verify_prefix(&part, &TransferControl::default())
            .unwrap(),
        3
    );
    assert_eq!(fs::read(&part).unwrap(), wanted);

    for (corrupt_at, good) in [(0, 0), (BLOCK + 5, 1), (BLOCK * 2 + 3, 2)] {
        let mut corrupt = wanted.clone();
        corrupt[corrupt_at] ^= 0xff;
        fs::write(&part, corrupt).unwrap();
        assert_eq!(
            block_map
                .verify_prefix(&part, &TransferControl::default())
                .unwrap(),
            good
        );
        assert_eq!(
            fs::read(&part).unwrap(),
            wanted[..block_map.offset(good) as usize]
        );
    }
}

#[test]
fn prefix_verification_never_creates_missing_file_or_truncates_on_cancel() {
    let temp = tempfile::tempdir().unwrap();
    let part = temp.path().join("game.bin.part");
    let bytes = vec![0x52; BLOCK * 2 + 17];
    let block_map = blocks(&bytes);
    assert_eq!(
        block_map
            .verify_prefix(&part, &TransferControl::default())
            .unwrap(),
        0
    );
    assert!(!part.exists());
    let mut corrupt = bytes;
    corrupt[BLOCK] ^= 0xff;
    fs::write(&part, &corrupt).unwrap();
    let cancelled = TransferControl::default();
    cancelled.cancel();
    assert!(matches!(
        block_map.verify_prefix(&part, &cancelled),
        Err(TransferError::Cancelled)
    ));
    assert_eq!(fs::read(&part).unwrap(), corrupt);
}

#[cfg(windows)]
#[test]
fn prefix_repair_rejects_hard_link_before_truncating_external_file() {
    let temp = tempfile::tempdir().unwrap();
    let user_file = temp.path().join("user-save.bin");
    let part = temp.path().join("game.bin.part");
    let saved = b"user-owned data that does not match a game block";
    fs::write(&user_file, saved).unwrap();
    fs::hard_link(&user_file, &part).unwrap();
    let block_map = blocks(&vec![0x33; BLOCK * 2]);
    assert!(
        block_map
            .verify_prefix(&part, &TransferControl::default())
            .is_err()
    );
    assert_eq!(fs::read(&user_file).unwrap(), saved);
    assert_eq!(fs::read(&part).unwrap(), saved);
}

#[test]
fn repeated_blocks_are_resolved_as_one_group_and_scan_finishes_early() {
    let temp = tempfile::tempdir().unwrap();
    let old = temp.path().join("old.bin");
    let bytes = vec![0; BLOCK * 256];
    fs::write(&old, &bytes).unwrap();
    let (offsets, windows) = blocks(&bytes)
        .find_in_old_counted(&old, &TransferControl::default(), u64::MAX)
        .unwrap();
    assert!(offsets.iter().all(|offset| *offset == Some(0)));
    assert_eq!(
        windows, 1,
        "resolved copies must not trigger a byte-by-byte scan"
    );
}

#[test]
fn resolved_repeated_blocks_keep_block_steps_when_another_block_is_missing() {
    let temp = tempfile::tempdir().unwrap();
    let old = temp.path().join("old.bin");
    let mut bytes = vec![0; BLOCK * 128];
    fs::write(&old, &bytes).unwrap();
    bytes.extend(vec![0x55; BLOCK]);
    let (offsets, windows) = blocks(&bytes)
        .find_in_old_counted(&old, &TransferControl::default(), u64::MAX)
        .unwrap();
    assert!(offsets[..128].iter().all(|offset| *offset == Some(0)));
    assert_eq!(offsets[128], None);
    assert_eq!(
        windows, 128,
        "each repeated block costs one lookup, regardless of group size"
    );
}

#[test]
fn weak_collision_work_is_bounded_and_short_tail_does_not_rescan_the_file() {
    let temp = tempfile::tempdir().unwrap();
    let old = temp.path().join("old.bin");
    fs::write(&old, vec![0; BLOCK * 16 + 17]).unwrap();
    let mut wanted = blocks(&vec![0; BLOCK * 2 + 17]);
    // Keep a matching polynomial hash but make full-block SHA prefixes wrong.
    wanted.digests[8..16].fill(0xff);
    wanted.digests[24..32].fill(0xff);
    let (offsets, windows) = wanted
        .find_in_old_counted(&old, &TransferControl::default(), (BLOCK * 2) as u64)
        .unwrap();
    assert_eq!(&offsets[..2], &[None, None]);
    assert_eq!(offsets[2], Some((BLOCK * 16) as u64));
    assert!(
        windows <= 18,
        "false weak matches must switch to block steps: {windows}"
    );
}

#[test]
fn changing_speed_limit_releases_a_waiting_transfer_without_restart() {
    let control = TransferControl::default();
    let worker_control = control.clone();
    let (started_tx, started_rx) = std::sync::mpsc::channel();
    let (done_tx, done_rx) = std::sync::mpsc::channel();
    let worker = std::thread::spawn(move || {
        let progress = Progress::new(BUFFER as u64, 1, 1);
        started_tx.send(()).unwrap();
        let result = progress.received(BUFFER as u64, &worker_control);
        done_tx.send(result).unwrap();
    });
    started_rx.recv_timeout(Duration::from_secs(2)).unwrap();
    assert!(done_rx.recv_timeout(Duration::from_millis(80)).is_err());
    control.set_speed_limit(0);
    done_rx
        .recv_timeout(Duration::from_secs(2))
        .unwrap()
        .unwrap();
    worker.join().unwrap();
}
