use super::*;
#[test]
fn setup_callbacks_preserve_invalid_input_and_unlock_after_preview_failure() {
    i_slint_backend_testing::init_integration_test_with_system_time();
    let temporary = tempfile::tempdir().unwrap();
    let install = temporary.path().join("launcher");
    let games = temporary.path().join("games");
    let args = [
        "preview-install".to_owned(),
        temporary
            .path()
            .join("missing-engine.exe")
            .display()
            .to_string(),
        install.display().to_string(),
        games.display().to_string(),
        "1.8.4".to_owned(),
    ];
    let log = HelperLog::in_directory(temporary.path(), HelperKind::Setup).unwrap();
    let ui = create_window(Arc::new(Request::parse(&args).unwrap()), Some(log.clone())).unwrap();
    assert!(!ui.get_removing());
    assert_eq!(ui.get_version(), "1.8.4");
    ui.set_install_path("relative".into());
    ui.invoke_submit();
    assert!(ui.get_error());
    assert!(!ui.get_busy());
    assert_eq!(ui.get_install_path(), "relative");
    assert!(ui.get_message().contains("полным путём"));

    ui.set_install_path(install.display().to_string().into());
    ui.set_games_path(install.join("games").display().to_string().into());
    ui.invoke_submit();
    assert!(ui.get_message().contains("раздельными"));
    assert!(!ui.get_busy());
    ui.set_games_path(games.display().to_string().into());
    // Preview must never launch the security helper, even on its callback.
    ui.invoke_security();
    ui.invoke_submit();
    assert!(ui.get_busy());
    assert!(!ui.get_error());

    let completed = std::rc::Rc::new(std::cell::Cell::new(false));
    let result = completed.clone();
    let weak = ui.as_weak();
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
    let timer = slint::Timer::default();
    timer.start(
        slint::TimerMode::Repeated,
        std::time::Duration::from_millis(10),
        move || {
            let ui = weak.upgrade().unwrap();
            if !ui.get_busy() {
                result.set(
                    ui.get_error() && !ui.get_done() && ui.get_message().contains("предпросмотр"),
                );
                ui.invoke_cancel();
            } else if std::time::Instant::now() >= deadline {
                let _ = slint::quit_event_loop();
            }
        },
    );
    slint::run_event_loop_until_quit().unwrap();
    timer.stop();
    assert!(
        completed.get(),
        "preview completion did not unlock the form or explain the failure"
    );
    assert!(!install.exists() && !games.exists());
    assert!(
        std::fs::read_to_string(log.path())
            .unwrap()
            .contains("setup.error")
    );

    let mut args = args;
    args[0] = "preview-uninstall".to_owned();
    let removal = create_window(Arc::new(Request::parse(&args).unwrap()), None).unwrap();
    assert!(removal.get_removing());
    assert!(!removal.get_delete_games());
    removal.set_done(true);
    // Completion of removal must only close; never launch an absent launcher.
    removal.invoke_submit();
    assert!(!removal.get_error());
}

#[cfg(windows)]
#[test]
fn nsis_arguments_quote_only_the_games_value_and_keep_destination_last() {
    let args = [
        "preview-install",
        "setup.exe",
        "C:\\Chill Hub",
        "D:\\Игры вместе",
        "1.8.2",
    ]
    .map(str::to_owned);
    let request = Request::parse(&args).unwrap();
    let command = engine_command(&request, false, false);
    let args: Vec<_> = command
        .get_args()
        .map(|s| s.to_string_lossy().into_owned())
        .collect();
    assert_eq!(
        args,
        [
            "/S",
            "/NODESKTOP",
            "/GAMESDIR=\"D:\\Игры вместе\"",
            "/D=C:\\Chill Hub"
        ]
    );
    let mut rollback = request;
    rollback.allow_downgrade = true;
    assert!(
        engine_command(&rollback, true, false)
            .get_args()
            .any(|arg| arg == "/ALLOWDOWNGRADE")
    );
}
#[test]
fn setup_rejects_root_relative_and_command_injection_paths() {
    for path in [
        "",
        "relative",
        "C:\\",
        "C:\\Games\\..\\Windows",
        "C:\\Games\" /S",
        "C:\\Games\n/D=C:\\Windows",
    ] {
        assert!(directory(path).is_err(), "{path}");
    }
    assert!(directory("C:\\Games\\Chill Hub").is_ok());
}

