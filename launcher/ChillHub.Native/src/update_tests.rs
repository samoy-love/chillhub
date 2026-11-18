use super::*;

#[cfg(windows)]
#[test]
fn aliases_share_update_identity_and_cannot_bypass_separate_directories() {
    let temp = tempfile::tempdir().unwrap();
    let parent = temp.path().join("Long shared updater directory");
    fs::create_dir(&parent).unwrap();
    let short = crate::paths::short_test_path(&parent);
    let canonical = fs::canonicalize(&parent).unwrap();
    let install = canonical.join("Launcher installation");
    fs::create_dir(&install).unwrap();
    let aliased_install = short.join("Launcher installation");
    assert_eq!(
        update_mutex_name(&install).unwrap(),
        update_mutex_name(&aliased_install).unwrap()
    );
    assert!(same_path(
        &install.join("Missing.exe"),
        &aliased_install.join("Missing.exe")
    ));
    assert!(!same_path(Path::new("relative"), Path::new("relative")));
    assert!(update_mutex_name(Path::new("relative")).is_err());
    let mut paths = UpdatePaths {
        install: install.clone(),
        config: canonical.join("Settings"),
        games: short.join("Games"),
        staging: short.join("Staging"),
    };
    assert!(paths.validate().is_ok());
    paths.games = aliased_install.join("Future games");
    assert!(paths.validate().is_err());
    paths.games = short.join("Settings/Future games");
    assert!(paths.validate().is_err());
    paths.games = short.join("Games");
    paths.staging = short.join("Settings/Future updates");
    assert!(paths.validate().is_err());
    assert!(!install.join("Future games").exists());
    assert!(!paths.config.exists());
}

#[test]
fn downgrade_and_bad_versions() {
    assert!(version_parts("1.7.20").unwrap() < version_parts("1.8.0").unwrap());
    for s in ["../1.8.1", "1", "1.8.1.exe", "1.-2.3", " 1.8.1"] {
        assert!(version_parts(s).is_err(), "{s}");
    }
}
#[test]
fn legacy_version_shapes_remain_safe_for_update_paths() {
    assert_eq!(version_parts("1.8").unwrap(), [1, 8, 0, 0]);
    assert_eq!(version_parts("1.8.1-beta.2").unwrap(), [1, 8, 1, 0]);
    assert_eq!(version_parts("1.8.1.9-preview").unwrap(), [1, 8, 1, 9]);
    for invalid in [
        "1.8-",
        "1.8-.beta",
        "1.8-beta/../other",
        "1.8-beta\\other",
        "1.8-beta?x=1",
        "1.8-beta\"",
        "1.8-beta-2",
        "1.2.3.4.5",
        "1000000.1",
    ] {
        assert!(version_parts(invalid).is_err(), "{invalid}");
    }
    assert!(version_parts(&format!("1.8-{}", "x".repeat(33))).is_err());
    assert!(version_parts("1.8.1-beta").unwrap() > version_parts("1.8").unwrap());
}
#[test]
fn preserve_is_exact_root_path() {
    assert!(is_preserved("CONFIG.JSON"));
    assert!(!is_preserved("data/config.json"));
    assert!(!is_preserved("config.json.bak"));
}
#[test]
fn only_trusted_https_updates() {
    for s in [
        "http://launcher.samoy.love/api",
        "https://evil.invalid",
        "https://launcher.samoy.love.evil.invalid/api",
        "https://user@launcher.samoy.love/api",
        "https://:password@launcher.samoy.love/api",
        "https://launcher.samoy.love:444/api",
        "https://launcher.samoy.love/api?channel=other",
        "https://launcher.samoy.love/api#x",
        "not a URL",
    ] {
        assert!(trusted_url(s).is_err());
    }
    assert!(trusted_url("https://launcher.samoy.love/api").is_ok());
}

