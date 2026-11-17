use super::*;

#[test]
fn installer_requests_preserve_explicit_directory_and_remove_only_owned_paths() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    let owned = temp.path().join("installed games");
    let extra = temp.path().join("extra downloads");
    fs::create_dir(&owned).unwrap();
    fs::create_dir(&extra).unwrap();
    let args = |items: &[&str]| items.iter().map(|s| String::from(*s)).collect::<Vec<_>>();
    let parse = |a: &[String]| request_from_args_in(a, || Ok(journal.clone()));
    for invalid in [
        args(&["--extra"]),
        args(&["--unknown"]),
        args(&["--offer", "--remove"]),
    ] {
        assert!(
            request_from_args_in(&invalid, || panic!(
                "invalid mode must not read the journal"
            ))
            .is_err()
        );
    }
    assert!(parse(&args(&["--remove"])).unwrap().is_none());
    let update_dir = temp.path().join("private staging");
    let offer_args = args(&[
        "--offer",
        "--games",
        owned.to_str().unwrap(),
        "--extra",
        extra.to_str().unwrap(),
        "--updates",
        update_dir.to_str().unwrap(),
    ]);
    let offer = parse(&offer_args).unwrap().unwrap();
    assert_eq!(offer.operation, SecurityOperation::Add);
    for path in [&owned, &extra, &update_dir] {
        assert!(offer.paths.iter().any(|offered| same_path(offered, path)));
    }
    assert!(!update_dir.exists());
    fs::write(
        &journal,
        serde_json::to_vec(&SecurityJournal {
            introduction_shown: true,
            added_paths: vec![owned.clone()],
            ..Default::default()
        })
        .unwrap(),
    )
    .unwrap();
    assert!(parse(&offer_args).unwrap().is_none());
    let removal = parse(&args(&["--remove", "--games", extra.to_str().unwrap()]))
        .unwrap()
        .unwrap();
    assert_eq!(removal.operation, SecurityOperation::Remove);
    assert_eq!(removal.paths, vec![owned]);
    assert!(extra.exists());
}
#[test]
fn broad_and_nonexistent_paths_are_rejected() {
    for path in [
        "C:\\",
        "C:\\Windows",
        "C:\\Users",
        "\\\\server\\share",
        "C:\\safe\\..\\Windows",
        "C:\\file:stream",
        "C:\\*.exe",
    ] {
        assert!(validate_directory(Path::new(path)).is_err(), "{path}");
    }
}
#[test]
fn valid_existing_game_and_request_roundtrip() {
    let tmp = tempfile::tempdir().unwrap();
    let game = tmp.path().join("Games/ChillHub/peak");
    fs::create_dir_all(&game).unwrap();
    let canonical = fs::canonicalize(&game).unwrap();
    let canonical_text = canonical.to_string_lossy();
    let expected = PathBuf::from(
        canonical_text
            .strip_prefix(r"\\?\")
            .unwrap_or(&canonical_text),
    );
    let request = SecurityRequest {
        paths: vec![game],
        operation: SecurityOperation::Add,
    };
    let roundtrip = decode_request(&encode_request(&request).unwrap()).unwrap();
    assert_eq!(roundtrip.paths, vec![expected]);
}
#[cfg(windows)]
#[test]
fn protected_and_offered_directory_aliases_have_one_identity() {
    for name in ["TEMP", "USERPROFILE"] {
        let protected = PathBuf::from(std::env::var_os(name).unwrap());
        let alias = crate::paths::short_test_path(&protected);
        assert!(validate_directory(&alias).is_err(), "{name}");
        assert!(within(&alias, &protected));
        assert!(within(&protected, &alias));
    }
    let temp = tempfile::tempdir().unwrap();
    let games = temp.path().join("Long Games Directory");
    fs::create_dir(&games).unwrap();
    let alias = crate::paths::short_test_path(&games);
    let journal = temp.path().join("security-exclusions.json");
    assert!(!should_offer_in(&journal, Some(&alias.join("Missing game")), &games).unwrap());
    assert!(!alias.join("Missing game").exists());
    assert!(!journal.exists());
}
#[cfg(windows)]
#[test]
fn journal_lock_serializes_short_aliases_before_the_journal_exists() {
    let temp = tempfile::tempdir().unwrap();
    let root = temp.path().join("Long journal directory");
    fs::create_dir(&root).unwrap();
    let journal = fs::canonicalize(&root)
        .unwrap()
        .join("Missing/security.json");
    let alias = crate::paths::short_test_path(&root).join("Missing/security.json");
    let guard = journal_lock(&journal).unwrap();
    let other = alias.clone();
    assert!(
        std::thread::spawn(move || journal_lock(&other).is_err())
            .join()
            .unwrap()
    );
    drop(guard);
    assert!(journal_lock(&alias).is_ok());
    assert!(!root.join("Missing").exists());
}
#[cfg(windows)]
fn registered_alias_fixture(root: &Path) -> (PathBuf, PathBuf) {
    fs::create_dir_all(root).unwrap();
    let long = validate_directory(root).unwrap();
    let mut short = crate::paths::short_test_path(root);
    if same_registered_path(&short, &long) {
        // Volumes may disable 8.3 generation; use a distinct input spelling
        // for the exact-string boundary test without querying Defender.
        short = PathBuf::from(long.to_string_lossy().replace('\\', "/"));
    }
    assert!(same_path(&short, &long));
    assert!(!same_registered_path(&short, &long));
    (short, long)
}
#[cfg(windows)]
#[test]
fn removal_selects_owned_registered_alias_without_substituting_foreign_spelling() {
    let temp = tempfile::tempdir().unwrap();
    let (owned, foreign) = registered_alias_fixture(&temp.path().join("Long Game Directory"));
    let journal = temp.path().join("security.json");
    record_results(
        &journal,
        &[ExclusionResult {
            path: owned.display().to_string(),
            status: "applied".into(),
            detail: String::new(),
            added: true,
        }],
    )
    .unwrap();
    let selected = filter_owned_removals(
        &SecurityRequest {
            paths: vec![foreign.clone()],
            operation: SecurityOperation::Remove,
        },
        &journal,
    )
    .unwrap();
    assert_eq!(selected.paths.len(), 1);
    assert!(same_registered_path(&selected.paths[0], &owned));
    assert!(!same_registered_path(&selected.paths[0], &foreign));
    // This is the same revalidation performed by the elevated boundary.
    let validated = validate_for_operation(&selected.paths, SecurityOperation::Remove).unwrap();
    assert!(same_registered_path(&validated[0], &owned));
    assert!(!result_path_matches(
        SecurityOperation::Remove,
        &owned,
        &foreign
    ));
}
#[cfg(windows)]
#[test]
fn multiple_owned_aliases_keep_separate_results_and_share_one_filesystem_guard() {
    let temp = tempfile::tempdir().unwrap();
    let (first, second) = registered_alias_fixture(&temp.path().join("Long Game Directory"));
    let journal = temp.path().join("security.json");
    for path in [&first, &second] {
        record_results(
            &journal,
            &[ExclusionResult {
                path: path.display().to_string(),
                status: "applied".into(),
                detail: String::new(),
                added: true,
            }],
        )
        .unwrap();
    }
    let selected = filter_owned_removals(
        &SecurityRequest {
            paths: vec![second.clone()],
            operation: SecurityOperation::Remove,
        },
        &journal,
    )
    .unwrap();
    assert_eq!(selected.paths.len(), 2);
    let keys = selected
        .paths
        .iter()
        .map(|path| result_path_key(SecurityOperation::Remove, path).unwrap())
        .collect::<std::collections::HashSet<_>>();
    assert_eq!(keys.len(), 2);
    let guards = mutation_guards(&selected.paths).unwrap();
    assert_eq!(guards.len(), 1);
    drop(guards);
    record_results(
        &journal,
        &[ExclusionResult {
            path: first.display().to_string(),
            status: "removed".into(),
            detail: String::new(),
            added: false,
        }],
    )
    .unwrap();
    let remaining = read_journal(&journal).unwrap();
    assert_eq!(remaining.added_paths.len(), 1);
    assert!(same_registered_path(&remaining.added_paths[0], &second));
    record_results(
        &journal,
        &[ExclusionResult {
            path: second.display().to_string(),
            status: "removed".into(),
            detail: String::new(),
            added: false,
        }],
    )
    .unwrap();
    assert!(read_journal(&journal).unwrap().added_paths.is_empty());
}
#[test]
fn entire_steam_library_is_rejected() {
    let tmp = tempfile::tempdir().unwrap();
    let root = tmp.path().join("SteamLibrary");
    fs::create_dir_all(root.join("steamapps/common")).unwrap();
    assert!(validate_directory(&root).is_err());
    assert!(validate_directory(&root.join("steamapps/common")).is_err());
}
#[test]
fn diagnostic_does_not_claim_quarantine() {
    let report = SecurityReport {
        detections: vec![Detection::default()],
        ..Default::default()
    };
    assert!(report.summary().contains("не доказывает"));
}
#[test]
fn change_script_never_interpolates_data_or_disables_protection() {
    assert!(CHANGE.contains("[Console]::In.ReadToEnd()"));
    assert!(!CHANGE.contains("Set-MpPreference"));
    assert!(!CHANGE.contains("DisableRealtimeMonitoring"));
    assert!(!CHANGE.contains("Restore"));
}

#[test]
fn journal_tracks_only_entries_added_by_this_application() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("security.json");
    record_results(
        &path,
        &[
            ExclusionResult {
                path: "D:\\Games\\ChillHub".into(),
                status: "registered".into(),
                detail: String::new(),
                added: true,
            },
            ExclusionResult {
                path: "D:\\Other".into(),
                status: "registered".into(),
                detail: String::new(),
                added: false,
            },
        ],
    )
    .unwrap();
    let journal = read_journal(&path).unwrap();
    assert_eq!(
        journal.added_paths,
        vec![PathBuf::from("D:\\Games\\ChillHub")]
    );
    record_results(
        &path,
        &[ExclusionResult {
            path: "D:\\Games\\ChillHub".into(),
            status: "removed".into(),
            detail: String::new(),
            added: false,
        }],
    )
    .unwrap();
    assert!(read_journal(&path).unwrap().added_paths.is_empty());
}

