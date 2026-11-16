use super::*;
#[test]
fn invalid_ancestor_errors_are_not_treated_as_missing_directories() {
    let temp = tempfile::tempdir().unwrap();
    assert!(canonical_path_with_missing(&temp.path().join("invalid\0name")).is_err());
    assert_eq!(fs::read_dir(temp.path()).unwrap().count(), 0);
}

#[test]
fn game_identifiers_have_a_bounded_ascii_namespace() {
    for id in [
        "a".to_owned(),
        "Game_01-test".to_owned(),
        "a".repeat(127),
        "a".repeat(128),
    ] {
        assert!(validate_game_id(&id).is_ok(), "{id:?}");
    }
    for id in [
        String::new(),
        "a".repeat(129),
        "a".repeat(130),
        "игра".into(),
        "game.name".into(),
        "game name".into(),
        "game/name".into(),
    ] {
        assert!(validate_game_id(&id).is_err(), "{id:?}");
    }
}

#[test]
fn manifest_paths_reject_ambiguous_components_and_allow_ordinary_names() {
    for raw in [
        "", "a//b", "a/./b", "a/", "a /b", "a./b", "a\n/b", "a\t/b", "a</b", "a>/b", "a\"/b",
        "a|/b", "a?/b", "a*/b", "con", "prn.dat", "Aux", "nul.exe", "com9.txt", "lpt1", "LPT9.exe",
    ] {
        assert!(relative_path(raw).is_err(), "{raw:?}");
    }
    for raw in [
        "COM0",
        "COM10",
        "LPT0",
        "LPT10",
        "COMx",
        "LPTx",
        "Company",
        "example.dat",
        "a/b",
    ] {
        assert!(relative_path(raw).is_ok(), "{raw:?}");
    }
}

#[test]
fn containment_rejects_parent_components_even_with_no_existing_root() {
    let temp = tempfile::tempdir().unwrap();
    for root in [temp.path().to_path_buf(), temp.path().join("missing")] {
        for raw in ["../outside", "game/../../outside", "./file"] {
            assert!(contained_path(&root, Path::new(raw)).is_err(), "{raw:?}");
        }
        assert_eq!(
            contained_path(&root, Path::new("game/file")).unwrap(),
            root.join("game/file")
        );
    }
}

#[test]
fn configured_game_directory_is_respected_in_an_isolated_process() {
    const MARKER: &str = "CHILLHUB_PATH_TEST_CHILD";
    if std::env::var_os(MARKER).is_some() {
        assert_eq!(
            default_games_dir(),
            PathBuf::from(std::env::var_os("CHILLHUB_GAMES_DIR").unwrap())
        );
        return;
    }
    let temp = tempfile::tempdir().unwrap();
    let output = std::process::Command::new(std::env::current_exe().unwrap())
        .args([
            "--exact",
            "paths::tests::configured_game_directory_is_respected_in_an_isolated_process",
            "--nocapture",
        ])
        .env(MARKER, "1")
        .env("CHILLHUB_GAMES_DIR", temp.path().join("Chosen Games"))
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
fn failed_atomic_replacement_preserves_destination_and_removes_temporary_file() {
    let temp = tempfile::tempdir().unwrap();
    let dest = temp.path().join("directory");
    fs::create_dir(&dest).unwrap();
    fs::write(dest.join("keep"), b"original").unwrap();
    assert!(atomic_write(&dest, b"replacement").is_err());
    assert_eq!(fs::read(dest.join("keep")).unwrap(), b"original");
    assert_eq!(fs::read_dir(temp.path()).unwrap().count(), 1);
}
#[cfg(windows)]
#[test]
fn identity_resolves_short_and_verbatim_ancestors_without_creating_missing_paths() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("Long game directory name");
    fs::create_dir(&root).unwrap();
    let short = short_test_path(&root);
    let canonical = fs::canonicalize(&root).unwrap();
    let suffix = Path::new("Missing Game").join("Game.exe");
    assert_eq!(path_identity(&short), path_identity(&root));
    assert_eq!(path_identity(&canonical), path_identity(&root));
    assert_eq!(
        path_identity(&short.join(&suffix)),
        path_identity(&canonical.join(&suffix))
    );
    assert_eq!(
        canonical_path_with_missing(&short.join(&suffix)).unwrap(),
        canonical.join(&suffix)
    );
    assert!(!root.join("Missing Game").exists());
    assert!(path_identity(&short.join("../outside")).is_none());
    assert!(path_identity(Path::new("relative/Game.exe")).is_none());
    let file = root.join("existing-file");
    fs::write(&file, b"fixture").unwrap();
    assert!(path_identity(&file.join("child")).is_none());
    assert_ne!(
        path_identity(&root.join("one")),
        path_identity(&root.join("two"))
    );
}
#[test]
fn rejects_windows_path_escapes_and_device_names() {
    for p in [
        "../x.exe",
        "a/../x",
        "C:\\evil.exe",
        "\\\\host\\x",
        "x:stream",
        "CON.txt",
        "a/COM1",
        "a./x",
        "x\0.exe",
    ] {
        assert!(relative_path(p).is_err(), "{p}");
    }
    assert_eq!(
        relative_path(r"Game\Игра.exe").unwrap(),
        PathBuf::from("Game").join("Игра.exe")
    );
}
#[test]
fn atomic_replacement_preserves_complete_json() {
    let tmp = tempfile::tempdir().unwrap();
    let p = tmp.path().join("data.json");
    atomic_write(&p, b"first").unwrap();
    atomic_write(&p, b"second").unwrap();
    assert_eq!(fs::read(p).unwrap(), b"second");
}

#[cfg(windows)]
pub(crate) fn short_test_path(path: &Path) -> PathBuf {
    use std::os::windows::ffi::{OsStrExt, OsStringExt};
    let source: Vec<u16> = path.as_os_str().encode_wide().chain([0]).collect();
    let mut output = vec![0; 32768];
    let count = unsafe {
        windows_sys::Win32::Storage::FileSystem::GetShortPathNameW(
            source.as_ptr(),
            output.as_mut_ptr(),
            output.len() as u32,
        )
    } as usize;
    assert!(
        count > 0 && count < output.len(),
        "GetShortPathNameW failed: {}",
        io::Error::last_os_error()
    );
    std::ffi::OsString::from_wide(&output[..count]).into()
}
