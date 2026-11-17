use super::*;

struct Fixture {
    directory: tempfile::TempDir,
    install: PathBuf,
    games: PathBuf,
    roaming: PathBuf,
    legacy: PathBuf,
}

impl Fixture {
    fn new() -> Self {
        let directory = tempfile::tempdir().unwrap();
        let install = directory.path().join("Chill Hub");
        let games = directory.path().join("Игры");
        let roaming = directory.path().join("Roaming");
        let legacy = directory.path().join("Legacy");
        for path in [&install, &games, &roaming, &legacy] {
            fs::create_dir(path).unwrap();
        }
        fs::write(install.join(UNINSTALLER), b"uninstaller").unwrap();
        fs::write(install.join(UPDATER), b"updater").unwrap();
        fs::write(install.join("ChillHub.exe"), b"launcher").unwrap();
        fs::write(games.join("save"), b"game data").unwrap();
        fs::write(roaming.join("config.json"), b"settings").unwrap();
        fs::write(roaming.join(SECURITY_JOURNAL), b"owned exclusions").unwrap();
        fs::write(legacy.join("config.json"), b"old settings").unwrap();
        Self {
            directory,
            install,
            games,
            roaming,
            legacy,
        }
    }

    fn clean(&self, games: &Path, delete: bool, protected: &[PathBuf]) -> Result<(), String> {
        clean_installation(
            &self.install,
            games,
            delete,
            &self.roaming,
            &self.legacy,
            protected,
        )
    }
}

#[test]
fn blocked_game_paths_leave_installation_and_settings_unchanged() {
    let fixture = Fixture::new();
    let protected = [fixture.games.clone()];
    let short = crate::paths::short_test_path(&fixture.games);
    assert!(same_known_path(&fixture.games, &short));
    for path in [
        PathBuf::from("C:\\"),
        fixture.games.join("child").join(".."),
        short,
        fixture.install.clone(),
    ] {
        assert!(fixture.clean(&path, true, &protected).is_err(), "{path:?}");
        assert!(fixture.install.join("ChillHub.exe").is_file());
        assert!(fixture.roaming.join("config.json").is_file());
        assert_eq!(fs::read(fixture.games.join("save")).unwrap(), b"game data");
    }
}

#[test]
fn keeping_games_removes_settings_but_retains_exclusion_ownership() {
    let fixture = Fixture::new();
    fixture.clean(Path::new("C:\\"), false, &[]).unwrap();
    assert!(!fixture.install.join("ChillHub.exe").exists());
    assert!(!fixture.roaming.join("config.json").exists());
    assert_eq!(
        fs::read(fixture.roaming.join(SECURITY_JOURNAL)).unwrap(),
        b"owned exclusions"
    );
    assert!(!fixture.legacy.exists());
    assert_eq!(fs::read(fixture.games.join("save")).unwrap(), b"game data");
}

#[test]
fn silent_removal_refuses_a_stale_registered_library() {
    let fixture = Fixture::new();
    let second = fixture.directory.path().join("Moved library");
    fs::create_dir(&second).unwrap();
    let contents = serde_json::json!({"GamesPath": second});
    fs::write(
        fixture.roaming.join("config.json"),
        serde_json::to_vec(&contents).unwrap(),
    )
    .unwrap();
    assert!(require_current_library(&fixture.games, &fixture.roaming, &fixture.legacy).is_err());
    let short = crate::paths::short_test_path(&second);
    assert!(require_current_library(&short, &fixture.roaming, &fixture.legacy).is_ok());
    assert!(fixture.games.join("save").is_file());
    assert!(fixture.install.join(UNINSTALLER).is_file());
}

#[test]
fn explicitly_selected_library_is_removed_and_another_installation_is_kept() {
    let fixture = Fixture::new();
    fs::write(fixture.legacy.join("ChillHub.exe"), b"other launcher").unwrap();
    fs::remove_file(fixture.roaming.join(SECURITY_JOURNAL)).unwrap();
    fixture.clean(&fixture.games, true, &[]).unwrap();
    assert!(!fixture.games.exists());
    assert!(!fixture.roaming.exists());
    assert_eq!(
        fs::read(fixture.legacy.join("ChillHub.exe")).unwrap(),
        b"other launcher"
    );
}

#[test]
fn linked_library_is_refused_before_any_deletion() {
    let fixture = Fixture::new();
    let junction = fixture.directory.path().join("junction");
    let target = fixture.directory.path().join("target");
    fs::create_dir(&target).unwrap();
    fs::write(target.join("save"), b"linked game data").unwrap();
    use std::os::windows::process::CommandExt;
    let system = std::env::var_os("SystemRoot").unwrap();
    let output = std::process::Command::new(
        Path::new(&system).join("System32/WindowsPowerShell/v1.0/powershell.exe"),
    )
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "New-Item -Path $env:FIXTURE_JUNCTION -Target $env:FIXTURE_TARGET -ItemType Junction -ErrorAction Stop",
        ])
        .env("FIXTURE_JUNCTION", &junction)
        .env("FIXTURE_TARGET", &target)
        .creation_flags(0x08000000)
        .output()
        .unwrap();
    assert!(output.status.success(), "{output:?}");
    assert!(fixture.clean(&junction, true, &[]).is_err());
    assert_eq!(fs::read(target.join("save")).unwrap(), b"linked game data");
    assert!(junction.exists());
    assert!(fixture.install.join("ChillHub.exe").exists());
    fs::remove_dir(&junction).unwrap();
}