#[test]
fn stale_game_exclusion_can_be_removed_without_recreating_directory() {
    let temp = tempfile::tempdir().unwrap();
    let missing = temp.path().join("games/chillhub/removed");
    assert!(validate_directory(&missing).is_err());
    assert!(validate_directory_policy(&missing, false).is_ok());
    assert!(!missing.exists());
}

#[test]
fn direct_remove_request_cannot_claim_preexisting_user_exclusion() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    let owned = temp.path().join("Games/Owned");
    let existing = temp.path().join("Games/Preexisting");
    record_results(
        &journal,
        &[ExclusionResult {
            path: owned.display().to_string(),
            status: "applied".into(),
            detail: String::new(),
            added: true,
        }],
    )
    .unwrap();
    assert!(
        filter_owned_removals(
            &SecurityRequest {
                paths: vec![existing],
                operation: SecurityOperation::Remove
            },
            &journal
        )
        .is_err()
    );
    assert!(
        filter_owned_removals(
            &SecurityRequest {
                paths: vec![owned.clone()],
                operation: SecurityOperation::Remove
            },
            &journal
        )
        .is_ok()
    );
    assert_eq!(read_journal(&journal).unwrap().added_paths, vec![owned]);
}

#[test]
fn offers_are_acknowledged_only_after_display_and_never_change_ownership() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    let games = temp.path().join("Games/ChillHub");
    let external = temp.path().join("Steam/game");
    assert!(should_offer_in(&journal, None, &games).unwrap());
    assert!(!journal.exists());
    acknowledge_offer_in(&journal, None).unwrap();
    assert!(!should_offer_in(&journal, None, &games).unwrap());
    assert!(!should_offer_in(&journal, Some(&games.join("inside")), &games).unwrap());
    assert!(should_offer_in(&journal, Some(&external), &games).unwrap());
    acknowledge_offer_in(&journal, Some(&external)).unwrap();
    assert!(!should_offer_in(&journal, Some(&external), &games).unwrap());
    assert!(read_journal(&journal).unwrap().added_paths.is_empty());
}