#[test]
fn setup_rejects_existing_files_and_the_windows_directory() {
    let root = tempfile::tempdir().unwrap();
    let file = root.path().join("not a folder.txt");
    std::fs::write(&file, "keep").unwrap();
    assert!(
        directory(file.to_str().unwrap())
            .unwrap_err()
            .contains("уже есть файл")
    );
    assert_eq!(std::fs::read_to_string(&file).unwrap(), "keep");
    assert!(directory(root.path().join("new folder").to_str().unwrap()).is_ok());
    if let Some(windows) = std::env::var_os("WINDIR") {
        let windows = PathBuf::from(windows);
        for path in [&windows, &windows.join("System32")] {
            assert!(
                directory(path.to_str().unwrap())
                    .unwrap_err()
                    .contains("системную папку")
            );
        }
    }
}
#[test]
fn setup_keeps_games_outside_installation_and_rejects_foreign_files() {
    let root = tempfile::tempdir().unwrap();
    let install = root.path().join("launcher");
    let games = root.path().join("games");
    assert!(validate_paths(&install, &install.join("games")).is_err());
    assert!(validate_paths(&install, root.path()).is_err());
    assert!(validate_paths(&install, &games).is_ok());
    std::fs::create_dir(&install).unwrap();
    std::fs::write(install.join("personal.txt"), "keep").unwrap();
    assert!(validate_paths(&install, &games).is_err());
    assert_eq!(
        std::fs::read_to_string(install.join("personal.txt")).unwrap(),
        "keep"
    );
    std::fs::write(install.join("ChillHub.exe"), "existing installation").unwrap();
    assert!(validate_paths(&install, &games).is_ok());
}

#[test]
fn real_setup_requires_an_existing_absolute_engine_and_passes_removal_options() {
    let root = tempfile::tempdir().unwrap();
    let engine = root.path().join("uninstaller.exe");
    let install = root.path().join("Existing Install");
    let mut args = [
        "uninstall".to_owned(),
        "uninstaller.exe".to_owned(),
        install.display().to_string(),
        root.path().join("Games").display().to_string(),
        "1.8.9".to_owned(),
    ];
    assert!(
        Request::parse(&args)
            .unwrap_err()
            .contains("Не найден установщик")
    );
    args[1] = engine.display().to_string();
    assert!(Request::parse(&args).is_err());
    std::fs::write(&engine, "fixture, not an executable").unwrap();
    let request = Request::parse(&args).unwrap();
    assert!(request.removing);
    assert!(!request.preview);
    let command = engine_command(&request, true, true);
    assert_eq!(command.get_program(), engine.as_os_str());
    let removal: Vec<_> = command
        .get_args()
        .map(|arg| arg.to_string_lossy().into_owned())
        .collect();
    assert_eq!(
        removal,
        [
            "/S".to_owned(),
            "/DELETEGAMES".to_owned(),
            format!("_?={}", install.display())
        ]
    );
    let keep = engine_command(&request, true, false);
    assert!(!keep.get_args().any(|arg| arg == "/DELETEGAMES"));
    assert!(
        execute(&request, true, false)
            .unwrap_err()
            .contains("Не удалось начать операцию")
    );
}
#[test]
fn preview_never_runs_the_engine() {
    let args = [
        "preview-uninstall",
        "missing.exe",
        "C:\\ChillHub",
        "C:\\Games\\ChillHub",
        "1.8.1",
    ]
    .map(str::to_owned);
    let request = Request::parse(&args).unwrap();
    assert!(
        execute(&request, false, true)
            .unwrap_err()
            .contains("предпросмотр")
    );
    assert!(Request::parse(&args[..4]).is_err());
}

#[test]
fn successful_engine_exit_still_requires_the_requested_result_on_disk() {
    let temp = tempfile::tempdir().unwrap();
    let install = temp.path().join("launcher");
    let games = temp.path().join("games");
    std::fs::create_dir(&install).unwrap();
    std::fs::create_dir(&games).unwrap();
    let mut request = Request {
        removing: false,
        preview: false,
        engine: temp.path().join("never-run.exe"),
        install: install.clone(),
        games: games.clone(),
        version: "1.8.9".into(),
        allow_downgrade: false,
    };
    let launcher = install.join("ChillHub.exe");
    let version = install.join("launcher.version");
    assert!(verify_setup_result(&request, false).is_err());
    std::fs::write(&version, "1.8.9\r\n").unwrap();
    assert!(verify_setup_result(&request, false).is_err());
    std::fs::write(&launcher, "fixture only").unwrap();
    assert!(verify_setup_result(&request, false).is_ok());
    std::fs::write(&version, "1.8.8").unwrap();
    assert!(verify_setup_result(&request, false).is_err());

    request.removing = true;
    assert!(verify_setup_result(&request, false).is_err());
    std::fs::remove_file(&launcher).unwrap();
    assert!(verify_setup_result(&request, true).is_err());
    assert!(
        verify_setup_result(&request, false)
            .unwrap()
            .contains("Игры остались")
    );
    std::fs::remove_dir(&games).unwrap();
    assert!(
        verify_setup_result(&request, true)
            .unwrap()
            .contains("игры удалены")
    );
}
