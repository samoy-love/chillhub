use super::*;
#[test]
fn shared_helper_lifetimes_block_update_until_both_exit() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("security-session.lock");
    let parent = open_lease(&path, true, false).unwrap();
    let elevated = open_lease(&path, false, false).unwrap();
    assert!(update_lease(temp.path()).is_err());
    drop(parent);
    assert!(update_lease(temp.path()).is_err());
    drop(elevated);
    assert!(update_lease(temp.path()).is_ok());
}
#[test]
fn session_tokens_are_fixed_size_hex() {
    assert!(validate_token(&"ab".repeat(32)));
    for value in [
        String::new(),
        "x".repeat(64),
        "a".repeat(63),
        "a".repeat(65),
    ] {
        assert!(!validate_token(&value));
    }
}