#[test]
fn effective_verification_is_bounded_and_never_promotes_unknown_to_success() {
    assert!(CHANGE.contains("-CheckExclusion -Path"));
    assert!(CHANGE.contains("WaitForExit(15000)"));
    assert!(CHANGE.contains("$retry -lt 10"));
    assert!(CHANGE.contains("else { 'unknown' }"));
    assert!(CHANGE.contains("$added=$attempted -and $exists"));
}

#[test]
fn report_exposes_known_defender_states_and_keeps_unknown_fields_optional() {
    let mut report = parse_report(
        r#"{"defenderAvailable":true,"antivirusEnabled":true,"realtimeEnabled":false,
        "tamperProtected":true,"antivirusNames":["Defender","Other"],
        "diagnosticError":"Часть сведений недоступна"}"#,
    )
    .unwrap();
    let summary = report.summary();
    for message in [
        "Антивирус: Defender, Other",
        "Microsoft Defender: включён",
        "Защита в реальном времени: выключена",
        "Защита от изменений: включена",
        "Часть сведений недоступна",
    ] {
        assert!(summary.contains(message), "{summary}");
    }

    report.antivirus_enabled = Some(false);
    report.realtime_enabled = Some(true);
    report.tamper_protected = Some(false);
    let summary = report.summary();
    assert!(summary.contains("Defender: выключен или работает в пассивном режиме"));
    assert!(summary.contains("Защита в реальном времени: включена"));
    assert!(summary.contains("Защита от изменений: выключена"));

    let summary = parse_report(r#"{"defenderAvailable":true}"#)
        .unwrap()
        .summary();
    assert!(summary.contains("состояние неизвестно"));
    assert!(!summary.contains("Защита в реальном времени"));
    assert!(parse_report(r#"{"antivirusEnabled":"yes"}"#).is_err());
}

#[test]
fn request_decoder_rejects_malformed_oversized_and_empty_payloads() {
    use base64::Engine;
    let encode = |bytes: &[u8]| base64::engine::general_purpose::STANDARD.encode(bytes);
    for payload in [
        "!".to_string(),
        encode(b"not json"),
        encode(br#"{"paths":[],"operation":"add"}"#),
        encode(br#"{"paths":[],"operation":"replace"}"#),
        "a".repeat(64 * 1024 + 1),
    ] {
        assert!(decode_request(&payload).is_err());
    }
    let temp = tempfile::tempdir().unwrap();
    let paths = vec![temp.path().join("missing game"); 33];
    let request = SecurityRequest {
        paths,
        operation: SecurityOperation::Add,
    };
    assert!(decode_request(&encode_request(&request).unwrap()).is_err());
    assert!(validate_directories(&request.paths).is_err());
}

#[test]
fn installer_default_update_location_and_encoded_requests_do_not_create_folders() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    let game = temp.path().join("missing game");
    let inputs = vec![
        "--offer".into(),
        "--games".into(),
        game.display().to_string(),
    ];
    let request = request_from_args_in(&inputs, || Ok(journal.clone()))
        .unwrap()
        .unwrap();
    let staging = std::env::temp_dir().join("ChillHub/SelfUpdate");
    assert!(request.paths.iter().any(|path| same_path(path, &staging)));
    assert!(request.paths.iter().any(|path| same_path(path, &game)));
    assert_eq!(request.operation, SecurityOperation::Add);

    let encoded = encode_request(&SecurityRequest {
        paths: vec![game.clone(), game.clone()],
        operation: SecurityOperation::Add,
    })
    .unwrap();
    let decoded = request_from_args_in(&["--request".into(), encoded], || Ok(journal.clone()))
        .unwrap()
        .unwrap();
    assert_eq!(decoded.paths.len(), 1);
    assert!(same_path(&decoded.paths[0], &game));
    assert!(!journal.exists());
    assert!(!game.exists());
}

#[test]
fn invalid_journal_prevents_additions_and_is_preserved_for_recovery() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    let game = temp.path().join("Games/Game");
    let encoded = encode_request(&SecurityRequest {
        paths: vec![game.clone()],
        operation: SecurityOperation::Add,
    })
    .unwrap();
    let args = ["--request".into(), encoded];

    for bad in [b"{broken".to_vec(), vec![b' '; 1024 * 1024 + 1]] {
        fs::write(&journal, &bad).unwrap();
        let error = request_from_args_in(&args, || Ok(journal.clone())).unwrap_err();
        assert!(error.contains("повреждён") || error.contains("слишком велик"));
        assert!(should_offer_at(temp.path(), None, &game).is_err());
        assert!(acknowledge_offer_at(temp.path(), None).is_err());
        assert_eq!(fs::read(&journal).unwrap(), bad);
        assert!(!game.exists());
    }
}

#[test]
fn journal_acknowledgement_preserves_legacy_entries_and_deduplicates_paths() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    let game = temp.path().join("Steam/Game");
    let added = temp.path().join("existing registration");
    let manual = serde_json::json!({"Path":"D:\\Games","Source":"manual"});
    fs::write(
        &journal,
        serde_json::to_vec(&serde_json::json!({
            "AddedPaths": [added],
            "ManualPaths": [manual],
        }))
        .unwrap(),
    )
    .unwrap();
    acknowledge_offer_at(temp.path(), Some(&game)).unwrap();
    acknowledge_offer_at(temp.path(), Some(&game)).unwrap();
    acknowledge_offer_at(temp.path(), None).unwrap();
    let state = read_journal(&journal).unwrap();
    assert_eq!(state.offered_steam_paths.len(), 1);
    assert!(same_path(&state.offered_steam_paths[0], &game));
    assert_eq!(state.added_paths, [added]);
    assert_eq!(state.manual_paths, [manual]);
    assert!(state.introduction_shown);
}

#[test]
fn directory_preparation_creates_only_validated_additions_from_ordinary_session() {
    let temp = tempfile::tempdir().unwrap();
    let game = temp.path().join("Games/Игры");
    let mut request = SecurityRequest {
        paths: vec![game.clone(), PathBuf::from("relative")],
        operation: SecurityOperation::Add,
    };
    assert!(prepare_directories(&request, false).is_err());
    assert!(!game.exists());
    request.paths.pop();
    assert!(
        prepare_directories(&request, true)
            .unwrap_err()
            .contains("без прав")
    );
    assert!(!game.exists());
    prepare_directories(&request, false).unwrap();
    assert!(game.is_dir());
    prepare_directories(&request, true).unwrap();

    let missing = temp.path().join("deleted game");
    request.paths = vec![missing.clone()];
    request.operation = SecurityOperation::Remove;
    prepare_directories(&request, true).unwrap();
    assert!(!missing.exists());
}

fn exclusion_reply(path: &Path, status: &str, added: bool) -> String {
    serde_json::json!([{
        "path": path,
        "status": status,
        "detail": "fixture",
        "added": added,
    }])
    .to_string()
}

#[test]
fn mutation_requires_elevation_and_rejects_the_entire_batch_if_one_path_is_invalid() {
    let temp = tempfile::tempdir().unwrap();
    let game = temp.path().join("Games/Game");
    fs::create_dir_all(&game).unwrap();
    let mut request = SecurityRequest {
        paths: vec![game, temp.path().join("missing")],
        operation: SecurityOperation::Add,
    };
    let result = apply_with(
        &request,
        false,
        |_| panic!("must reject before reaching the mutation boundary"),
        |_| panic!("must reject before recording results"),
    );
    assert!(result.unwrap_err().contains("подтверждение Windows"));
    let result = apply_with(
        &request,
        true,
        |_| panic!("must validate every directory before mutation"),
        |_| panic!("must reject before recording results"),
    );
    assert!(result.unwrap_err().contains("папка не существует"));

    request.paths.pop();
    let selected =
        validate_directories(&[request.paths[0].clone(), request.paths[0].clone()]).unwrap();
    assert_eq!(selected.len(), 1);
    assert!(same_path(&selected[0], &request.paths[0]));
}

#[test]
fn mutation_records_each_result_before_starting_next_and_never_trusts_other_paths() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    let paths: Vec<_> = (0..6)
        .map(|index| {
            let path = temp.path().join(format!("Game {index}"));
            fs::create_dir(&path).unwrap();
            validate_directory(&path).unwrap()
        })
        .collect();
    let request = SecurityRequest {
        paths: paths.clone(),
        operation: SecurityOperation::Add,
    };
    let mut index = 0;
    let results = apply_with(
        &request,
        true,
        |input| {
            let current: SecurityRequest = serde_json::from_slice(input).unwrap();
            assert_eq!(current.paths, [paths[index].clone()]);
            assert_eq!(current.operation, SecurityOperation::Add);
            if index > 0 {
                assert_eq!(read_journal(&journal).unwrap().added_paths, paths[..1]);
            }
            let reply = match index {
                0 => Ok(exclusion_reply(&paths[index], "unknown", true)),
                1 => Ok(exclusion_reply(&paths[0], "applied", true)),
                2 => Ok("[]".into()),
                3 => Ok("malformed".into()),
                4 => Err("offline".into()),
                5 => Ok(exclusion_reply(&paths[index], "existing", false)),
                _ => unreachable!(),
            };
            index += 1;
            reply
        },
        |result| record_results(&journal, std::slice::from_ref(result)),
    )
    .unwrap();
    assert_eq!(
        results
            .iter()
            .map(|r| r.status.as_str())
            .collect::<Vec<_>>(),
        [
            "unknown", "failed", "failed", "failed", "failed", "existing"
        ],
    );
    assert_eq!(results[1].path, paths[1].display().to_string());
    assert!(results[1].detail.contains("Некорректный ответ"));
    assert!(results[2].detail.contains("Некорректный ответ"));
    assert!(results[3].detail.contains("Не удалось прочитать результат"));
    assert_eq!(results[4].detail, "offline");
    assert_eq!(read_journal(&journal).unwrap().added_paths, paths[..1]);
}

