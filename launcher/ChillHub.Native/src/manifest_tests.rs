use super::*;
#[test]
fn opened_handle_metadata_must_be_regular_even_after_path_validation() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("regular.bin");
    fs::write(&path, b"untouched").unwrap();
    validate_regular_metadata(&fs::metadata(&path).unwrap()).unwrap();
    assert!(validate_regular_metadata(&fs::metadata(temp.path()).unwrap()).is_err());
    assert_eq!(fs::read(path).unwrap(), b"untouched");
}

#[test]
fn opened_write_handle_accepts_regular_file_and_rejects_hard_link_before_truncating() {
    use std::io::Write;
    let temp = tempfile::tempdir().unwrap();
    let target = temp.path().join("target.bin");
    let alias = temp.path().join("alias.bin");
    fs::write(&target, b"old content").unwrap();
    let mut file = open_regular(&target, true, false, true).unwrap();
    file.write_all(b"new content").unwrap();
    drop(file);
    fs::hard_link(&target, &alias).unwrap();
    assert!(open_regular(&alias, true, false, true).is_err());
    assert_eq!(fs::read(&target).unwrap(), b"new content");
}
#[test]
fn directory_normalization_is_idempotent_for_programmatic_manifests() {
    let mut manifest = Manifest {
        empty_dirs: vec!["cache///".into(), "plugins\\\\".into(), "ordinary".into()],
        ..Default::default()
    };
    manifest.normalize_empty_dirs();
    assert_eq!(manifest.empty_dirs, ["cache", "plugins", "ordinary"]);
    manifest.normalize_empty_dirs();
    assert_eq!(manifest.empty_dirs, ["cache", "plugins", "ordinary"]);
}

#[test]
fn manifest_path_limits_and_component_boundaries_are_enforced() {
    for length in [1023, 1024] {
        assert!(validate_relative(&"a".repeat(length)).is_ok());
    }
    for path in [
        "a".repeat(1025),
        "file ".into(),
        "a/ b".into(),
        ".".into(),
        "..".into(),
        "a/".into(),
        String::new(),
    ] {
        assert!(validate_relative(&path).is_err(), "{path:?}");
    }
}

#[test]
fn hashes_require_both_the_exact_length_and_hexadecimal_content() {
    for hash in ["0".repeat(63), "0".repeat(65), "x".repeat(64)] {
        for sha_only in [false, true] {
            let mut file = ManifestFile {
                path: "game.exe".into(),
                ..Default::default()
            };
            if sha_only {
                file.sha256 = Some(hash.clone());
            } else {
                file.blake3 = hash.clone();
            }
            assert!(
                validate_manifest(&Manifest {
                    files: vec![file],
                    ..Default::default()
                })
                .is_err()
            );
        }
    }
    let manifest = Manifest {
        files: ["A/bc", "ab/C"]
            .into_iter()
            .map(|path| ManifestFile {
                path: path.into(),
                blake3: "a".repeat(64),
                ..Default::default()
            })
            .collect(),
        ..Default::default()
    };
    validate_manifest(&manifest).unwrap();
}

#[test]
fn filesystem_errors_and_parent_components_are_not_accepted_as_missing_paths() {
    let temp = tempfile::tempdir().unwrap();
    assert!(reject_link(&temp.path().join("invalid\0name")).is_err());
    assert!(check_no_links(&temp.path().join("../outside")).is_err());
    assert!(check_no_links(Path::new("relative")).is_err());
    check_no_links(&temp.path().join("missing/child")).unwrap();
}

#[test]
fn directory_creation_is_idempotent_but_does_not_hide_real_errors() {
    let temp = tempfile::tempdir().unwrap();
    let dir = temp.path().join("new");
    create_directory_if_absent(&dir).unwrap();
    create_directory_if_absent(&dir).unwrap();
    fs::write(dir.join("keep"), b"original").unwrap();
    create_directory_if_absent(&dir).unwrap();
    assert_eq!(fs::read(dir.join("keep")).unwrap(), b"original");
    assert!(create_directory_if_absent(&temp.path().join("missing/child")).is_err());
}