#[test]
fn latest_release_cannot_downgrade_or_use_version_as_a_path() {
    let base = "https://launcher.samoy.love";
    let current = "1.8.9";
    let parse = |json: &str| interpret_release(json.as_bytes(), base, current);
    for remote in ["1.8.8", "1.8.9.0", "1.8.9-rc.1"] {
        let json = format!(r#"{{"version":"{remote}"}}"#);
        assert!(
            matches!(parse(&json).unwrap(), UpdateCheck::Current {current: installed, remote: advertised} if installed == current && advertised == remote)
        );
    }
    let UpdateCheck::Available(release) = parse(r#"{"version":"1.9.0-beta.2"}"#).unwrap() else {
        panic!("newer version was ignored");
    };
    assert_eq!(release.version, "1.9.0-beta.2");
    assert_eq!(
        release.manifest_url,
        format!("{base}/manifests/launcher/1.9.0-beta.2.json")
    );
    assert_eq!(
        release.content_base_url,
        format!("{base}/content/launcher/1.9.0-beta.2/files")
    );
    for bad in [
        r#"{"version":"../outside"}"#,
        r#"{"version":"1.9.0-test/other"}"#,
        r#"{"version":99}"#,
        "{",
    ] {
        assert!(parse(bad).is_err(), "{bad}");
    }
}

#[test]
fn prerelease_updates_advance_numerically_and_final_releases_win() {
    let base = "https://launcher.samoy.love";
    for (current, remote, available) in [
        ("1.8.9-beta.9", "1.8.9-beta.10", true),
        ("1.8.9-beta.10", "1.8.9-beta.9", false),
        ("1.8.9-BETA.10", "1.8.9-beta.10", false),
        ("1.8.9-beta.10", "1.8.9-rc.1", true),
        ("1.8.9-rc.1", "1.8.9", true),
        ("1.8.9", "1.8.9-rc.1", false),
    ] {
        let json = format!(r#"{{"version":"{remote}"}}"#);
        let check = interpret_release(json.as_bytes(), base, current).unwrap();
        assert_eq!(
            matches!(check, UpdateCheck::Available(_)),
            available,
            "{current} -> {remote}"
        );
    }
}

#[test]
fn invalid_manifest_is_rejected_before_offering_an_update() {
    let base = "https://launcher.samoy.love";
    let parse = |version| {
        let json = format!(r#"{{"version":"{version}"}}"#);
        interpret_release(json.as_bytes(), base, "1.8.9").unwrap()
    };
    assert!(
        validate_offer(parse("1.8.9"), |_| panic!(
            "current version needs no manifest"
        ))
        .is_ok()
    );
    assert!(validate_offer(parse("1.9.0"), |_| Ok(Manifest::default())).is_err());
    assert!(
        validate_offer(parse("1.9.0"), |_| {
            Ok(Manifest {
                game_id: "launcher".into(),
                version: "1.9.0".into(),
                files: vec![ManifestFile {
                    path: "../ChillHub.exe".into(),
                    ..Default::default()
                }],
                ..Default::default()
            })
        })
        .is_err()
    );
}

#[test]
fn updater_arguments_require_both_named_flags_in_the_expected_order() {
    let args = |values: &[&str]| values.iter().map(|s| String::from(*s)).collect::<Vec<_>>();
    let correct = args(&["--job", "папка обновления/job.json", "--sha256", "digest"]);
    assert_eq!(
        job_arguments(&correct).unwrap(),
        (Path::new("папка обновления/job.json"), "digest")
    );
    for invalid in [
        args(&[]),
        args(&["--job", "job.json", "--sha256"]),
        args(&["--job", "job.json", "--sha256", "digest", "extra"]),
        args(&["--j0b", "job.json", "--sha256", "digest"]),
        args(&["--job", "job.json", "--hash", "digest"]),
    ] {
        assert!(job_arguments(&invalid).is_err(), "{invalid:?}");
    }
}

#[test]
fn updater_job_checks_digest_session_address_and_parent_identity() {
    let temp = tempfile::tempdir().unwrap();
    let staged = package(temp.path());
    let work = staged.session.join("work");
    fs::create_dir_all(&work).unwrap();
    let path = work.join("job.json");
    let valid = UpdateJob {
        token: "x".repeat(36),
        parent_pid: std::process::id() + 1,
        parent_executable: staged.paths.install.join(LAUNCHER_NAME),
        restart_args: vec![],
        mode: UpdateMode::Apply,
        staged,
    };
    let write = |path: &Path, job: &UpdateJob| {
        let bytes = serde_json::to_vec(job).unwrap();
        fs::write(path, &bytes).unwrap();
        format!("{:x}", Sha256::digest(bytes))
    };
    let hash = write(&path, &valid);
    assert_eq!(decode_job(&path, &hash).unwrap().token, valid.token);
    assert!(decode_job(&path, "wrong digest").is_err());
    assert!(
        read_job(&path, &hash)
            .unwrap_err()
            .contains("вне подготовленной сессии")
    );
    let alternate = work.join("substituted.json");
    assert!(decode_job(&alternate, &write(&alternate, &valid)).is_err());

    for invalid in [
        UpdateJob {
            token: "x".repeat(35),
            ..valid.clone()
        },
        UpdateJob {
            token: "x".repeat(37),
            ..valid.clone()
        },
        UpdateJob {
            parent_pid: 0,
            ..valid.clone()
        },
        UpdateJob {
            parent_pid: std::process::id(),
            ..valid.clone()
        },
        UpdateJob {
            parent_executable: path.clone(),
            ..valid.clone()
        },
    ] {
        assert!(decode_job(&path, &write(&path, &invalid)).is_err());
    }
}
pub(super) fn package(root: &Path) -> StagedUpdate {
    let paths = UpdatePaths {
        install: root.join("install"),
        config: root.join("config"),
        games: root.join("games"),
        staging: root.join("staging"),
    };
    fs::create_dir_all(&paths.install).unwrap();
    let session = paths.staging.join("session");
    fs::create_dir_all(session.join("payload")).unwrap();
    let files = [LAUNCHER_NAME, UPDATER_NAME, "ChillHub.SecurityHelper.exe"]
        .into_iter()
        .map(|name| {
            let bytes = format!("new {name}");
            fs::write(session.join("payload").join(name), &bytes).unwrap();
            fs::write(paths.install.join(name), "old").unwrap();
            StagedFile {
                source: name.into(),
                target: name.into(),
                manifest: ManifestFile {
                    path: name.into(),
                    size: bytes.len() as u64,
                    sha256: Some(format!("{:x}", Sha256::digest(bytes.as_bytes()))),
                    ..Default::default()
                },
            }
        })
        .collect();
    StagedUpdate {
        version: "1.8.1".into(),
        current_version: "1.8.0".into(),
        paths,
        session,
        files,
        delete_paths: vec![],
        empty_dirs: vec![],
    }
}

#[test]
fn historical_package_root_is_stripped_before_guarding_local_files() {
    let root = tempfile::tempdir().unwrap();
    let staged = package(root.path());
    let mut manifest = Manifest {
        files: staged
            .files
            .into_iter()
            .map(|mut file| {
                file.manifest.path = format!("ChillHub-legacy/{}", file.target);
                file.manifest
            })
            .collect(),
        empty_dirs: vec!["ChillHub-legacy/media/unused".into()],
        ..Default::default()
    };
    let (files, dirs) = normalized_payload(&manifest).unwrap();
    assert_eq!(files[0].target, LAUNCHER_NAME);
    assert_eq!(files[0].source, format!("ChillHub-legacy/{LAUNCHER_NAME}"));
    assert_eq!(dirs, ["media/unused"]);

    let saved = manifest.files[1].path.clone();
    manifest.files[1].path = "OtherPackage/ChillHub.Updater.exe".into();
    assert!(
        normalized_payload(&manifest)
            .unwrap_err()
            .contains("корень пакета")
    );
    manifest.files[1].path = saved;
    let mut state = manifest.files[0].clone();
    state.path = "ChillHub-legacy/Config.JSON".into();
    manifest.files.push(state);
    assert!(
        normalized_payload(&manifest)
            .unwrap_err()
            .contains("состояние установки")
    );
}

#[test]
fn launcher_prepares_a_hashed_job_and_preserves_the_recovery_worker() {
    let root = tempfile::tempdir().unwrap();
    let staged = package(root.path());
    assert!(
        launch_updater(&staged, &[])
            .unwrap_err()
            .contains("установленного ChillHub.exe")
    );
    assert!(
        launch_recovery(&staged, &[])
            .unwrap_err()
            .contains("Журнал восстановления")
    );
    let installed_helper = staged.paths.install.join(UPDATER_NAME);
    let mut job = UpdateJob {
        parent_pid: std::process::id() + 1,
        parent_executable: staged.paths.install.join(LAUNCHER_NAME),
        restart_args: vec!["--game".into(), "Тест игры".into()],
        token: uuid::Uuid::new_v4().to_string(),
        mode: UpdateMode::Apply,
        staged,
    };
    let worker = prepare_worker(&job).unwrap();
    assert_eq!(
        worker.executable,
        job.staged.session.join("work").join(UPDATER_NAME)
    );
    assert_eq!(fs::read_to_string(&worker.executable).unwrap(), "old");
    let decoded = decode_job(&worker.job_file, &worker.sha256).unwrap();
    assert_eq!(decoded.mode, UpdateMode::Apply);
    assert_eq!(decoded.token, job.token);
    assert_eq!(decoded.restart_args, job.restart_args);

    fs::write(&installed_helper, "replacement").unwrap();
    job.mode = UpdateMode::Recovery;
    let recovery = prepare_worker(&job).unwrap();
    assert_eq!(fs::read_to_string(&recovery.executable).unwrap(), "old");
    assert_ne!(recovery.sha256, worker.sha256);
    assert_eq!(
        decode_job(&recovery.job_file, &recovery.sha256)
            .unwrap()
            .mode,
        UpdateMode::Recovery
    );
}

#[cfg(windows)]
#[test]
fn updater_only_records_a_launch_attempt_after_the_expected_ready_token() {
    use std::os::windows::process::CommandExt;
    use std::process::Stdio;
    use windows_sys::Win32::System::Threading::CREATE_NO_WINDOW;

    let temp = tempfile::tempdir().unwrap();
    let staged = package(temp.path());
    let work = staged.session.join("work");
    fs::create_dir(&work).unwrap();
    let ready = work.join("ready");
    let mut job = UpdateJob {
        parent_pid: 1,
        parent_executable: staged.paths.install.join(LAUNCHER_NAME),
        restart_args: vec![],
        token: uuid::Uuid::new_v4().to_string(),
        mode: UpdateMode::Apply,
        staged,
    };
    let ping = PathBuf::from(std::env::var_os("SystemRoot").unwrap()).join("System32/ping.exe");
    let mut child = Command::new(ping)
        .args(["-n", "5", "127.0.0.1"])
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .unwrap();

    let attempts =
        |job: &UpdateJob| update_attempt_count(&job.staged.paths, &job.staged.version).unwrap();
    for suffix in ["\n".into(), "x".repeat(1024)] {
        fs::write(&ready, format!("{}{suffix}", job.token)).unwrap();
        let timeout =
            acknowledge_launch(&job, &mut child, 0, Duration::from_millis(100)).unwrap_err();
        assert!(timeout.contains("не подтвердил готовность"), "{timeout}");
        assert_eq!(attempts(&job), 0);
    }

    fs::write(&ready, &job.token).unwrap();
    acknowledge_launch(&job, &mut child, 0, Duration::from_secs(1)).unwrap();
    assert_eq!(attempts(&job), 1);
    job.mode = UpdateMode::Recovery;
    acknowledge_launch(&job, &mut child, 1, Duration::from_secs(1)).unwrap();
    assert_eq!(attempts(&job), 1);

    fs::write(&ready, "different nonce").unwrap();
    let _ = child.kill();
    child.wait().unwrap();
    let early_exit = acknowledge_launch(&job, &mut child, 1, Duration::from_secs(1)).unwrap_err();
    assert!(
        early_exit.contains("завершился до начала работы"),
        "{early_exit}"
    );
    assert_eq!(attempts(&job), 1);
}
#[test]
fn transaction_preserves_configuration_and_commits_marker() {
    let temp = tempfile::tempdir().unwrap();
    let staged = package(temp.path());
    fs::write(staged.paths.install.join("config.json"), "private").unwrap();
    apply_transaction(&staged, &|_, _| {}).unwrap();
    assert_eq!(
        fs::read_to_string(staged.paths.install.join("config.json")).unwrap(),
        "private"
    );
    assert_eq!(
        fs::read_to_string(staged.paths.install.join("launcher.version")).unwrap(),
        "1.8.1"
    );
}

#[test]
fn transaction_replaces_owned_payload_removes_obsolete_files_and_expires_its_attempts() {
    let temp = tempfile::tempdir().unwrap();
    let mut staged = package(temp.path());
    let install = &staged.paths.install;
    let config = &staged.paths.config;
    fs::create_dir(config).unwrap();
    fs::write(install.join("launcher.version"), &staged.current_version).unwrap();
    fs::write(install.join("obsolete.dll"), "previous payload").unwrap();
    fs::write(install.join("personal.txt"), "user file").unwrap();
    fs::write(config.join("update-attempts.json"), "stale history").unwrap();
    staged.delete_paths = vec!["obsolete.dll".into(), "already-missing.dll".into()];
    staged.empty_dirs.push("media/empty".into());

    apply_transaction(&staged, &|_, _| {}).unwrap();

    let inventory: Vec<String> =
        serde_json::from_slice(&fs::read(config.join("installed-payload.json")).unwrap()).unwrap();
    assert_eq!(
        inventory,
        staged
            .files
            .iter()
            .map(|file| file.target.clone())
            .collect::<Vec<_>>()
    );
    assert!(!config.join("update-attempts.json").exists());
    assert!(!install.join("obsolete.dll").exists());
    assert!(install.join("media/empty").is_dir());
    assert_eq!(
        fs::read(install.join("personal.txt")).unwrap(),
        b"user file"
    );
    assert_eq!(
        fs::read(staged.session.join("backup/obsolete.dll")).unwrap(),
        b"previous payload"
    );
    assert_eq!(
        fs::read_to_string(staged.session.join("backup/launcher.version")).unwrap(),
        staged.current_version
    );
    assert_eq!(
        fs::read_to_string(install.join("launcher.version")).unwrap(),
        staged.version
    );
    assert!(!recover_interrupted(&staged).unwrap());
    assert!(
        apply_transaction(&staged, &|_, _| {})
            .unwrap_err()
            .contains("повторное применение запрещено")
    );
}

#[test]
fn updater_keeps_an_existing_directory_at_a_payload_file_path() {
    let temp = tempfile::tempdir().unwrap();
    let staged = package(temp.path());
    let collision = staged.paths.install.join(UPDATER_NAME);
    fs::remove_file(&collision).unwrap();
    fs::create_dir(&collision).unwrap();
    fs::write(collision.join("personal.txt"), "keep me").unwrap();
    let error = apply_transaction(&staged, &|_, _| {}).unwrap_err();
    assert!(error.contains("На месте файла находится папка"), "{error}");
    assert!(error.contains(UPDATER_NAME), "{error}");
    assert_eq!(
        fs::read(collision.join("personal.txt")).unwrap(),
        b"keep me"
    );
    assert_eq!(
        fs::read(staged.paths.install.join(LAUNCHER_NAME)).unwrap(),
        b"old"
    );
    assert!(!staged.session.join("journal.json").exists());
}

#[test]
fn invalid_staged_metadata_is_rejected_before_changing_the_installation() {
    let temp = tempfile::tempdir().unwrap();
    let staged = package(temp.path());
    let mut duplicate = staged.files.clone();
    let mut repeated = duplicate[0].clone();
    repeated.target = repeated.target.to_ascii_lowercase();
    duplicate.push(repeated);
    let cases = [
        (
            StagedUpdate {
                version: staged.current_version.clone(),
                ..staged.clone()
            },
            "не повышает версию",
        ),
        (
            StagedUpdate {
                session: staged.paths.staging.clone(),
                ..staged.clone()
            },
            "вне временной папки",
        ),
        (
            StagedUpdate {
                files: duplicate,
                ..staged.clone()
            },
            "содержит повтор",
        ),
        (
            StagedUpdate {
                files: staged.files[..2].to_vec(),
                ..staged.clone()
            },
            "Нет обязательного файла",
        ),
        (
            StagedUpdate {
                delete_paths: vec!["launcher.version".into()],
                ..staged.clone()
            },
            "Недопустимый список удаляемых файлов",
        ),
        (
            StagedUpdate {
                empty_dirs: vec!["launcher.version".into()],
                ..staged.clone()
            },
            "Недопустимая папка пакета",
        ),
    ];

    for (bad, expected) in cases {
        let actual = apply_transaction(&bad, &|_, _| {}).unwrap_err();
        assert!(actual.contains(expected), "{actual}");
    }
    assert_eq!(
        fs::read(staged.paths.install.join(LAUNCHER_NAME)).unwrap(),
        b"old"
    );
    assert!(!staged.session.join("journal.json").exists());
}

#[test]
fn updater_rejects_a_wrong_blake3_even_with_correct_size_and_sha256() {
    let temp = tempfile::tempdir().unwrap();
    let mut staged = package(temp.path());
    staged.files[0].manifest.blake3 = "0".repeat(64);
    let error = apply_transaction(&staged, &|_, _| {}).unwrap_err();
    assert!(error.contains("Проверка файла не пройдена"), "{error}");
    assert_eq!(
        fs::read(staged.paths.install.join(LAUNCHER_NAME)).unwrap(),
        b"old"
    );
    assert!(!staged.session.join("journal.json").exists());
}

#[test]
fn interrupted_update_restarts_only_when_original_installation_is_restored() {
    let temp = tempfile::tempdir().unwrap();
    let staged = package(temp.path());
    let journal_path = staged.session.join("journal.json");
    let backup = staged.session.join("backup");
    fs::create_dir(&backup).unwrap();
    assert!(safe_to_restart(&staged));
    fs::write(&journal_path, "{").unwrap();
    assert!(!safe_to_restart(&staged));

    let mut journal = Journal {
        committed: false,
        recovered: false,
        files: vec![
            Backup {
                target: LAUNCHER_NAME.into(),
                existed: true,
                attributes: None,
            },
            Backup {
                target: "fresh.dll".into(),
                existed: false,
                attributes: None,
            },
        ],
    };
    let write_journal = |journal: &Journal| {
        fs::write(&journal_path, serde_json::to_vec(journal).unwrap()).unwrap();
    };
    write_journal(&journal);
    assert!(!safe_to_restart(&staged), "missing original backup");
    fs::write(backup.join(LAUNCHER_NAME), "old").unwrap();
    assert!(safe_to_restart(&staged));
    fs::write(
        staged.paths.install.join("fresh.dll"),
        "partially installed",
    )
    .unwrap();
    assert!(!safe_to_restart(&staged), "new payload file remains");
    fs::remove_file(staged.paths.install.join("fresh.dll")).unwrap();
    fs::write(staged.paths.install.join(LAUNCHER_NAME), "inconsistent").unwrap();
    assert!(
        !safe_to_restart(&staged),
        "installed file differs from original"
    );

    journal.files[0].target = "../outside.exe".into();
    write_journal(&journal);
    assert!(!safe_to_restart(&staged));
    assert!(
        recover_interrupted(&staged)
            .unwrap_err()
            .contains("Журнал восстановления повреждён")
    );
}

#[test]
fn recovery_reports_missing_backups_and_still_restores_unaffected_files() {
    let temp = tempfile::tempdir().unwrap();
    let staged = package(temp.path());
    let backup = staged.session.join("backup");
    fs::create_dir(&backup).unwrap();
    fs::write(backup.join(LAUNCHER_NAME), "original launcher").unwrap();
    let journal = Journal {
        committed: false,
        recovered: false,
        files: [LAUNCHER_NAME, UPDATER_NAME]
            .into_iter()
            .map(|target| Backup {
                target: target.into(),
                existed: true,
                attributes: None,
            })
            .collect(),
    };
    let journal_path = staged.session.join("journal.json");
    let previous = serde_json::to_vec(&journal).unwrap();
    fs::write(&journal_path, &previous).unwrap();
    assert!(
        recover_interrupted(&staged)
            .unwrap_err()
            .contains(UPDATER_NAME)
    );
    assert_eq!(
        fs::read(staged.paths.install.join(LAUNCHER_NAME)).unwrap(),
        b"original launcher"
    );
    assert_eq!(fs::read(&journal_path).unwrap(), previous);
    assert!(!safe_to_restart(&staged));
}
#[test]
fn corrupted_payload_never_touches_installation() {
    let temp = tempfile::tempdir().unwrap();
    let staged = package(temp.path());
    fs::write(staged.session.join("payload").join(LAUNCHER_NAME), "bad").unwrap();
    assert!(apply_transaction(&staged, &|_, _| {}).is_err());
    assert_eq!(
        fs::read_to_string(staged.paths.install.join(LAUNCHER_NAME)).unwrap(),
        "old"
    );
}
#[test]
fn rejected_overlap_cannot_mutate_state() {
    let temp = tempfile::tempdir().unwrap();
    let mut staged = package(temp.path());
    staged.paths.config = staged.paths.install.join("data");
    assert!(validate_staged(&staged).is_err());
}

#[test]
fn staged_manifest_file_must_have_a_valid_digest() {
    let temp = tempfile::tempdir().unwrap();
    let mut staged = package(temp.path());
    staged.files[0].manifest.sha256 = Some("malformed".into());
    assert!(validate_staged(&staged).unwrap_err().contains("хеш файла"));
}

#[test]
fn staging_downloads_verified_payload_and_preserves_installation() {
    let temp = tempfile::tempdir().unwrap();
    let previous = package(temp.path());
    let paths = previous.paths;
    fs::create_dir(&paths.config).unwrap();
    fs::write(
        paths.config.join("installed-payload.json"),
        br#"["obsolete.dll", "config.json", "ChillHub.exe"]"#,
    )
    .unwrap();
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let release = UpdateRelease {
        version: "999.0.0".into(),
        manifest_url: format!("http://{}/manifest.json", server.server_addr()),
        content_base_url: format!("http://{}/files/", server.server_addr()),
    };
    let payload = paths.staging.join("payload policy fixture");
    let request = update_request(&release, payload.clone());
    assert_eq!(request.game_id, "launcher");
    assert_eq!(request.version, release.version);
    assert_eq!(request.root, payload);
    assert_eq!(request.manifest_url, release.manifest_url);
    assert_eq!(request.content_base_url, release.content_base_url);
    assert_eq!(request.scope, SyncScope::OwnFilesOnly);
    assert!(!request.write_markers);
    assert!(request.approved_cleanup_files.is_empty());

    let files: Vec<_> = previous.files.into_iter().map(|f| f.manifest).collect();
    let manifest = Manifest {
        game_id: "launcher".into(),
        version: release.version.clone(),
        files,
        empty_dirs: vec!["empty-data".into()],
        ..Default::default()
    };
    let downloader = std::thread::spawn(move || {
        for _ in 0..3 {
            let request = server
                .recv_timeout(Duration::from_secs(10))
                .unwrap()
                .unwrap();
            let name = request.url().strip_prefix("/files/").unwrap();
            let payload = format!("new {name}");
            request
                .respond(tiny_http::Response::from_string(payload))
                .unwrap();
        }
    });
    let staged = stage_manifest(
        &release,
        &paths,
        &TransferEngine::for_update().unwrap(),
        manifest,
        &TransferControl::default(),
        &|_| {},
    )
    .unwrap();
    downloader.join().unwrap();
    assert_eq!(staged.delete_paths, ["obsolete.dll"]);
    assert_eq!(staged.empty_dirs, ["empty-data"]);
    assert!(!staged.session.join("payload/.version").exists());
    assert!(!paths.install.join("launcher.version").exists());
    assert_eq!(
        fs::read_to_string(paths.install.join(LAUNCHER_NAME)).unwrap(),
        "old"
    );
    assert_eq!(
        fs::read_to_string(staged.session.join("payload").join(LAUNCHER_NAME)).unwrap(),
        format!("new {LAUNCHER_NAME}")
    );
    assert!(
        fs::read_to_string(staged.session.join("stage-owner.json"))
            .unwrap()
            .contains("prepared")
    );
}
#[test]
fn recovery_restores_original_and_removes_new_file() {
    let temp = tempfile::tempdir().unwrap();
    let staged = package(temp.path());
    fs::create_dir_all(staged.session.join("backup")).unwrap();
    fs::write(
        staged.session.join("backup").join(LAUNCHER_NAME),
        "original",
    )
    .unwrap();
    let journal = Journal {
        committed: false,
        recovered: false,
        files: vec![
            Backup {
                target: LAUNCHER_NAME.into(),
                existed: true,
                attributes: None,
            },
            Backup {
                target: UPDATER_NAME.into(),
                existed: false,
                attributes: None,
            },
        ],
    };
    atomic_write(
        &staged.session.join("journal.json"),
        &serde_json::to_vec(&journal).unwrap(),
    )
    .unwrap();
    assert!(recover_interrupted(&staged).unwrap());
    assert_eq!(
        fs::read_to_string(staged.paths.install.join(LAUNCHER_NAME)).unwrap(),
        "original"
    );
    assert!(!staged.paths.install.join(UPDATER_NAME).exists());
}

#[test]
fn pending_recovery_only_offers_uncommitted_jobs_for_this_installation() {
    let temp = tempfile::tempdir().unwrap();
    let staged = package(temp.path());
    let work = staged.session.join("work");
    fs::create_dir_all(&work).unwrap();
    fs::create_dir(staged.paths.staging.join("empty-session")).unwrap();
    fs::write(staged.paths.staging.join("unrelated.txt"), "leave me").unwrap();
    let journal_path = staged.session.join("journal.json");
    let mut journal = Journal {
        committed: false,
        recovered: false,
        files: vec![],
    };
    fs::write(&journal_path, serde_json::to_vec(&journal).unwrap()).unwrap();
    let mut job = UpdateJob {
        parent_pid: 123,
        parent_executable: staged.paths.install.join(LAUNCHER_NAME),
        restart_args: vec![],
        mode: UpdateMode::Apply,
        token: "x".repeat(36),
        staged: staged.clone(),
    };
    let job_path = work.join("job.json");
    fs::write(&job_path, serde_json::to_vec(&job).unwrap()).unwrap();
    let pending = pending_recoveries(&staged.paths).unwrap();
    assert_eq!(pending.len(), 1);
    assert_eq!(pending[0].session, staged.session);

    job.staged.paths.install = temp.path().join("another-installation");
    fs::create_dir(&job.staged.paths.install).unwrap();
    fs::write(&job_path, serde_json::to_vec(&job).unwrap()).unwrap();
    assert!(
        pending_recoveries(&staged.paths)
            .unwrap_err()
            .contains("другой установке")
    );

    journal.recovered = true;
    fs::write(&journal_path, serde_json::to_vec(&journal).unwrap()).unwrap();
    assert!(pending_recoveries(&staged.paths).unwrap().is_empty());
}

#[cfg(windows)]
#[test]
fn updater_waits_for_identified_parent_before_applying_or_recovering() {
    use std::os::windows::process::CommandExt;
    use std::process::Stdio;
    use std::sync::Mutex;
    use windows_sys::Win32::System::Threading::CREATE_NO_WINDOW;

    let executable = PathBuf::from(std::env::var_os("SystemRoot").unwrap())
        .join("System32")
        .join("cmd.exe");
    for mode in [UpdateMode::Apply, UpdateMode::Recovery] {
        let temp = tempfile::tempdir().unwrap();
        let staged = package(temp.path());
        let work = staged.session.join("work");
        fs::create_dir(&work).unwrap();
        let launcher = staged.paths.install.join(LAUNCHER_NAME);
        if mode == UpdateMode::Recovery {
            fs::write(&launcher, "interrupted").unwrap();
            let backup_dir = staged.session.join("backup");
            fs::create_dir(&backup_dir).unwrap();
            fs::write(backup_dir.join(LAUNCHER_NAME), "old").unwrap();
            let journal = Journal {
                committed: false,
                recovered: false,
                files: vec![Backup {
                    target: LAUNCHER_NAME.into(),
                    existed: true,
                    attributes: None,
                }],
            };
            fs::write(
                staged.session.join("journal.json"),
                serde_json::to_vec(&journal).unwrap(),
            )
            .unwrap();
        }
        let mut parent = Command::new(&executable)
            .args(["/d", "/c", "set", "/p", "fixture="])
            .stdin(Stdio::piped())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .creation_flags(CREATE_NO_WINDOW)
            .spawn()
            .unwrap();
        let input = Mutex::new(parent.stdin.take());
        let job = UpdateJob {
            mode,
            parent_pid: parent.id(),
            parent_executable: executable.clone(),
            restart_args: ["/d", "/c", "exit", "0"].map(String::from).to_vec(),
            token: "fixture-parent-ack".into(),
            staged,
        };
        let wrong_parent = UpdateJob {
            parent_executable: launcher.clone(),
            ..job.clone()
        };
        assert!(
            execute_with_parent(&wrong_parent, &|_, _| {
                panic!("updater acknowledged a different process")
            })
            .is_err()
        );
        assert!(!work.join("ready").exists());

        execute_with_parent(&job, &|_, _| {
            if let Some(input) = input.lock().unwrap().take() {
                assert_eq!(fs::read_to_string(work.join("ready")).unwrap(), job.token);
                let before = if mode == UpdateMode::Apply {
                    "old"
                } else {
                    "interrupted"
                };
                assert_eq!(fs::read_to_string(&launcher).unwrap(), before);
                drop(input);
            }
        })
        .unwrap();
        parent.wait().unwrap();
        let expected = if mode == UpdateMode::Apply {
            format!("new {LAUNCHER_NAME}")
        } else {
            "old".to_owned()
        };
        assert_eq!(fs::read_to_string(&launcher).unwrap(), expected);
        let outcome = previous_outcome(&job.staged.paths).unwrap();
        assert!(outcome.success);
        assert_eq!(
            outcome.version,
            if mode == UpdateMode::Apply {
                job.staged.version
            } else {
                job.staged.current_version
            }
        );
    }
}

#[test]
fn error_after_file_replacement_rolls_back_all_files() {
    let temp = tempfile::tempdir().unwrap();
    let mut staged = package(temp.path());
    fs::write(staged.paths.install.join("occupied"), "user file").unwrap();
    staged.empty_dirs.push("occupied".into());
    assert!(apply_transaction(&staged, &|_, _| {}).is_err());
    for name in [LAUNCHER_NAME, UPDATER_NAME, "ChillHub.SecurityHelper.exe"] {
        assert_eq!(
            fs::read_to_string(staged.paths.install.join(name)).unwrap(),
            "old"
        );
    }
    assert!(!staged.paths.install.join("launcher.version").exists());
    assert_eq!(
        fs::read_to_string(staged.paths.install.join("occupied")).unwrap(),
        "user file"
    );
    assert!(safe_to_restart(&staged));
}