#[test]
fn journal_failure_stops_following_mutations_and_stale_removal_keeps_the_folder_absent() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    let game = temp.path().join("Games/Stale");
    let request = SecurityRequest {
        paths: vec![game.clone(), temp.path().join("Games/Later")],
        operation: SecurityOperation::Remove,
    };
    let mut calls = 0;
    let result = apply_with(
        &request,
        true,
        |input| {
            let current: SecurityRequest = serde_json::from_slice(input).unwrap();
            assert_eq!(current.paths, [game.clone()]);
            assert_eq!(current.operation, SecurityOperation::Remove);
            calls += 1;
            Ok(exclusion_reply(&game, "removed", false))
        },
        |result| {
            assert_eq!(result.status, "removed");
            Err("journal unavailable".into())
        },
    );
    assert_eq!(result.unwrap_err(), "journal unavailable");
    assert_eq!(calls, 1);
    assert!(!game.exists());
    assert!(!journal.exists());
}

#[cfg(windows)]
fn hold_journal_open_without_delete(path: &Path) -> fs::File {
    use std::os::windows::fs::OpenOptionsExt;
    use windows_sys::Win32::Storage::FileSystem::FILE_SHARE_READ;

    fs::OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_READ)
        .open(path)
        .unwrap()
}

#[cfg(windows)]
#[test]
fn readable_but_unwritable_journal_blocks_the_first_change() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    let contents = br#"{
        "IntroductionShown": true,
        "FutureVersionField": "preserve the original bytes"
    }"#;
    fs::write(&journal, contents).unwrap();
    let game = temp.path().join("Game");
    fs::create_dir(&game).unwrap();
    let request = SecurityRequest {
        paths: vec![game],
        operation: SecurityOperation::Add,
    };

    let blocker = hold_journal_open_without_delete(&journal);
    assert!(read_journal(&journal).unwrap().introduction_shown);
    let result = apply_in_journal(&request, &journal, |_| {
        panic!("journal preflight must fail before the first mutation");
    });
    assert!(
        result.unwrap_err().contains("Не удалось проверить запись"),
        "a readable journal still has to support atomic replacement",
    );
    assert_eq!(fs::read(&journal).unwrap(), contents);
    drop(blocker);

    preflight_journal(&request, &journal).unwrap();
    assert_eq!(fs::read(&journal).unwrap(), contents);
}

