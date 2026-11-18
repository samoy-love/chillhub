use super::*;
#[test]
fn cleanup_requires_a_live_nonempty_catalog_and_rechecks_republished_games() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let root = paths.games_dir.join("local-game");
    fs::create_dir_all(&root).unwrap();
    fs::write(root.join(".version"), "1").unwrap();
    let mut snapshot = crate::net::CatalogSnapshot {
        library: crate::model::LibraryResponse {
            items: vec![GameRecord {
                game_id: "other".into(),
                ..Default::default()
            }],
        },
        offline: true,
        all_entries_valid: true,
        fetched_at: String::new(),
        warning: None,
    };
    assert!(confirmed_orphans(&paths, &snapshot).is_err());
    snapshot.offline = false;
    assert_eq!(
        confirmed_orphans(&paths, &snapshot).unwrap()[0].id,
        "local-game"
    );
    snapshot.all_entries_valid = false;
    assert!(confirmed_orphans(&paths, &snapshot).is_err());
    snapshot.all_entries_valid = true;
    snapshot.library.items.clear();
    assert!(confirmed_orphans(&paths, &snapshot).is_err());
    snapshot.library.items.push(GameRecord {
        game_id: "LOCAL-GAME".into(),
        ..Default::default()
    });
    assert!(confirmed_orphans(&paths, &snapshot).unwrap().is_empty());
    assert!(root.join(".version").is_file());
}

#[test]
fn only_marked_unpublished_games_are_offered_for_removal() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    for name in ["known", "old", "personal"] {
        fs::create_dir_all(paths.games_dir.join(name)).unwrap();
    }
    for name in ["known", "old"] {
        fs::write(paths.games_dir.join(name).join(".version"), "1").unwrap();
    }
    let found = orphaned(
        &paths,
        &[GameRecord {
            game_id: "known".into(),
            ..Default::default()
        }],
    )
    .unwrap();
    assert_eq!(
        found.iter().map(|g| g.id.as_str()).collect::<Vec<_>>(),
        vec!["old"]
    );
}

#[test]
fn library_scan_ignores_foreign_entries_and_detects_mods_and_interrupted_installs() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    assert!(orphaned(&paths, &[]).unwrap().is_empty());
    fs::create_dir_all(&paths.games_dir).unwrap();
    for (name, marker) in [
        ("old-mods", ".mods.version"),
        ("interrupted", ".updating"),
        ("not a game id", ".version"),
    ] {
        let folder = paths.games_dir.join(name);
        fs::create_dir(&folder).unwrap();
        fs::write(folder.join(marker), "1.0").unwrap();
    }
    fs::write(paths.games_dir.join("personal-file"), "keep").unwrap();
    let found = orphaned(&paths, &[]).unwrap();
    assert_eq!(
        found
            .iter()
            .map(|entry| entry.id.as_str())
            .collect::<Vec<_>>(),
        ["interrupted", "old-mods"]
    );
    assert_eq!(
        fs::read(paths.games_dir.join("personal-file")).unwrap(),
        b"keep"
    );
}
#[test]
fn game_root_cannot_contain_launcher_or_user_state() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let install = temp.path().join("install");
    assert!(validate_games_location(&paths.games_dir, &paths, &install).is_ok());
    assert!(validate_games_location(&paths.data_dir, &paths, &install).is_err());
    assert!(validate_games_location(temp.path(), &paths, &install).is_err());
    assert!(validate_games_location(&install.join("games"), &paths, &install).is_err());
}

#[cfg(windows)]
#[test]
fn aliases_cannot_place_games_inside_install_or_settings_even_before_creation() {
    let temp = tempfile::tempdir().unwrap();
    let parent = temp.path().join("Long shared application directory");
    fs::create_dir(&parent).unwrap();
    let short = crate::paths::short_test_path(&parent);
    let long = fs::canonicalize(&parent).unwrap();
    let paths = AppPaths {
        data_dir: long.join("Settings still missing"),
        legacy_dir: None,
        games_dir: long.join("Separate games"),
    };
    let install = long.join("Launcher still missing");
    for folder in [
        short.clone(),
        short.join("Settings still missing"),
        short.join("Settings still missing/Games"),
        short.join("Launcher still missing/Games"),
    ] {
        assert!(validate_games_location(&folder, &paths, &install).is_err());
    }
    assert!(validate_games_location(&short.join("Separate games"), &paths, &install).is_ok());
    assert!(!paths.data_dir.exists());
    assert!(!install.exists());
}

#[cfg(windows)]
#[test]
fn unavailable_drive_setting_does_not_create_directories() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let drive = (b'D'..=b'Z').rev().find(|drive| {
        fs::metadata(format!("{}:\\", *drive as char))
            .is_err_and(|e| e.kind() == std::io::ErrorKind::NotFound)
    });
    if let Some(drive) = drive {
        let folder = PathBuf::from(format!("{}:\\ChillHub Alias Test\\Games", drive as char));
        let install = temp.path().join("install");
        fs::create_dir(&install).unwrap();
        assert!(validate_games_location(&folder, &paths, &install).is_ok());
        let update = crate::update::UpdatePaths {
            install,
            config: paths.data_dir.clone(),
            games: folder.clone(),
            staging: temp.path().join("staging"),
        };
        assert!(
            update.validate().is_ok(),
            "a disconnected games disk must not block a launcher update"
        );
        let mut invalid = update.clone();
        invalid.install = folder.clone();
        assert!(invalid.validate().is_err());
        invalid = update.clone();
        invalid.config = folder.clone();
        assert!(invalid.validate().is_err());
        invalid = update;
        invalid.staging = folder.clone();
        assert!(invalid.validate().is_err());
        assert!(!folder.exists());
    }
}
