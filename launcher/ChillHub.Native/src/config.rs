use crate::paths::{AppPaths, atomic_write};
use serde::{Deserialize, Serialize};
use std::{collections::BTreeMap, fs, path::Path};
const MAX_CONFIG_BYTES: u64 = 1024 * 1024;

fn read_settings(path: &Path) -> std::io::Result<Vec<u8>> {
    use std::io::Read;
    for attempt in 0..4 {
        let result = (|| {
            let file = fs::File::open(path)?;
            if file.metadata()?.len() > MAX_CONFIG_BYTES {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::InvalidData,
                    "Файл настроек слишком большой",
                ));
            }
            let mut bytes = Vec::new();
            file.take(MAX_CONFIG_BYTES + 1).read_to_end(&mut bytes)?;
            if bytes.len() as u64 > MAX_CONFIG_BYTES {
                return Err(std::io::Error::new(
                    std::io::ErrorKind::InvalidData,
                    "Файл настроек слишком большой",
                ));
            }
            Ok(bytes)
        })();
        match result {
            Err(ref error)
                if attempt < 3
                    && (error.kind() == std::io::ErrorKind::PermissionDenied
                        || matches!(error.raw_os_error(), Some(32 | 33))) =>
            {
                std::thread::sleep(std::time::Duration::from_millis(100 << attempt));
            }
            _ => return result,
        }
    }
    unreachable!()
}

pub const DEFAULT_API_BASE_URL: &str = "https://launcher.samoy.love";

#[derive(Clone, Copy, Debug, Eq, PartialEq, Hash, Deserialize, Serialize)]
pub enum LaunchTarget {
    SteamModded,
    SteamVanilla,
    LocalModded,
    LocalVanilla,
}

impl LaunchTarget {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::SteamModded => "SteamModded",
            Self::SteamVanilla => "SteamVanilla",
            Self::LocalModded => "LocalModded",
            Self::LocalVanilla => "LocalVanilla",
        }
    }
    pub fn parse(value: &str) -> Option<Self> {
        [
            Self::SteamModded,
            Self::SteamVanilla,
            Self::LocalModded,
            Self::LocalVanilla,
        ]
        .into_iter()
        .find(|t| t.as_str().eq_ignore_ascii_case(value))
    }
    pub fn via_steam(self) -> bool {
        matches!(self, Self::SteamModded | Self::SteamVanilla)
    }
    pub fn modded(self) -> bool {
        matches!(self, Self::SteamModded | Self::LocalModded)
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
pub struct AppConfig {
    pub games_path: String,
    pub download_threads: i32,
    pub speed_limit_mbps: i32,
    pub api_base_url: String,
    pub last_game_id: String,
    pub launch_targets: BTreeMap<String, String>,
    pub selected_versions: BTreeMap<String, String>,
    pub minimize_to_tray: bool,
    pub tray_hint_shown: bool,
    pub last_seen_changelog_version: String,
    pub window_width: f64,
    pub window_height: f64,
    pub window_maximized: bool,
    #[serde(flatten)]
    pub extra: BTreeMap<String, serde_json::Value>,
}

impl Default for AppConfig {
    fn default() -> Self {
        Self {
            games_path: crate::paths::default_games_dir()
                .to_string_lossy()
                .into_owned(),
            download_threads: 8,
            speed_limit_mbps: 0,
            api_base_url: DEFAULT_API_BASE_URL.into(),
            last_game_id: String::new(),
            launch_targets: BTreeMap::new(),
            selected_versions: BTreeMap::new(),
            minimize_to_tray: true,
            tray_hint_shown: false,
            last_seen_changelog_version: String::new(),
            window_width: 0.,
            window_height: 0.,
            window_maximized: false,
            extra: BTreeMap::new(),
        }
    }
}

impl AppConfig {
    pub fn normalize(&mut self) {
        self.download_threads = self.download_threads.clamp(2, 16);
        self.speed_limit_mbps = self.speed_limit_mbps.clamp(0, 1000);
        if self.games_path.trim().is_empty() {
            self.games_path = Self::default().games_path;
        }
        if !acceptable_api_url(&self.api_base_url) {
            self.api_base_url = DEFAULT_API_BASE_URL.into();
        }
        if !self.window_width.is_finite() || self.window_width < 0. {
            self.window_width = 0.;
        }
        if !self.window_height.is_finite() || self.window_height < 0. {
            self.window_height = 0.;
        }
        self.selected_versions.retain(|game, version| {
            crate::paths::validate_game_id(game).is_ok() && valid_version(version)
        });
    }
    pub fn remembered(&self, game_id: &str) -> Option<LaunchTarget> {
        self.launch_targets
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(game_id))
            .and_then(|(_, v)| LaunchTarget::parse(v))
    }
    pub fn remember(&mut self, game_id: &str, target: LaunchTarget) {
        self.launch_targets
            .retain(|k, _| !k.eq_ignore_ascii_case(game_id));
        self.launch_targets
            .insert(game_id.to_owned(), target.as_str().into());
    }
    pub fn selected_version(&self, game_id: &str) -> Option<&str> {
        self.selected_versions
            .iter()
            .find(|(id, _)| id.eq_ignore_ascii_case(game_id))
            .map(|(_, version)| version.as_str())
    }
    pub fn select_version(&mut self, game_id: &str, selected: &str, latest: &str) {
        self.selected_versions
            .retain(|id, _| !id.eq_ignore_ascii_case(game_id));
        if selected != latest && valid_version(selected) {
            self.selected_versions
                .insert(game_id.to_owned(), selected.to_owned());
        }
    }
    pub fn apply_paths(&self, paths: &AppPaths) -> AppPaths {
        let mut result = paths.clone();
        result.games_dir = std::env::var_os("CHILLHUB_GAMES_DIR")
            .map(Into::into)
            .unwrap_or_else(|| self.games_path.clone().into());
        result
    }
}

