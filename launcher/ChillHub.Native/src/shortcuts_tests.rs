use super::*;
#[cfg(windows)]
#[test]
fn refresh_discovers_only_launcher_links_without_changing_them() {
    let temp = tempfile::tempdir().unwrap();
    let desktop = temp.path().join("Рабочий стол");
    let programs = temp.path().join("Programs");
    let pinned = temp.path().join("Pinned");
    let launcher = temp.path().join("Лаунчер.exe");
    let foreign = temp.path().join("Other.exe");
    fs::write(&launcher, b"fixture").unwrap();
    fs::write(&foreign, b"fixture").unwrap();
    let wanted = [
        desktop.join("Переименованный.lnk"),
        programs.join("Chill Hub/Launcher.lnk"),
        pinned.join("Game.lnk"),
    ];
    let ignored = [
        desktop.join("Other.lnk"),
        desktop.join("Nested/Launcher.lnk"),
    ];
    for path in wanted.iter().chain(ignored.iter()) {
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        let target = if path == &ignored[0] {
            &foreign
        } else {
            &launcher
        };
        native::write(path, target, "", temp.path(), "Fixture", target).unwrap();
    }
    let snapshots: Vec<_> = wanted
        .iter()
        .chain(ignored.iter())
        .map(|path| (path, fs::read(path).unwrap()))
        .collect();
    let actual = shortcuts_to_refresh(
        &launcher,
        &[(desktop, false), (programs, true), (pinned, false)],
    );
    assert_eq!(actual.len(), wanted.len());
    for path in &wanted {
        assert!(actual.contains(path));
    }
    for (path, bytes) in snapshots {
        assert_eq!(fs::read(path).unwrap(), bytes);
    }
}
#[test]
fn isolated_profile_never_touches_desktop() {
    let t = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(t.path());
    assert!(
        create_after_install(&paths, &GameRecord::default())
            .unwrap()
            .is_none()
    );
    assert_eq!(remove_for_game(&paths, "peak").unwrap(), 0);
}
#[test]
fn ownership_requires_target_and_matching_local_arguments() {
    let temp = tempfile::tempdir().unwrap();
    let launcher = temp.path().join("Apps/ChillHub.exe");
    let root = temp.path().join("Games/peak");
    let game_args = format!(
        "--game peak --exe {}",
        crate::launch::quote_windows_argument(&root.join("Game.exe").to_string_lossy())
    );
    let link = |target: &str, args: &str| Link {
        path: "ignored.lnk".into(),
        target: target.into(),
        arguments: args.into(),
    };
    assert!(owned(
        &link(
            &root.join("bin/Game.exe").to_string_lossy().to_uppercase(),
            ""
        ),
        &launcher,
        "peak",
        &root
    ));
    assert!(!owned(
        &link(
            &temp.path().join("Games/peak-2/Game.exe").to_string_lossy(),
            ""
        ),
        &launcher,
        "peak",
        &root
    ));
    assert!(!owned(
        &link(&temp.path().join("Other.exe").to_string_lossy(), &game_args),
        &launcher,
        "peak",
        &root
    ));
    assert!(owned(
        &link(&launcher.to_string_lossy(), &game_args),
        &launcher,
        "peak",
        &root
    ));
    assert!(!owned(
        &link(
            &launcher.to_string_lossy(),
            "--game peak --target SteamVanilla"
        ),
        &launcher,
        "peak",
        &root
    ));
}
#[test]
fn temp_shell_shortcuts_roundtrip_and_remove_only_owned() {
    let t = tempfile::tempdir().unwrap();
    let desktop = t.path().join("desktop");
    fs::create_dir(&desktop).unwrap();
    let root = t.path().join("games/peak");
    fs::create_dir_all(&root).unwrap();
    let launcher = t.path().join("ChillHub.exe");
    let exe = root.join("Game.exe");
    fs::write(&launcher, b"fixture").unwrap();
    fs::write(&exe, b"fixture").unwrap();
    let request = ShortcutRequest {
        game_id: "peak".into(),
        title: "Игра: тест".into(),
        exe_path: Some(exe),
        target: None,
    };
    let link = create_at(&desktop, &launcher, &request).unwrap();
    assert_eq!(link.file_name().unwrap(), "Игра_ тест (Chill Hub).lnk");
    let original = fs::read(&link).unwrap();
    fs::write(desktop.join("note.txt"), b"keep").unwrap();
    let repeat = create_at(&desktop, &launcher, &request).unwrap_or_else(|error| {
        let readback = native::details(&link);
        let links = read_links(std::slice::from_ref(&link));
        panic!(
            "{error}; expected link={link:?}, launcher={launcher:?}, launcher identity={:?}, exe={:?}, exe identity={:?}; parsed={links:?}; COM={readback:?}",
            path_key(&launcher),
            request.exe_path,
            request.exe_path.as_ref().and_then(|path| path_key(path)),
        );
    });
    assert_eq!(repeat, link);
    let short_launcher = crate::paths::short_test_path(&launcher);
    let short_exe = crate::paths::short_test_path(request.exe_path.as_ref().unwrap());
    let alias_request = ShortcutRequest {
        exe_path: Some(short_exe),
        ..request.clone()
    };
    assert_eq!(
        create_at(&desktop, &short_launcher, &alias_request).unwrap(),
        link
    );
    assert_eq!(fs::read(&link).unwrap(), original);
    assert_eq!(
        remove_at(&desktop, &launcher, "different", &root).unwrap(),
        0
    );
    assert_eq!(remove_at(&desktop, &launcher, "peak", &root).unwrap(), 1);
    assert!(desktop.join("note.txt").exists());
}

