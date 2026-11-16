use super::*;
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