pub fn valid_version(version: &str) -> bool {
    !version.is_empty()
        && version.len() <= 160
        && !version.contains(['/', '\\', ':'])
        && crate::paths::relative_path(version).is_ok()
}

pub fn acceptable_api_url(raw: &str) -> bool {
    let Ok(uri) = url::Url::parse(raw.trim()) else {
        return false;
    };
    if uri.host_str().is_none() || !uri.username().is_empty() || uri.password().is_some() {
        return false;
    }
    uri.scheme() == "https"
        || (uri.scheme() == "http"
            && match uri.host() {
                Some(url::Host::Domain(d)) => d.eq_ignore_ascii_case("localhost"),
                Some(url::Host::Ipv4(ip)) => ip.is_loopback(),
                Some(url::Host::Ipv6(ip)) => ip.is_loopback(),
                _ => false,
            })
}

#[derive(Clone, Debug)]
pub struct ConfigStore {
    pub paths: AppPaths,
}

impl ConfigStore {
    pub fn new(paths: AppPaths) -> Self {
        Self { paths }
    }
    /// Planning must not create, migrate or repair settings as a side effect.
    pub fn load_readonly(&self) -> Result<AppConfig, String> {
        use std::io::Read;
        let file = match fs::File::open(self.paths.config_path()) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                return Ok(AppConfig {
                    games_path: self.paths.games_dir.to_string_lossy().into_owned(),
                    ..Default::default()
                });
            }
            Err(error) => return Err(format!("Не удалось прочитать настройки: {error}")),
        };
        const LIMIT: u64 = 1024 * 1024;
        if file.metadata().map_err(|e| e.to_string())?.len() > LIMIT {
            return Err("Файл настроек слишком большой".into());
        }
        let mut bytes = Vec::new();
        file.take(LIMIT + 1)
            .read_to_end(&mut bytes)
            .map_err(|e| e.to_string())?;
        if bytes.len() as u64 > LIMIT {
            return Err("Файл настроек слишком большой".into());
        }
        let mut config =
            deserialize_config(&bytes).map_err(|_| "Файл настроек повреждён".to_string())?;
        config.normalize();
        Ok(config)
    }
    pub fn load(&self) -> Result<AppConfig, String> {
        let path = self.paths.config_path();
        let defaults = || AppConfig {
            games_path: self.paths.games_dir.to_string_lossy().into_owned(),
            ..Default::default()
        };
        if !path.exists() {
            if let Some(legacy) = &self.paths.legacy_dir {
                let old = legacy.join("config.json");
                if old.is_file() {
                    let bytes = read_settings(&old)
                        .map_err(|e| format!("Не удалось прочитать старые настройки: {e}"))?;
                    if let Ok(mut migrated) = deserialize_config(&bytes) {
                        migrated.normalize();
                        self.save(&migrated)?;
                        return Ok(migrated);
                    }
                }
            }
            let config = defaults();
            self.save(&config)?;
            return Ok(config);
        }
        let bytes = read_settings(&path)
            .map_err(|e| format!("Настройки недоступны, файл оставлен без изменений: {e}"))?;
        match deserialize_config(&bytes) {
            Ok(mut cfg) => {
                cfg.normalize();
                Ok(cfg)
            }
            Err(_) => {
                atomic_write(&self.paths.data_dir.join("config.corrupted.json"), &bytes)
                    .map_err(|e| e.to_string())?;
                let cfg = defaults();
                self.save(&cfg)?;
                Ok(cfg)
            }
        }
    }
    pub fn save(&self, config: &AppConfig) -> Result<(), String> {
        let mut normalized = config.clone();
        normalized.normalize();
        let bytes = serde_json::to_vec_pretty(&normalized).map_err(|e| e.to_string())?;
        if bytes.len() as u64 > MAX_CONFIG_BYTES {
            return Err("Файл настроек слишком большой".into());
        }
        atomic_write(&self.paths.config_path(), &bytes)
            .map_err(|e| format!("Не удалось сохранить настройки: {e}"))
    }
    pub fn update(&self, edit: impl FnOnce(&mut AppConfig)) -> Result<AppConfig, String> {
        fs::create_dir_all(&self.paths.data_dir).map_err(|e| e.to_string())?;
        let lock = fs::OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(self.paths.data_dir.join("config.lock"))
            .map_err(|e| e.to_string())?;
        fs2::FileExt::lock_exclusive(&lock).map_err(|e| e.to_string())?;
        let mut cfg = self.load()?;
        edit(&mut cfg);
        self.save(&cfg)?;
        Ok(cfg)
    }
}

