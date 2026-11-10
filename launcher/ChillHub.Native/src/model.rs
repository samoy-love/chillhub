use serde::{Deserialize, Serialize};

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct LibraryResponse {
    pub items: Vec<GameRecord>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct GameRecord {
    pub game_id: String,
    pub title: String,
    pub has_latest: bool,
    pub latest_version: String,
    pub manifest_url: String,
    pub exe_relative_path: String,
    pub icon_url: String,
    pub mods: Option<ModsRecord>,
    pub presentation: Option<Presentation>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ModsRecord {
    pub has_latest: bool,
    pub version: String,
    pub revision: String,
    pub display_name: String,
    pub display_version: String,
    pub manifest_url: String,
    pub content_base_url: String,
    pub community: String,
    pub loader: String,
    pub steam_app_id: String,
    pub steam_folder: String,
    pub exe_names: Vec<String>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Presentation {
    pub description: String,
    pub modpack_description: String,
    pub modpack_version: String,
    pub modpack_revision: String,
    pub genre: String,
    pub hero_url: String,
    pub highlights: Vec<String>,
    pub mod_benefits: Vec<ModBenefit>,
    pub media: Vec<MediaRecord>,
    pub mods: Vec<ModDescription>,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct MediaRecord {
    pub kind: String,
    pub url: String,
    pub poster_url: String,
    pub caption: String,
    pub provider: String,
    pub role: String,
    pub source_url: String,
    pub author: String,
    pub published_at: String,
    pub width: u32,
    pub height: u32,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ModBenefit {
    pub title: String,
    pub description: String,
    pub icon: String,
}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct ModDescription {
    pub id: String,
    pub name: String,
    pub russian_name: String,
    pub description: String,
    pub category: String,
    pub version: String,
    pub url: String,
}
