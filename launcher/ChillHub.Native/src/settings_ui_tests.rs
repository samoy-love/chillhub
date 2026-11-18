use super::*;
#[test]
fn fixed_profile_rejects_an_ineffective_change_but_allows_other_preferences() {
    let forced = std::ffi::OsStr::new(r"C:\Preview\Games");
    assert!(validate_path_override(r"D:\Games", None).is_ok());
    assert!(validate_path_override("c:/preview/games/", Some(forced)).is_ok());
    assert!(
        validate_path_override(r"D:\Games", Some(forced))
            .unwrap_err()
            .contains("закреплена")
    );
}
#[test]
fn equivalent_windows_paths_do_not_block_speed_changes() {
    assert!(same_path("D:/Games/", r"d:\games"));
    assert!(!same_path(r"D:\Games", r"D:\Games2"));
}
#[test]
fn package_url_uses_server_community_and_rejects_path_injection() {
    let mut m = chillhub_native::model::ModsRecord {
        community: "peak".into(),
        version: "Lart_Iste-PeakFriendsEdition-2.0.15".into(),
        ..Default::default()
    };
    assert_eq!(
        modpack_url(&m).unwrap(),
        "https://thunderstore.io/c/peak/p/Lart_Iste/PeakFriendsEdition/"
    );
    m.community = "../other".into();
    assert!(modpack_url(&m).is_none());
}
#[test]
fn disk_size_reads_only_requested_tree() {
    let t = tempfile::tempdir().unwrap();
    std::fs::write(t.path().join("one"), [1, 2, 3]).unwrap();
    assert_eq!(folder_size(t.path()).unwrap(), 3);
}
#[test]
fn path_normalization_keeps_unc_and_missing_drive() {
    assert_eq!(
        normalize_games_path(r"D:\Games\\ChillHub"),
        PathBuf::from(r"D:\Games\ChillHub")
    );
    assert_eq!(
        normalize_games_path(r"\\nas\\games\\ChillHub"),
        PathBuf::from(r"\\nas\games\ChillHub")
    );
    assert_eq!(
        normalize_games_path(" "),
        chillhub_native::paths::default_games_dir()
    );
}

#[test]
fn package_links_reject_wrong_hosts_schemes_and_credentials_before_opening() {
    for url in [
        "invalid url",
        "http://thunderstore.io/c/game/p/team/package/",
        "https://thunderstore.io.example.com/",
        "https://person@thunderstore.io/",
        "https://:secret@thunderstore.io/",
    ] {
        assert!(open_package(url).is_err(), "{url}");
    }
}

#[test]
fn absent_or_regular_file_paths_have_no_tree_to_measure() {
    let root = tempfile::tempdir().unwrap();
    let file = root.path().join("game");
    assert_eq!(folder_size(&file).unwrap(), 0);
    std::fs::write(&file, b"existing file").unwrap();
    assert_eq!(folder_size(&file).unwrap(), 0);
    assert_eq!(
        normalize_games_path("  "),
        chillhub_native::paths::default_games_dir()
    );
    assert!(validate_path_override("D:/Fresh Games", None).is_ok());
}

#[test]
fn rooted_and_unc_folder_input_preserve_the_windows_path_prefix() {
    assert_eq!(
        normalize_games_path(" /Games//Current/ "),
        PathBuf::from(r"\Games\Current")
    );
    assert_eq!(
        normalize_games_path("//nas//share/game/"),
        PathBuf::from(r"\\nas\share\game")
    );
}

#[cfg(windows)]
#[test]
fn disk_size_excludes_linked_filesystems_from_the_games_total() {
    use std::{os::windows::process::CommandExt, process::Command};
    let temp = tempfile::tempdir().unwrap();
    let games = temp.path().join("games");
    let outside = temp.path().join("other-content");
    let junction = games.join("linked");
    std::fs::create_dir_all(games.join("title/subfolder")).unwrap();
    std::fs::create_dir(&outside).unwrap();
    std::fs::write(games.join("title/subfolder/content.bin"), [1; 21]).unwrap();
    std::fs::write(games.join("title/.version"), [1; 3]).unwrap();
    std::fs::write(outside.join("external.bin"), [1; 100]).unwrap();

    let windows = PathBuf::from(std::env::var_os("SystemRoot").unwrap());
    let output = Command::new(windows.join("System32/WindowsPowerShell/v1.0/powershell.exe"))
        .args([
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "New-Item -Path $env:FIXTURE_JUNCTION -Target $env:FIXTURE_TARGET -ItemType Junction -ErrorAction Stop",
        ])
        .env("FIXTURE_JUNCTION", &junction)
        .env("FIXTURE_TARGET", &outside)
        .creation_flags(0x08000000)
        .output()
        .unwrap();
    assert!(output.status.success(), "{output:?}");
    assert_eq!(folder_size(&games).unwrap(), 24);
    assert!(folder_size(&junction).is_err());
    std::fs::remove_dir(&junction).unwrap();
    assert_eq!(
        std::fs::read(outside.join("external.bin")).unwrap(),
        [1; 100]
    );
}
