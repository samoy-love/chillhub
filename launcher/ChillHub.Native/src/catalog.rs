use chillhub_native::model::GameRecord;
use serde::Deserialize;
use std::path::Path;

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Game {
    pub id: String,
    pub title: String,
    pub description: String,
    pub genre: String,
    pub icon: String,
    #[serde(skip)]
    pub fallback_icon: String,
    pub hero: String,
    #[serde(skip)]
    pub fallback_hero: String,
    pub game_version: String,
    pub steam_app_id: String,
    pub steam_supported: bool,
    pub local_supported: bool,
    pub has_mods: bool,
    pub modpack_name: String,
    pub modpack_version: String,
    pub modpack_revision: String,
    pub metadata_note: String,
    #[serde(default)]
    pub modpack_description: String,
    pub highlights: Vec<String>,
    #[serde(default)]
    pub mod_benefits: Vec<chillhub_native::model::ModBenefit>,
    pub media: Vec<Media>,
    pub mods: Vec<ModEntry>,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct Media {
    pub kind: String,
    pub path: String,
    #[serde(skip)]
    pub fallback_path: String,
    #[serde(skip)]
    pub optimized_index: Option<usize>,
    pub caption: String,
    pub url: String,
    pub source: String,
    #[serde(default)]
    pub provider: String,
}

#[derive(Clone, Debug, Default, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub struct ModEntry {
    pub name: String,
    pub russian_name: String,
    pub description: String,
    pub category: String,
    pub version: String,
}

pub fn load(path: &Path) -> Result<Vec<Game>, String> {
    let data = std::fs::read(path)
        .map_err(|error| format!("Не удалось прочитать каталог {}: {error}", path.display()))?;
    let mut games: Vec<Game> = serde_json::from_slice(&data)
        .map_err(|error| format!("Не удалось разобрать каталог {}: {error}", path.display()))?;
    for game in &mut games {
        for (index, media) in game.media.iter_mut().enumerate() {
            if media.path.starts_with("Assets/") {
                media.optimized_index = Some(index);
            }
        }
    }
    // Stable sorting keeps the source order of the remaining library entries.
    games.sort_by_key(|game| match game.id.as_str() {
        "peak" => 0,
        "lethal-company" => 1,
        _ => 2,
    });
    Ok(games)
}

pub fn from_live(records: &[GameRecord], seed: &[Game]) -> Vec<Game> {
    let curated: std::collections::HashMap<String, chillhub_native::model::Presentation> =
        serde_json::from_str(include_str!("../Data/presentation-seed.json")).unwrap_or_default();
    records
        .iter()
        .map(|r| {
            let mut g = seed
                .iter()
                .find(|g| g.id == r.game_id)
                .cloned()
                .unwrap_or_default();
            g.id = r.game_id.clone();
            g.title = r.title.clone();
            g.game_version = r.latest_version.clone();
            g.local_supported = r.has_latest && !r.latest_version.is_empty();
            g.has_mods = r.mods.as_ref().is_some_and(|m| m.has_latest);
            g.steam_supported = r.mods.as_ref().is_some_and(|m| !m.steam_app_id.is_empty());
            if !r.icon_url.is_empty() {
                g.fallback_icon = g.icon.clone();
                g.icon = r.icon_url.clone();
            }
            if let Some(m) = &r.mods {
                g.steam_app_id = m.steam_app_id.clone();
                g.modpack_name = m.display_name.clone();
                g.modpack_version = if m.display_version.is_empty() {
                    m.version.clone()
                } else {
                    m.display_version.clone()
                };
                g.modpack_revision = m.revision.clone();
            }
            if let Some(p) = r.presentation.as_ref().or_else(|| curated.get(&r.game_id)) {
                g.description = p.description.clone();
                g.genre = p.genre.clone();
                g.fallback_hero = g.hero.clone();
                g.hero = p.hero_url.clone();
                g.highlights = p.highlights.clone();
                g.mod_benefits = p.mod_benefits.clone();
                g.media = p
                    .media
                    .iter()
                    .map(|m| {
                        let fallback = g.media.iter().find(|seed| {
                            seed.kind == m.kind
                                && (seed.url == m.url
                                    || (!m.source_url.is_empty() && seed.source == m.source_url))
                        });
                        Media {
                            kind: m.kind.clone(),
                            path: if m.kind == "video" {
                                m.poster_url.clone()
                            } else {
                                m.url.clone()
                            },
                            url: m.url.clone(),
                            fallback_path: fallback
                                .map(|seed| seed.path.clone())
                                .unwrap_or_default(),
                            optimized_index: fallback.and_then(|seed| seed.optimized_index),
                            caption: m.caption.clone(),
                            source: m.source_url.clone(),
                            provider: m.provider.clone(),
                        }
                    })
                    .collect();
                g.mods = p
                    .mods
                    .iter()
                    .map(|m| ModEntry {
                        name: m.name.clone(),
                        russian_name: m.russian_name.clone(),
                        description: m.description.clone(),
                        category: m.category.clone(),
                        version: m.version.clone(),
                    })
                    .collect();
                g.modpack_description = p.modpack_description.clone();
                g.metadata_note = p.modpack_description.clone();
                if r.mods.as_ref().is_some_and(|m| {
                    p.modpack_version != m.version
                        || !p.modpack_revision.eq_ignore_ascii_case(&m.revision)
                }) {
                    g.mods.clear();
                    g.mod_benefits.clear();
                    g.modpack_description =
                        "Состав для этой версии ещё не подготовлен. Установка и запуск доступны."
                            .into();
                    g.metadata_note = g.modpack_description.clone();
                }
            } else if g.has_mods {
                g.metadata_note =
                    "Состав опубликованной сборки уточняется. Версия модпака указана по серверу."
                        .into();
            }
            if g.description.is_empty() {
                g.description = "Описание игры пока не добавлено.".into();
            }
            g
        })
        .collect()
}

