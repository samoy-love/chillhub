use super::*;
use std::fs;
#[test]
fn legacy_outcome_is_shown_once_without_losing_a_newer_result() {
    let temp = tempfile::tempdir().unwrap();
    let staged = super::super::tests::package(temp.path());
    let paths = &staged.paths;
    let file = paths.install.join("launcher.update-status");
    let first = "outcome=fatal\nversion=1.8.1\ntime=first\nmessage=Ошибка\\nПуть C:\\\\Games\n";
    fs::write(&file, first).unwrap();
    let shown = super::super::previous_outcome(paths).unwrap();
    assert!(!shown.success);
    assert_eq!(shown.message, "Ошибка\nПуть C:\\Games");
    fs::write(&file, first.replace("time=first", "time=second")).unwrap();
    assert!(!super::super::acknowledge_outcome(paths, &shown).unwrap());
    let next = super::super::previous_outcome(paths).unwrap();
    assert!(super::super::acknowledge_outcome(paths, &next).unwrap());
    assert!(super::super::previous_outcome(paths).is_none());
    assert!(file.exists());
    fs::write(&file, "outcome=ok\nversion=1.8.2\nmessage=Готово\n").unwrap();
    assert!(super::super::previous_outcome(paths).unwrap().success);
}
#[test]
fn malformed_or_oversized_legacy_outcomes_are_ignored() {
    let temp = tempfile::tempdir().unwrap();
    let staged = super::super::tests::package(temp.path());
    let file = staged.paths.install.join("launcher.update-status");
    for data in [
        b"message=only".to_vec(),
        b"outcome=unknown".to_vec(),
        vec![b'x'; 65537],
    ] {
        fs::write(&file, data).unwrap();
        assert!(read(&staged.paths).is_none());
    }
}
#[test]
fn native_result_supersedes_existing_legacy_message() {
    let temp = tempfile::tempdir().unwrap();
    let staged = super::super::tests::package(temp.path());
    fs::write(
        staged.paths.install.join("launcher.update-status"),
        "outcome=fatal\nmessage=Old failure\n",
    )
    .unwrap();
    let current = UpdateOutcome {
        version: "1.8.2".into(),
        success: true,
        message: "New success".into(),
        legacy_fingerprint: None,
    };
    super::super::write_outcome(&staged.paths, &current).unwrap();
    assert_eq!(
        super::super::previous_outcome(&staged.paths),
        Some(current.clone())
    );
    assert!(super::super::acknowledge_outcome(&staged.paths, &current).unwrap());
    assert!(super::super::previous_outcome(&staged.paths).is_none());
}
