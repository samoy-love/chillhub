use super::*;
use std::path::PathBuf;

fn actual_catalog() -> Vec<Game> {
    let source_file = PathBuf::from(file!());
    let source_file = if source_file.is_absolute() {
        source_file
    } else {
        Path::new(env!("CARGO_MANIFEST_DIR")).join(source_file)
    };
    let path = source_file.parent().unwrap().join("../Data/games.json");
    load(&path).unwrap()
}

#[test]
fn actual_catalog_preserves_metadata_and_library_order() {
    let games = actual_catalog();
    assert_eq!(games.len(), 9);
    assert_eq!(games.iter().filter(|game| game.has_mods).count(), 4);
    let ids: Vec<_> = games.iter().map(|game| game.id.as_str()).collect();
    assert_eq!(
        ids,
        [
            "peak",
            "lethal-company",
            "over-the-hill",
            "bombanana",
            "farfarwest",
            "how-to-fish",
            "repo",
            "drive-beyond-horizons",
            "machine-party"
        ]
    );
    let peak = &games[0];
    assert_eq!(peak.steam_app_id, "3527290");
    assert!(!peak.modpack_revision.is_empty());
    assert_eq!(peak.media[0].kind, "video");
    assert!(!peak.media[0].url.is_empty());
    assert_eq!(peak.mods.len(), 233);
    assert!(
        peak.mods
            .iter()
            .all(|entry| !entry.name.is_empty() && !entry.russian_name.is_empty())
    );
}

#[test]
fn live_video_qualities_reach_player_without_losing_attribution() {
    let record: GameRecord = serde_json::from_value(serde_json::json!({
        "gameId":"video-fixture", "title":"Видео", "presentation": {"media":[{
            "kind":"video", "provider":"file", "url":"/assets/main.mp4",
            "sourceUrl":"https://example.org/original", "variants":[
                {"url":"/assets/720.mp4","width":1280,"height":720,"bytes":12345},
                {"url":"/assets/1080.mp4","width":1920,"height":1080,"bytes":23456}
            ]
        }, {"kind":"video", "provider":"youtube", "url":"https://www.youtube.com/watch?v=abcdefghijk"}]}
    }))
    .unwrap();
    let games = from_live(&[record], &[]);
    assert_eq!(
        games[0].media.len(),
        1,
        "retired external cards must be hidden"
    );
    let media = &games[0].media[0];
    assert_eq!(media.variants.len(), 2);
    assert_eq!(media.variants[0].url, "/assets/720.mp4");
    assert_eq!(media.variants[1].height, 1080);
    assert_eq!(media.variants[1].bytes, 23456);
    assert_eq!(media.source, "https://example.org/original");
}

#[test]
fn library_search_matches_title_genre_and_modpack() {
    let games = actual_catalog();
    assert_eq!(filtered_indices(&games, "  lEtHaL  "), [1]);
    assert_eq!(filtered_indices(&games, "PEAKFRIENDS"), [0]);
    assert!(filtered_indices(&games, "ВОСХОЖДЕНИЕ").contains(&0));
    assert_eq!(filtered_indices(&games, "").len(), games.len());
    assert_eq!(filtered_indices(&games, "bombanana").len(), 1);
    assert!(filtered_indices(&games, "missing game").is_empty());
}

#[test]
fn all_four_launch_choices_survive_when_available() {
    let game = &actual_catalog()[0];
    assert_eq!(default_choice(game), ("local".into(), "mods".into()));
    for source in ["steam", "local"] {
        for mode in ["mods", "plain"] {
            assert_eq!(
                valid_choice(game, source, mode),
                (source.into(), mode.into())
            );
        }
    }
    assert_eq!(
        valid_choice(game, "unknown", "unknown"),
        default_choice(game)
    );
}

#[test]
fn steam_defaults_to_vanilla_preserving_an_existing_explicit_modded_choice() {
    let mut game = actual_catalog().remove(0);
    assert_eq!(
        switching_source(&game, &default_choice(&game), "steam"),
        ("steam".into(), "plain".into())
    );
    let steam_mods = valid_choice(&game, "steam", "mods");
    assert_eq!(switching_source(&game, &steam_mods, "steam"), steam_mods);
    assert_eq!(
        valid_choice(&game, "steam", ""),
        ("steam".into(), "plain".into())
    );
    game.local_supported = false;
    assert_eq!(default_choice(&game), ("steam".into(), "plain".into()));
}

