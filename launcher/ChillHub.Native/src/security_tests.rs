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
