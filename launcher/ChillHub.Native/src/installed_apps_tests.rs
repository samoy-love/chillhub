use super::*;
#[test]
fn counts_games_once_and_never_publishes_partial_size() {
    let root = tempfile::tempdir().unwrap();
    let games = root.path().join("games");
    std::fs::create_dir(&games).unwrap();
    std::fs::write(root.path().join("launcher"), [0_u8; 100]).unwrap();
    std::fs::write(games.join("game"), [0_u8; 100]).unwrap();
    assert_eq!(
        total_size(root.path(), &games, Instant::now() + Duration::from_secs(2)),
        Some(200)
    );
    assert_eq!(total_size(root.path(), &games, Instant::now()), None);
    assert_eq!(to_kib(1025), 2);
    assert_eq!(to_kib(u64::MAX), i32::MAX as u32);
}
