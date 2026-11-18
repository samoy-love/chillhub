use super::*;
use crate::manifest::ManifestFile;
use std::{thread, time::Duration};

#[test]
fn manifest_fetch_allows_a_game_operation_and_discards_changed_files() {
    let temp = tempfile::tempdir().unwrap();
    let paths = AppPaths::isolated(temp.path());
    let game = GameRecord {
        game_id: "fixture".into(),
        latest_version: "1.0.0".into(),
        manifest_url: "/manifest".into(),
        ..Default::default()
    };
    let root = paths.game_root(&game.game_id).unwrap();
    fs::create_dir_all(&root).unwrap();
    fs::write(root.join(".version"), "1.0.0").unwrap();
    fs::write(root.join(".chillhub.lock"), []).unwrap();
    fs::write(root.join("Game.exe"), b"obsolete bytes").unwrap();
    let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
    let api = ApiClient::new(&format!("http://{}", server.server_addr())).unwrap();

    thread::scope(|scope| {
        let worker = scope.spawn(|| check(&game, &paths, &api));
        let request = server
            .recv_timeout(Duration::from_secs(5))
            .unwrap()
            .expect("integrity check did not request the manifest");
        assert_eq!(request.url(), "/manifest");
        let lock =
            manifest::open_regular(&root.join(".chillhub.lock"), false, false, false).unwrap();
        let acquired = FileExt::try_lock_exclusive(&lock);
        if acquired.is_ok() {
            fs::write(root.join("Game.exe"), b"changed during the request").unwrap();
            FileExt::unlock(&lock).unwrap();
        }
        let manifest = Manifest {
            game_id: game.game_id.clone(),
            version: game.latest_version.clone(),
            files: vec![ManifestFile {
                path: "Game.exe".into(),
                size: 4,
                blake3: blake3::hash(b"game").to_hex().to_string(),
                ..Default::default()
            }],
            ..Default::default()
        };
        request
            .respond(tiny_http::Response::from_data(
                serde_json::to_vec(&manifest).unwrap(),
            ))
            .unwrap();
        assert!(
            acquired.is_ok(),
            "the request locked the game: {acquired:?}"
        );
        assert!(!worker.join().unwrap().unwrap());
    });
    assert!(!needs_repair(&root, &game.latest_version));
    assert!(!paths.data_dir.join("integrity").exists());
}
