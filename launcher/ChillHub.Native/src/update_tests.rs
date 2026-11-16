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
    assert!(request.approved_cleanup_paths.is_empty());

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
        assert!(execute_with_parent(&wrong_parent, &|_, _| {}).is_err());
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