fn deserialize_config(bytes: &[u8]) -> Result<AppConfig, serde_json::Error> {
    let mut value: serde_json::Value =
        serde_json::from_slice(bytes.strip_prefix(&[0xef, 0xbb, 0xbf]).unwrap_or(bytes))?;
    if let Some(object) = value.as_object_mut() {
        for key in [
            "GamesPath",
            "ApiBaseUrl",
            "LastGameId",
            "LaunchTargets",
            "SelectedVersions",
            "LastSeenChangelogVersion",
        ] {
            if object.get(key).is_some_and(serde_json::Value::is_null) {
                object.remove(key);
            }
        }
    }
    serde_json::from_value(value)
}

pub fn games_path_is_usable(path: &Path) -> Result<(), String> {
    fs::create_dir_all(path).map_err(|e| e.to_string())?;
    let probe = path.join(format!(".write-test-{}", uuid::Uuid::new_v4()));
    let file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&probe)
        .map_err(|e| e.to_string())?;
    drop(file);
    fs::remove_file(probe).map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn migrates_pascal_case_without_deleting_old_or_unknown_fields() {
        let tmp = tempfile::tempdir().unwrap();
        let mut paths = AppPaths::isolated(tmp.path());
        let legacy = tmp.path().join("legacy");
        fs::create_dir_all(&legacy).unwrap();
        fs::write(legacy.join("config.json"), r#"{"GamesPath":"D:\\Games\\custom","DownloadThreads":99,"LaunchTargets":{"PEAK":"SteamVanilla"},"FutureField":42}"#).unwrap();
        paths.legacy_dir = Some(legacy.clone());
        let store = ConfigStore::new(paths);
        let mut cfg = store.load().unwrap();
        assert_eq!(cfg.download_threads, 16);
        assert_eq!(cfg.remembered("peak"), Some(LaunchTarget::SteamVanilla));
        cfg.games_path = "different".into();
        store.save(&cfg).unwrap();
        assert_eq!(store.load().unwrap().games_path, "different");
        assert_eq!(store.load().unwrap().extra["FutureField"], 42);
        assert!(legacy.join("config.json").exists());
    }
    #[test]
    fn corrupt_backup_is_exact_and_roundtrip_preserves_all_settings() {
        let tmp = tempfile::tempdir().unwrap();
        let store = ConfigStore::new(AppPaths::isolated(tmp.path()));
        fs::create_dir_all(&store.paths.data_dir).unwrap();
        fs::write(store.paths.config_path(), b"broken").unwrap();
        let mut cfg = store.load().unwrap();
        assert_eq!(
            fs::read(store.paths.data_dir.join("config.corrupted.json")).unwrap(),
            b"broken"
        );
        cfg.remember("peak", LaunchTarget::LocalVanilla);
        cfg.window_width = 1360.;
        cfg.minimize_to_tray = false;
        store.save(&cfg).unwrap();
        let back = store.load().unwrap();
        assert_eq!(back.window_width, 1360.);
        assert!(!back.minimize_to_tray);
        assert_eq!(back.remembered("PEAK"), Some(LaunchTarget::LocalVanilla));
    }
    #[test]
    fn api_endpoint_rejects_insecure_remote() {
        assert!(acceptable_api_url("https://launcher.samoy.love"));
        assert!(acceptable_api_url("http://127.0.0.1:55700"));
        for s in [
            "http://example.com",
            "file:///tmp",
            "https://user:secret@example.com",
            "javascript:x",
        ] {
            assert!(!acceptable_api_url(s));
        }
    }
    #[test]
    fn oversized_settings_are_preserved_instead_of_reset() {
        let tmp = tempfile::tempdir().unwrap();
        let store = ConfigStore::new(AppPaths::isolated(tmp.path()));
        fs::create_dir_all(&store.paths.data_dir).unwrap();
        let bytes = vec![b' '; MAX_CONFIG_BYTES as usize + 1];
        fs::write(store.paths.config_path(), &bytes).unwrap();
        assert!(store.load().is_err());
        assert_eq!(fs::read(store.paths.config_path()).unwrap(), bytes);
        assert!(!store.paths.data_dir.join("config.corrupted.json").exists());
    }
}