#[test]
fn directory_guards_allow_readers_but_reject_files_and_preserve_missing_paths() {
    let temp = tempfile::tempdir().unwrap();
    let missing = temp.path().join("not-created");
    let _missing_guard = DirectoryGuard::acquire(&missing, false).unwrap();
    assert!(!missing.exists());
    let dir = temp.path().join("created");
    let guard = DirectoryGuard::acquire(&dir, true).unwrap();
    assert!(dir.is_dir());
    assert_eq!(fs::read_dir(&dir).unwrap().count(), 0);
    fs::write(dir.join("file"), b"content").unwrap();
    assert!(DirectoryGuard::acquire(&dir.join("file"), false).is_err());
    #[cfg(windows)]
    assert!(
        fs::rename(&dir, temp.path().join("moved")).is_err(),
        "guard pins directory identity"
    );
    drop(guard);
    fs::rename(&dir, temp.path().join("moved")).unwrap();
}

#[cfg(windows)]
#[test]
fn junctions_cannot_redirect_content_access_outside_the_installation() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("root");
    let outside = temp.path().join("outside");
    fs::create_dir(&root).unwrap();
    fs::create_dir(&outside).unwrap();
    fs::write(outside.join("keep"), b"original").unwrap();
    let link = root.join("junction");
    let output = std::process::Command::new("cmd.exe")
        .args(["/D", "/C", "mklink", "/J"])
        .arg(&link)
        .arg(&outside)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "junction fixture failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );
    assert!(is_reparse(&fs::symlink_metadata(&link).unwrap()));
    assert!(reject_link(&link).is_err());
    assert!(check_no_links(&link.join("keep")).is_err());
    assert!(safe_join(&root, "junction/keep").is_err());
    assert!(DirectoryGuard::acquire(&link, false).is_err());
    assert_eq!(fs::read(outside.join("keep")).unwrap(), b"original");
    // Remove only the junction itself; never recursively walk the target.
    fs::remove_dir(&link).unwrap();
}

#[test]
fn service_metadata_matches_only_reserved_relative_paths() {
    for path in [
        ".version",
        ".mods.version",
        ".mods.revision",
        ".mods.manifest.json",
        ".updating",
        ".chillhub.lock",
        ".integrity.json",
        "FreeTP/.HASH",
        ".chillhub-reboot",
        ".chillhub-reboot/unique-session/payload",
    ] {
        assert!(is_service_path(path), "{path}");
        assert!(is_service_path(&path.to_uppercase()), "{path}");
    }
    for path in [
        "game.exe",
        "folder/.version",
        ".version.backup",
        "x/freetp/.hash",
        "freetp/hash",
        ".chillhub-reboot.backup",
        "saves/.chillhub-reboot/payload",
    ] {
        assert!(!is_service_path(path), "{path}");
    }
}
#[test]
fn empty_directory_wire_separators_do_not_relax_path_validation_or_duplicates() {
    let manifest: Manifest =
        serde_json::from_str(r#"{"files":[],"emptyDirs":["BepInEx/plugins/empty/","cache\\"]}"#)
            .unwrap();
    assert_eq!(manifest.empty_dirs, ["BepInEx/plugins/empty", "cache"]);
    validate_manifest(&manifest).unwrap();
    for dirs in [
        vec!["dir", "dir/"],
        vec!["../outside/"],
        vec!["/absolute/"],
        vec!["parent//child/"],
        vec!["parent\\child/"],
        vec!["C:\\"],
    ] {
        let mut manifest = Manifest {
            empty_dirs: dirs.into_iter().map(str::to_owned).collect(),
            ..Default::default()
        };
        manifest.normalize_empty_dirs();
        assert!(validate_manifest(&manifest).is_err());
    }
}
#[test]
fn windows_paths_are_rejected_on_every_host() {
    for path in [
        "../x",
        "/x",
        "C:/x",
        "x:ads",
        "a\\b",
        "a//b",
        "a/./b",
        "x.",
        " a",
        "NUL.txt",
        "com1",
        "LPT².exe",
        "a/../b",
        "x\0",
    ] {
        assert!(validate_relative(path).is_err(), "{path}");
    }
    validate_relative("BepInEx/plugins/Пример.dll").unwrap();
}
#[test]
fn collisions_are_rejected() {
    let file = |p: &str| ManifestFile {
        path: p.into(),
        blake3: "0".repeat(64),
        ..Default::default()
    };
    for paths in [
        ["a", "A"],
        ["a", "a/b"],
        ["a", "a.part"],
        ["Dir/a", "dir/b"],
    ] {
        let manifest = Manifest {
            files: paths.iter().map(|p| file(p)).collect(),
            ..Default::default()
        };
        assert!(validate_manifest(&manifest).is_err());
    }
}