pub fn filtered_indices(games: &[Game], query: &str, only_mods: bool) -> Vec<usize> {
    let query = query.trim().to_lowercase();
    games
        .iter()
        .enumerate()
        .filter(|(_, game)| !only_mods || game.has_mods)
        .filter(|(_, game)| {
            [
                game.title.as_str(),
                game.genre.as_str(),
                game.modpack_name.as_str(),
            ]
            .iter()
            .any(|value| value.to_lowercase().contains(&query))
        })
        .map(|(index, _)| index)
        .collect()
}

pub fn default_choice(game: &Game) -> (String, String) {
    let source = if game.steam_supported {
        "steam"
    } else if game.local_supported {
        "local"
    } else {
        return (String::new(), String::new());
    };
    let mode = if game.has_mods { "mods" } else { "plain" };
    (source.into(), mode.into())
}

pub fn valid_choice(game: &Game, source: &str, mode: &str) -> (String, String) {
    let (default_source, default_mode) = default_choice(game);
    if default_source.is_empty() {
        return (default_source, default_mode);
    }
    let source = match source.trim().to_ascii_lowercase().as_str() {
        "steam" if game.steam_supported => "steam".into(),
        "local" if game.local_supported => "local".into(),
        _ => default_source,
    };
    let mode = match mode.trim().to_ascii_lowercase().as_str() {
        "plain" => "plain".into(),
        "mods" if game.has_mods => "mods".into(),
        _ => default_mode,
    };
    (source, mode)
}

#[cfg(test)]
mod tests {
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
    fn library_search_matches_title_genre_and_modpack() {
        let games = actual_catalog();
        assert_eq!(filtered_indices(&games, "  lEtHaL  ", false), [1]);
        assert_eq!(filtered_indices(&games, "PEAKFRIENDS", true), [0]);
        assert!(filtered_indices(&games, "ВОСХОЖДЕНИЕ", false).contains(&0));
        assert_eq!(filtered_indices(&games, "", true), [0, 1, 5, 6]);
        assert!(filtered_indices(&games, "bombanana", true).is_empty());
        assert!(filtered_indices(&games, "missing game", false).is_empty());
    }

    #[test]
    fn all_four_launch_choices_survive_when_available() {
        let game = &actual_catalog()[0];
        assert_eq!(default_choice(game), ("steam".into(), "mods".into()));
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
            ("steam".into(), "mods".into())
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
        let mut presentations: std::collections::HashMap<
            String,
            chillhub_native::model::Presentation,
        > = serde_json::from_str(include_str!("../Data/presentation-seed.json")).unwrap();
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
}