#[cfg(windows)]
#[test]
fn journal_that_becomes_unwritable_reports_possible_change_and_stops_the_batch() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    let request = SecurityRequest {
        paths: ["First", "Second"]
            .into_iter()
            .map(|name| {
                let path = temp.path().join(name);
                fs::create_dir(&path).unwrap();
                validate_directory(&path).unwrap()
            })
            .collect(),
        operation: SecurityOperation::Add,
    };
    let mut changes = 0;
    let mut blocker = None;
    let result = apply_in_journal(&request, &journal, |input| {
        let selected: SecurityRequest = serde_json::from_slice(input).unwrap();
        assert_eq!(selected.paths, request.paths[changes..changes + 1]);
        changes += 1;
        blocker = Some(hold_journal_open_without_delete(&journal));
        Ok(exclusion_reply(&selected.paths[0], "applied", true))
    });
    let error = result.unwrap_err();
    assert!(error.contains("Изменение могло примениться"));
    assert!(error.contains(request.paths[0].to_str().unwrap()));
    assert!(error.contains("журнал сохранить не удалось"));
    assert_eq!(changes, 1);
    drop(blocker);
    assert!(read_journal(&journal).unwrap().added_paths.is_empty());
}

#[cfg(windows)]
#[test]
fn journal_reader_waits_for_atomic_replacement_and_observes_the_new_value() {
    let temp = tempfile::tempdir().unwrap();
    let journal = temp.path().join("security-exclusions.json");
    fs::write(&journal, br#"{"IntroductionShown":false}"#).unwrap();
    let writer_lock = journal_lock(&journal).unwrap();

    let (started, reading) = std::sync::mpsc::channel();
    let (finished, outcome) = std::sync::mpsc::channel();
    let reader_journal = journal.clone();
    let games = temp.path().join("Games");
    let reader = std::thread::spawn(move || {
        started.send(()).unwrap();
        finished
            .send(should_offer_in(&reader_journal, None, &games))
            .unwrap();
    });
    reading.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(
        outcome.recv_timeout(Duration::from_millis(80)),
        Err(std::sync::mpsc::RecvTimeoutError::Timeout),
        "a competing reader must wait before opening the old journal file",
    );
    acknowledge_offer_in(&journal, None).unwrap();
    drop(writer_lock);
    assert!(
        !outcome
            .recv_timeout(Duration::from_secs(5))
            .unwrap()
            .unwrap()
    );
    reader.join().unwrap();
}

#[cfg(windows)]
#[test]
fn powershell_roundtrips_utf8_stdin_and_reports_exit_errors_without_accessing_defender() {
    let input = "  Игры в каталоге 'Game'  ";
    let result = powershell(
        "[Console]::Write([Console]::In.ReadToEnd())",
        Some(input.as_bytes()),
    )
    .unwrap();
    assert_eq!(result, input.trim());
    let error = powershell("[Console]::Error.Write('test error'); exit 7", None).unwrap_err();
    assert_eq!(error, "Windows не выполнила запрос: test error");
}