#[test]
fn unavailable_source_and_modpack_are_normalized() {
    let mut game = Game {
        local_supported: true,
        ..Game::default()
    };
    assert_eq!(default_choice(&game), ("local".into(), "plain".into()));
    assert_eq!(
        valid_choice(&game, "steam", "mods"),
        ("local".into(), "plain".into())
    );
    game.has_mods = true;
    assert_eq!(
        valid_choice(&game, "steam", "mods"),
        ("local".into(), "mods".into())
    );
    game.local_supported = false;
    game.steam_supported = true;
    assert_eq!(
        valid_choice(&game, "local", "mods"),
        ("steam".into(), "plain".into())
    );
    assert_eq!(
        valid_choice(&game, " STEAM ", "PLAIN"),
        ("steam".into(), "plain".into())
    );
    game.steam_supported = false;
    assert_eq!(
        valid_choice(&game, "steam", "mods"),
        (String::new(), String::new())
    );
}

#[test]
fn live_catalog_is_authoritative_and_mod_descriptions_match_exact_build() {
    use chillhub_native::model::{ModDescription, ModsRecord, Presentation};
    let seed = actual_catalog();
    assert!(from_live(&[], &seed).is_empty());
    let mut record = GameRecord {
        game_id: "peak".into(),
        title: "PEAK".into(),
        has_latest: true,
        latest_version: "1.0.0".into(),
        mods: Some(ModsRecord {
            has_latest: true,
            version: "pack-1".into(),
            revision: "abcdef".into(),
            ..Default::default()
        }),
        presentation: Some(Presentation {
            modpack_version: "pack-1".into(),
            modpack_revision: "ABCDEF".into(),
            mods: vec![ModDescription {
                name: "Published mod".into(),
                ..Default::default()
            }],
            ..Default::default()
        }),
        ..Default::default()
    };
    assert_eq!(from_live(&[record.clone()], &seed)[0].mods.len(), 1);
    record.mods.as_mut().unwrap().version = "pack-2".into();
    let updated = from_live(&[record], &seed).remove(0);
    assert!(updated.mods.is_empty());
    assert!(updated.has_mods);
    assert!(updated.modpack_description.contains("ещё не подготовлен"));
}
#[test]
fn reordered_media_keeps_its_own_offline_artwork() {
    let seed = actual_catalog();
    let original = seed.iter().find(|g| g.id == "peak").unwrap();
    let mut presentations: std::collections::HashMap<String, chillhub_native::model::Presentation> =
        serde_json::from_str(include_str!("../Data/presentation-seed.json")).unwrap();
    let mut presentation = presentations.remove("peak").unwrap();
    presentation.media.reverse();
    let record = GameRecord {
        game_id: "peak".into(),
        title: "PEAK".into(),
        presentation: Some(presentation),
        ..Default::default()
    };
    let game = from_live(std::slice::from_ref(&record), &seed).remove(0);
    for (actual, expected) in game.media.iter().zip(original.media.iter().rev()) {
        assert_eq!(actual.fallback_path, expected.path);
        assert_eq!(actual.optimized_index, expected.optimized_index);
    }
    let mut record = record;
    let changed = &mut record.presentation.as_mut().unwrap().media[0];
    changed.url = "https://launcher.samoy.love/new-screenshot.jpg".into();
    changed.source_url.clear();
    let game = from_live(&[record], &seed).remove(0);
    assert!(game.media[0].fallback_path.is_empty());
    assert_eq!(game.media[0].optimized_index, None);
}

#[test]
fn live_icon_replaces_seed_artwork_and_preserves_offline_fallback() {
    use chillhub_native::model::ModsRecord;

    let seed = actual_catalog();
    let original = seed.iter().find(|game| game.id == "peak").unwrap();
    let record = GameRecord {
        game_id: original.id.clone(),
        title: original.title.clone(),
        icon_url: "https://launcher.samoy.love/content/peak/current-icon.png".into(),
        mods: Some(ModsRecord {
            has_latest: true,
            version: "pack.internal".into(),
            display_version: "Июльское обновление".into(),
            ..ModsRecord::default()
        }),
        ..GameRecord::default()
    };
    let game = from_live(std::slice::from_ref(&record), &seed).remove(0);
    assert_eq!(game.icon, record.icon_url);
    assert_eq!(game.fallback_icon, original.icon);
    assert_eq!(game.modpack_version, "Июльское обновление");
}

#[test]
fn missing_and_malformed_seed_catalogs_report_distinct_errors() {
    let temp = tempfile::tempdir().unwrap();
    let path = temp.path().join("catalog.json");
    let check_error = |expected: &str| {
        let error = load(&path).err().unwrap();
        assert!(error.contains(expected), "{error}");
        assert!(error.contains("catalog.json"), "{error}");
    };
    check_error("Не удалось прочитать каталог");
    std::fs::write(&path, "{").unwrap();
    check_error("Не удалось разобрать каталог");
}
