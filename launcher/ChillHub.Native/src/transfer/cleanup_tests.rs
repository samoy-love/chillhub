use super::*;

impl CleanupFile {
    pub(crate) fn fixture(path: &str) -> Self {
        Self {
            path: path.into(),
            identity: FileIdentity {
                volume: 1,
                index: [1; 16],
                generation: 1,
            },
            digest: "fixture".into(),
        }
    }
}

#[test]
fn approval_matches_every_exact_snapshot_and_never_extends_to_another_name_or_identity() {
    let old = CleanupFile::fixture("save.txt");
    let other = CleanupFile::fixture("new.txt");
    assert!(is_approved(&[], &[]));
    assert!(!is_approved(std::slice::from_ref(&old), &[]));
    assert!(is_approved(
        std::slice::from_ref(&old),
        &[old.clone(), other.clone()]
    ));
    assert!(!is_approved(
        &[old.clone(), other],
        std::slice::from_ref(&old)
    ));
    for path in ["SAVE.TXT", "save.txt.bak", "dir/save.txt"] {
        assert!(!is_approved(
            &[CleanupFile::fixture(path)],
            std::slice::from_ref(&old)
        ));
    }
    let mut different_identity = old.clone();
    different_identity.identity.index = [2; 16];
    assert!(!is_approved(
        &[different_identity],
        std::slice::from_ref(&old)
    ));
    let mut different_content = old.clone();
    different_content.digest = "changed".into();
    assert!(!is_approved(&[different_content], &[old]));
}

#[test]
fn removing_then_replacing_with_identical_bytes_still_requires_fresh_approval() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("save.bin");
    let replacement = temp.path().join("replacement");
    fs::write(&path, b"same").unwrap();
    fs::write(&replacement, b"same").unwrap();
    let control = TransferControl::default();
    let original = snapshot(&path, "save.bin", &control).unwrap().unwrap();
    fs::remove_file(&path).unwrap();
    fs::rename(replacement, &path).unwrap();
    let current = snapshot(&path, "save.bin", &control).unwrap().unwrap();
    assert_eq!(original.digest, current.digest);
    assert_ne!(original.identity, current.identity);
    assert!(matches!(
        remove_approved(&path, &original, &control).unwrap(),
        CleanupResult::Changed(file) if file == current
    ));
    assert_eq!(fs::read(&path).unwrap(), b"same");
    assert!(matches!(
        remove_approved(&path, &current, &control).unwrap(),
        CleanupResult::Removed
    ));
    assert!(matches!(
        remove_approved(&path, &current, &control).unwrap(),
        CleanupResult::Missing
    ));
}

#[cfg(windows)]
#[test]
fn windows_delete_handle_pins_the_file_and_allows_removing_a_readonly_snapshot() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("old-settings.ini");
    let replacement = temp.path().join("replacement");
    fs::write(&path, b"approved").unwrap();
    fs::write(&replacement, b"new").unwrap();
    let control = TransferControl::default();
    let deletion_handle = open_for_delete(&path).unwrap();
    assert!(fs::write(&path, b"unreviewed").is_err());
    assert!(fs::rename(&replacement, &path).is_err());
    assert_eq!(
        capture(&deletion_handle, "old-settings.ini", &control)
            .unwrap()
            .path,
        "old-settings.ini"
    );
    drop(deletion_handle);
    let mut permissions = fs::metadata(&path).unwrap().permissions();
    permissions.set_readonly(true);
    fs::set_permissions(&path, permissions).unwrap();
    let original = snapshot(&path, "old-settings.ini", &control)
        .unwrap()
        .unwrap();
    assert!(matches!(
        remove_approved(&path, &original, &control).unwrap(),
        CleanupResult::Removed
    ));
    assert!(!path.exists());
    assert_eq!(fs::read(&replacement).unwrap(), b"new");
}
