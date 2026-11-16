use super::*;

#[test]
fn consent_is_empty_or_exact_and_case_insensitive_but_never_a_prefix() {
    let names = |values: &[&str]| values.iter().map(|s| s.to_string()).collect::<Vec<_>>();
    assert!(is_approved(&[], &[]));
    assert!(!is_approved(&names(&["save.txt"]), &[]));
    assert!(is_approved(
        &names(&["SAVE.TXT"]),
        &names(&["save.txt", "gone.txt"])
    ));
    assert!(!is_approved(
        &names(&["save.txt", "new.txt"]),
        &names(&["save.txt"])
    ));
    assert!(!is_approved(
        &names(&["save.txt.bak"]),
        &names(&["save.txt"])
    ));
    assert!(!is_approved(&names(&["dir/save.txt"]), &names(&["dir"])));
}