#[test]
fn unicode_shell_links_preserve_all_fields_and_foreign_ownership() {
    let temp = tempfile::tempdir().unwrap();
    let desktop = temp.path().join("桌面 🎮");
    let launcher_dir = temp.path().join("启动 🎲");
    let root = temp.path().join("游戏 🌄/peak");
    for path in [&desktop, &launcher_dir, &root] {
        fs::create_dir_all(path).unwrap();
    }
    let launcher = launcher_dir.join("启动器 🎮.exe");
    let exe = root.join("游戏 🎮.exe");
    fs::write(&launcher, b"fixture").unwrap();
    fs::write(&exe, b"fixture").unwrap();
    let request = ShortcutRequest {
        game_id: "peak".into(),
        title: "Игра 中文 🎮".into(),
        exe_path: Some(exe.clone()),
        target: None,
    };
    let path = create_at(&desktop, &launcher, &request).unwrap();
    assert_eq!(path.file_name().unwrap(), "Игра 中文 🎮 (Chill Hub).lnk");
    let bytes = fs::read(&path).unwrap();
    let links = read_links(std::slice::from_ref(&path)).unwrap();
    assert_eq!(links.len(), 1);
    assert_eq!(links[0].path, path);
    assert_eq!(path_key(&links[0].target), path_key(&launcher));
    let parsed = ShortcutRequest::parse_args(&arguments(&links[0].arguments).unwrap())
        .unwrap()
        .unwrap();
    assert_eq!(parsed.title, request.title);
    assert_eq!(path_key(parsed.exe_path.as_ref().unwrap()), path_key(&exe));
    let (working, icon, index) = native::details(&path).unwrap();
    assert_eq!(path_key(&working), path_key(&launcher_dir));
    assert_eq!(path_key(&icon), path_key(&exe));
    assert_eq!(index, 0);
    assert_eq!(create_at(&desktop, &launcher, &request).unwrap(), path);
    let wrong_game = ShortcutRequest {
        game_id: "other".into(),
        ..request.clone()
    };
    assert!(create_at(&desktop, &launcher, &wrong_game).is_err());
    assert_eq!(fs::read(&path).unwrap(), bytes);

    let foreign = desktop.join("Другой 中文 🎮.lnk");
    let other_launcher = temp.path().join("other.exe");
    fs::write(&other_launcher, b"fixture").unwrap();
    native::write(
        &foreign,
        &other_launcher,
        &links[0].arguments,
        temp.path(),
        "Чужой 中文 🎮",
        &exe,
    )
    .unwrap();
    let foreign_bytes = fs::read(&foreign).unwrap();
    assert_eq!(remove_at(&desktop, &launcher, "peak", &root).unwrap(), 1);
    assert!(!path.exists());
    assert_eq!(fs::read(&foreign).unwrap(), foreign_bytes);
}

#[cfg(windows)]
#[test]
fn manually_created_game_shortcut_roundtrips_unicode_paths_and_launch_arguments() {
    let temp = tempfile::tempdir().unwrap();
    let directory = temp.path().join("Игры ' и 🎮");
    fs::create_dir(&directory).unwrap();
    let launcher = directory.join("Chill Hub.exe");
    let game = directory.join("Игра с пробелами.exe");
    fs::write(&launcher, b"temporary fixture").unwrap();
    fs::write(&game, b"temporary fixture").unwrap();
    let output = directory.join("Играть ' с модами.lnk");
    let request = ShortcutRequest {
        game_id: "peak".into(),
        title: "Пик 'кавычки' и \"вершина\"\\ ".trim_end().into(),
        exe_path: Some(game),
        target: Some(crate::config::LaunchTarget::LocalModded),
    };
    crate::launch::create_game_shortcut(&launcher, &output, &request).unwrap();

    let links = read_links(std::slice::from_ref(&output)).unwrap();
    assert_eq!(links.len(), 1);
    let link = &links[0];
    assert_eq!(path_key(&link.target), path_key(&launcher));
    assert_eq!(
        path_key(&native::details(&output).unwrap().0),
        path_key(&directory)
    );
    let argv = arguments(&link.arguments).unwrap();
    assert_eq!(argv, request.arguments());
    assert_eq!(ShortcutRequest::parse_args(&argv).unwrap(), Some(request));
}
