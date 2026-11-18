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

#[cfg(windows)]
#[test]
fn read_only_inspection_does_not_open_user_saves_for_cleanup() {
    use std::os::windows::fs::OpenOptionsExt;
    let temp = tempfile::tempdir().unwrap();
    let save = temp.path().join("user-save.bin");
    fs::write(&save, b"user-owned bytes").unwrap();
    let held = fs::OpenOptions::new()
        .write(true)
        .share_mode(0)
        .open(&save)
        .unwrap();
    let engine = TransferEngine::new().unwrap();
    let request = TransferRequest {
        root: temp.path().into(),
        manifest_url: "http://127.0.0.1/manifest".into(),
        content_base_url: "http://127.0.0.1/content/".into(),
        ..Default::default()
    };
    let control = TransferControl::default();
    let inspection = engine
        .inspect_manifest(&request, Manifest::default(), &control)
        .unwrap();
    assert!(!inspection.needs_repair);
    assert_eq!(inspection.required_free_bytes, 0);
    assert!(
        engine
            .plan_manifest(&request, Manifest::default(), &control, &|_| {})
            .is_err()
    );
    drop(held);
    assert_eq!(fs::read(save).unwrap(), b"user-owned bytes");
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
fn sparse_or_shared_old_files_do_not_promise_disk_space() {
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::System::IO::DeviceIoControl;
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("game");
    fs::create_dir(&root).unwrap();
    let sparse_path = root.join("sparse.bin");
    let sparse = open_regular(&sparse_path, true, true, false).unwrap();
    let mut bytes_returned = 0;
    let ok = unsafe {
        DeviceIoControl(
            sparse.as_raw_handle().cast(),
            0x0009_00c4,
            std::ptr::null(),
            0,
            std::ptr::null_mut(),
            0,
            &mut bytes_returned,
            std::ptr::null_mut(),
        )
    };
    assert_ne!(ok, 0, "FSCTL_SET_SPARSE: {}", io::Error::last_os_error());
    sparse.set_len(1024 * 1024).unwrap();
    drop(sparse);
    let source = temp.path().join("source.bin");
    fs::write(&source, vec![1; 1024 * 1024]).unwrap();
    fs::hard_link(&source, root.join("shared.bin")).unwrap();
    let manifest = Manifest {
        files: ["sparse.bin", "shared.bin"]
            .map(|path| ManifestFile {
                path: path.into(),
                size: 64 * 1024,
                blake3: "00".repeat(32),
                ..Default::default()
            })
            .into(),
        ..Default::default()
    };
    let request = TransferRequest {
        root,
        manifest_url: "http://127.0.0.1/manifest".into(),
        content_base_url: "http://127.0.0.1/content/".into(),
        max_parallel: 1,
        ..Default::default()
    };
    let engine = TransferEngine::new().unwrap();
    let plan = engine
        .plan_manifest(&request, manifest, &TransferControl::default(), &|_| {})
        .unwrap();
    assert_eq!(plan.required_free_bytes, 128 * 1024);
    assert_eq!(fs::metadata(&source).unwrap().len(), 1024 * 1024);
}

#[cfg(windows)]
#[test]
fn a_worker_pins_the_current_directory_and_releases_it_when_switching() {
    let temp = tempfile::tempdir().unwrap();
    let first = temp.path().join("first");
    let second = temp.path().join("second");
    let moved = temp.path().join("moved");
    let mut worker = TransferWorker::new();
    worker.guard_directory(&first).unwrap();
    worker.guard_directory(&first).unwrap();
    assert!(fs::rename(&first, &moved).is_err());
    worker.guard_directory(&second).unwrap();
    fs::rename(&first, &moved).unwrap();
    assert!(fs::rename(&second, &first).is_err());
    drop(worker);
    fs::rename(&second, &first).unwrap();
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

#[cfg(windows)]
#[test]
fn atomic_replace_accepts_long_unicode_and_verbatim_windows_paths() {
    let temp = tempfile::tempdir().unwrap();
    let mut directory = temp.path().to_owned();
    for _ in 0..8 {
        directory.push("Сборка-тест-длинного-пути-до-файла");
    }
    fs::create_dir_all(&directory).unwrap();
    let part = directory.join("Данные.bin.part");
    let target = directory.join("Данные.bin");
    fs::write(&part, b"updated bytes").unwrap();
    fs::write(&target, b"old bytes").unwrap();
    atomic_replace(&part, &target).unwrap();
    assert_eq!(fs::read(&target).unwrap(), b"updated bytes");
    let verbatim = fs::canonicalize(&directory).unwrap();
    let part = verbatim.join("Данные.bin.part");
    let target = verbatim.join("Данные.bin");
    fs::write(&part, b"new bytes on the verbatim path").unwrap();
    atomic_replace(&part, &target).unwrap();
    assert_eq!(
        fs::read(&target).unwrap(),
        b"new bytes on the verbatim path"
    );
    let tail = "longer-directory/".repeat(21);
    let mixed = format!(r"C:\Root/{tail}Игра.exe.part");
    let normalized = String::from_utf16(&windows_move_path(Path::new(&mixed))).unwrap();
    assert!(normalized.starts_with(r"\\?\C:\Root\"));
    assert!(!normalized.contains('/'));
    let unc = format!(r"\\server\share\{tail}Игра.exe.part");
    let normalized = String::from_utf16(&windows_move_path(Path::new(&unc))).unwrap();
    assert!(normalized.starts_with(r"\\?\UNC\server\share\"));
    assert!(normalized.ends_with("Игра.exe.part\0"));
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

fn verify_prefix(blocks: &Blocks, part: &Path, control: &TransferControl) -> Result<usize> {
    let mut buffer = vec![0; blocks.block_size];
    let mut digests = FileHashes::new(&ManifestFile::default());
    blocks
        .verify_prefix(part, &mut buffer, &mut digests, control)
        .map(|(count, _)| count)
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
            verify_prefix(&block_map, &part, &TransferControl::default()).unwrap(),
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
        verify_prefix(&block_map, &part, &TransferControl::default()).unwrap(),
        3
    );
    assert_eq!(fs::read(&part).unwrap(), wanted);

    for (corrupt_at, good) in [(0, 0), (BLOCK + 5, 1), (BLOCK * 2 + 3, 2)] {
        let mut corrupt = wanted.clone();
        corrupt[corrupt_at] ^= 0xff;
        fs::write(&part, corrupt).unwrap();
        assert_eq!(
            verify_prefix(&block_map, &part, &TransferControl::default()).unwrap(),
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
        verify_prefix(&block_map, &part, &TransferControl::default()).unwrap(),
        0
    );
    assert!(!part.exists());
    let mut corrupt = bytes;
    corrupt[BLOCK] ^= 0xff;
    fs::write(&part, &corrupt).unwrap();
    let cancelled = TransferControl::default();
    cancelled.cancel();
    assert!(matches!(
        verify_prefix(&block_map, &part, &cancelled),
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
    assert!(verify_prefix(&block_map, &part, &TransferControl::default()).is_err());
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
fn rolling_window_survives_refill_before_reusing_a_shifted_block() {
    let temp = tempfile::tempdir().unwrap();
    let old = temp.path().join("old.bin");
    let first = vec![1; BLOCK];
    let second = vec![2; BLOCK];
    let block_map = blocks(&[first.as_slice(), second.as_slice()].concat());
    let shifted = 5 * BLOCK + 19;
    let mut contents = vec![0; shifted];
    contents.extend_from_slice(&first);
    contents.extend_from_slice(&second);
    fs::write(&old, contents).unwrap();
    let offsets = block_map
        .find_in_old(&old, &TransferControl::default())
        .unwrap();
    assert_eq!(
        offsets,
        [Some(shifted as u64), Some((shifted + BLOCK) as u64)]
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
