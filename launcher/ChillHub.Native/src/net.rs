use crate::{config::acceptable_api_url, model::LibraryResponse, paths::atomic_write};
use reqwest::blocking::Client;
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use std::{
    fmt, fs,
    io::Read,
    path::{Path, PathBuf},
    time::{Duration, Instant},
};
use url::Url;

const MAX_JSON: u64 = 16 * 1024 * 1024;

#[derive(Clone, Debug)]
pub struct ApiClient {
    pub client: Client,
    pub base: Url,
    diagnostic_dir: Option<PathBuf>,
}

#[derive(Debug)]
pub enum ApiError {
    Network(String),
    Http(u16, String),
    Invalid(String),
}

impl fmt::Display for ApiError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Network(_) => write!(f, "Не удалось связаться с сервером. Попробуйте ещё раз."),
            Self::Http(429, _) => write!(f, "Слишком много запросов. Попробуйте чуть позже."),
            Self::Http(500..=599, _) => write!(f, "На сервере неполадки. Попробуйте позже."),
            Self::Http(code, _) => write!(f, "Сервер не выполнил запрос (код {code})."),
            Self::Invalid(message) => write!(f, "{message}"),
        }
    }
}
impl std::error::Error for ApiError {}

#[derive(Clone, Debug, Default, Deserialize, Serialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Maintenance {
    pub enabled: bool,
    pub title: String,
    pub message: String,
    #[serde(default)]
    pub reason: String,
    #[serde(deserialize_with = "maintenance_blocks")]
    pub blocks: MaintenanceBlocks,
    pub server_time: String,
    pub ends_at: Option<String>,
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(default)]
pub struct MaintenanceBlocks {
    pub install: bool,
    pub update: bool,
    pub launch: bool,
}
impl Default for MaintenanceBlocks {
    fn default() -> Self {
        Self {
            install: true,
            update: true,
            launch: false,
        }
    }
}
fn maintenance_blocks<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> Result<MaintenanceBlocks, D::Error> {
    Ok(Option::<MaintenanceBlocks>::deserialize(deserializer)?.unwrap_or_default())
}
impl Maintenance {
    pub fn banner(&self) -> String {
        if !self.enabled {
            return String::new();
        }
        let mut text = self.reason.trim().to_string();
        if text.is_empty() {
            text = "На сервере идут технические работы.".into();
        } else if !text.ends_with(['.', '!', '?', '…']) {
            text.push('.');
        }
        if let Some(end) = self
            .ends_at
            .as_deref()
            .and_then(|v| chrono::DateTime::parse_from_rfc3339(v).ok())
        {
            let now = chrono::DateTime::parse_from_rfc3339(&self.server_time)
                .unwrap_or_else(|_| chrono::Utc::now().fixed_offset());
            if end <= now {
                text.push_str(" Работы затянулись, ждём сообщения от сервера.");
            } else {
                text.push_str(&format!(
                    " Ожидаемое окончание — {}.",
                    end.with_timezone(&chrono::Local).format("%d.%m в %H:%M")
                ));
            }
        }
        text
    }
}

#[derive(Clone, Debug, Deserialize, Serialize)]
struct CatalogCache {
    fetched_at: String,
    response: LibraryResponse,
}

#[derive(Clone, Debug)]
pub struct CatalogSnapshot {
    pub library: LibraryResponse,
    pub offline: bool,
    pub fetched_at: String,
    pub warning: Option<String>,
}

impl ApiClient {
    pub fn new(base: &str) -> Result<Self, ApiError> {
        if !acceptable_api_url(base) {
            return Err(ApiError::Invalid(
                "Адрес сервера должен использовать HTTPS.".into(),
            ));
        }
        let base = Url::parse(&format!("{}/", base.trim().trim_end_matches('/')))
            .map_err(|e| ApiError::Invalid(e.to_string()))?;
        let origin = base.origin();
        let client = Client::builder()
            .user_agent(concat!("ChillHub/", env!("CARGO_PKG_VERSION")))
            .connect_timeout(Duration::from_secs(8))
            .timeout(Duration::from_secs(25))
            .redirect(reqwest::redirect::Policy::custom(move |attempt| {
                if attempt.previous().len() >= 4 {
                    return attempt.error("Слишком много перенаправлений");
                }
                if attempt.url().origin() != origin
                    || !attempt.url().username().is_empty()
                    || attempt.url().password().is_some()
                {
                    return attempt.error("Сервер перенаправил запрос на другой адрес");
                }
                attempt.follow()
            }))
            .build()
            .map_err(|e| ApiError::Network(e.to_string()))?;
        Ok(Self {
            client,
            base,
            diagnostic_dir: None,
        })
    }

    pub fn with_diagnostics(mut self, directory: PathBuf) -> Self {
        self.diagnostic_dir = Some(directory);
        self
    }

    pub fn event(&self, event: &str, fields: serde_json::Value) {
        if let Some(directory) = &self.diagnostic_dir {
            crate::diagnostics::event(directory, event, fields);
        }
    }

    pub fn resolve(&self, value: &str) -> Result<Url, ApiError> {
        let result = self
            .base
            .join(value)
            .map_err(|e| ApiError::Invalid(e.to_string()))?;
        if result.origin() != self.base.origin()
            || !result.username().is_empty()
            || result.password().is_some()
            || result.fragment().is_some()
        {
            return Err(ApiError::Invalid(
                "Адрес файла не относится к выбранному серверу.".into(),
            ));
        }
        Ok(result)
    }

    pub fn json<T: DeserializeOwned>(&self, route: &str) -> Result<T, ApiError> {
        self.json_with_timeout(route, Duration::from_secs(25))
    }

    fn json_with_timeout<T: DeserializeOwned>(
        &self,
        route: &str,
        timeout: Duration,
    ) -> Result<T, ApiError> {
        let url = self.resolve(route)?;
        let started = Instant::now();
        let path = url.path().to_owned();
        let request_id = uuid::Uuid::new_v4().to_string();
        let mut status_code = None;
        let result = (|| {
            let response = self
                .client
                .get(url)
                .header("X-Request-ID", &request_id)
                .timeout(timeout)
                .send()
                .map_err(|e| ApiError::Network(format!("{:?}", e.without_url())))?;
            let status = response.status();
            status_code = Some(status.as_u16());
            let mut bytes = Vec::new();
            response
                .take(MAX_JSON + 1)
                .read_to_end(&mut bytes)
                .map_err(|e| ApiError::Network(e.to_string()))?;
            if bytes.len() as u64 > MAX_JSON {
                return Err(ApiError::Invalid("Ответ сервера слишком большой.".into()));
            }
            if !status.is_success() {
                return Err(ApiError::Http(
                    status.as_u16(),
                    String::from_utf8_lossy(&bytes).chars().take(512).collect(),
                ));
            }
            serde_json::from_slice(&bytes).map_err(|_| {
                ApiError::Invalid("Сервер вернул неполные или повреждённые данные.".into())
            })
        })();
        // Deliberately exclude query strings, response bodies and authentication.
        self.event(
            "http.complete",
            serde_json::json!({
                "requestId": request_id, "path": path, "status": status_code,
                "elapsedMs": started.elapsed().as_millis(), "ok": result.is_ok(),
                "error": result.as_ref().err().map(ToString::to_string),
                "cause": match result.as_ref().err() {
                    Some(ApiError::Network(cause)) => Some(cause.as_str()),
                    _ => None
                }
            }),
        );
        result
    }

    pub fn catalog(&self, cache_dir: &Path) -> Result<CatalogSnapshot, ApiError> {
        let cache_path = self.cache_path(cache_dir, "catalog");
        match self
            .json_with_timeout::<serde_json::Value>("api/games", Duration::from_secs(8))
            .and_then(validate_catalog)
        {
            Ok(mut response) => {
                // A local build can remain published while latest.json is absent.
                // Match the original GamePage's fallback without treating mods-only
                // entries as locally downloadable games. Keep the worker bounded.
                let deadline = Instant::now() + Duration::from_secs(4);
                let mut incomplete = false;
                for game in response
                    .items
                    .iter_mut()
                    .filter(|g| g.has_latest && g.latest_version.trim().is_empty())
                {
                    let remaining = deadline.saturating_duration_since(Instant::now());
                    let builds = if remaining.is_zero() {
                        None
                    } else {
                        self.builds_with_timeout(
                            &game.game_id,
                            remaining.min(Duration::from_secs(2)),
                        )
                        .ok()
                    };
                    if let Some(version) = builds.and_then(|items| items.into_iter().next()) {
                        let manifest = self.game_manifest_url(&game.game_id, &version)?;
                        game.manifest_url = manifest.path().to_owned();
                        game.latest_version = version;
                    } else {
                        incomplete = true;
                    }
                }
                let fetched_at = chrono::Utc::now().to_rfc3339();
                let cache = CatalogCache {
                    fetched_at: fetched_at.clone(),
                    response: response.clone(),
                };
                // Never replace a known offline catalog with unresolved build metadata.
                let warning = if incomplete {
                    Some("Для части игр не удалось получить список версий. Обновите каталог чуть позже.".into())
                } else {
                    serde_json::to_vec(&cache)
                        .ok()
                        .and_then(|data| atomic_write(&cache_path, &data).err())
                        .map(|_| {
                            "Каталог получен, но сохранить его для работы без сети не удалось."
                                .into()
                        })
                };
                Ok(CatalogSnapshot {
                    library: response,
                    offline: false,
                    fetched_at,
                    warning,
                })
            }
            Err(error) => self.cached_catalog(cache_dir).ok_or(error),
        }
    }
    /// A bounded, network-free snapshot for the first frame. It is explicitly offline
    /// until a fresh request succeeds: a saved catalog cannot establish server status.
    pub fn cached_catalog(&self, cache_dir: &Path) -> Option<CatalogSnapshot> {
        let file = fs::File::open(self.cache_path(cache_dir, "catalog")).ok()?;
        if file.metadata().ok()?.len() > MAX_JSON {
            return None;
        }
        let mut bytes = Vec::new();
        file.take(MAX_JSON + 1).read_to_end(&mut bytes).ok()?;
        if bytes.len() as u64 > MAX_JSON {
            return None;
        }
        let value: serde_json::Value = serde_json::from_slice(&bytes).ok()?;
        let fetched_at = value.get("fetched_at")?.as_str()?.to_owned();
        chrono::DateTime::parse_from_rfc3339(&fetched_at).ok()?;
        let library = validate_catalog(value.get("response")?.clone()).ok()?;
        Some(CatalogSnapshot {
            library,
            offline: true,
            fetched_at,
            warning: Some(
                "Показана сохранённая библиотека. Установленные игры доступны без сети.".into(),
            ),
        })
    }
    pub fn maintenance(&self) -> Result<Maintenance, ApiError> {
        self.json_with_timeout("api/maintenance", Duration::from_secs(5))
    }
    pub fn builds(&self, game_id: &str) -> Result<Vec<String>, ApiError> {
        self.builds_with_timeout(game_id, Duration::from_secs(25))
    }
    fn builds_with_timeout(
        &self,
        game_id: &str,
        timeout: Duration,
    ) -> Result<Vec<String>, ApiError> {
        if !safe_id(game_id) {
            return Err(ApiError::Invalid("Некорректный идентификатор игры.".into()));
        }
        #[derive(Deserialize)]
        struct Builds {
            items: Vec<String>,
        }
        let response =
            self.json_with_timeout::<Builds>(&format!("api/games/{game_id}/builds"), timeout)?;
        let mut items = response
            .items
            .into_iter()
            .map(|v| v.trim().to_owned())
            .filter(|v| crate::config::valid_version(v))
            .collect::<Vec<_>>();
        items.sort_by(|a, b| compare_build_versions(b, a));
        items.dedup_by(|a, b| a.eq_ignore_ascii_case(b));
        Ok(items)
    }
    pub fn cache_path(&self, dir: &Path, name: &str) -> PathBuf {
        let hash = blake3::hash(self.base.as_str().as_bytes()).to_hex();
        dir.join(format!("{name}-{}.json", &hash[..16]))
    }
    pub fn game_content_base(&self, game_id: &str, version: &str) -> Result<Url, ApiError> {
        if !safe_id(game_id) || version.trim().is_empty() {
            return Err(ApiError::Invalid("Не указана версия игры.".into()));
        }
        let mut base = self.base.clone();
        base.path_segments_mut()
            .map_err(|_| ApiError::Invalid("Некорректный адрес сервера.".into()))?
            .pop_if_empty()
            .extend(["content", game_id, version, "files", ""]);
        Ok(base)
    }

    pub fn game_manifest_url(&self, game_id: &str, version: &str) -> Result<Url, ApiError> {
        if !safe_id(game_id) || !crate::config::valid_version(version) {
            return Err(ApiError::Invalid("Некорректная версия игры.".into()));
        }
        let filename = format!("{version}.json");
        let mut manifest = self.base.clone();
        manifest
            .path_segments_mut()
            .map_err(|_| ApiError::Invalid("Некорректный адрес сервера.".into()))?
            .pop_if_empty()
            .extend(["manifests", game_id, filename.as_str()]);
        Ok(manifest)
    }
}

// Same segment ordering as C# VersionOrder: numeric segments sort above text,
// absent segments are zero, separators are '.', '-', '_' and '+'. This is game
// build ordering, deliberately separate from strict launcher update versions.
fn compare_build_versions(a: &str, b: &str) -> std::cmp::Ordering {
    let left = a.split(['.', '-', '_', '+']).collect::<Vec<_>>();
    let right = b.split(['.', '-', '_', '+']).collect::<Vec<_>>();
    fn number(value: &str) -> Option<i64> {
        if !value.is_empty() && value.bytes().all(|c| c.is_ascii_digit()) {
            value.parse().ok()
        } else {
            None
        }
    }
    for index in 0..left.len().max(right.len()) {
        let a = left.get(index).copied().unwrap_or("0");
        let b = right.get(index).copied().unwrap_or("0");
        let order = match (number(a), number(b)) {
            (Some(a), Some(b)) => a.cmp(&b),
            (Some(_), None) => std::cmp::Ordering::Greater,
            (None, Some(_)) => std::cmp::Ordering::Less,
            _ => a.to_ascii_lowercase().cmp(&b.to_ascii_lowercase()),
        };
        if !order.is_eq() {
            return order;
        }
    }
    std::cmp::Ordering::Equal
}

fn validate_catalog(value: serde_json::Value) -> Result<LibraryResponse, ApiError> {
    if !value.get("items").is_some_and(|items| items.is_array()) {
        return Err(ApiError::Invalid(
            "Сервер вернул неполный каталог игр.".into(),
        ));
    }
    let mut response: LibraryResponse = serde_json::from_value(value)
        .map_err(|_| ApiError::Invalid("Сервер вернул повреждённый каталог игр.".into()))?;
    let original_count = response.items.len();
    response
        .items
        .retain(|g| safe_id(&g.game_id) && !g.title.trim().is_empty());
    let mut seen = std::collections::HashSet::new();
    response
        .items
        .retain(|g| seen.insert(g.game_id.to_ascii_lowercase()));
    if original_count != 0 && response.items.is_empty() {
        return Err(ApiError::Invalid(
            "В каталоге сервера нет корректных записей игр.".into(),
        ));
    }
    Ok(response)
}

pub fn safe_id(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 64
        && !value.starts_with('_')
        && value
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || c == b'-' || c == b'_')
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn maintenance_defaults_and_server_clock_are_safe() {
        for raw in [
            r#"{"enabled":true}"#,
            r#"{"enabled":true,"blocks":null}"#,
            r#"{"enabled":true,"blocks":{}}"#,
        ] {
            let state: Maintenance = serde_json::from_str(raw).unwrap();
            assert!(state.blocks.install && state.blocks.update && !state.blocks.launch);
        }
        let m:Maintenance=serde_json::from_str(r#"{"enabled":true,"reason":"Меняем диск","serverTime":"2001-01-01T10:00:00Z","endsAt":"2001-01-01T11:00:00Z","blocks":{"install":false,"update":false}}"#).unwrap();
        assert!(m.banner().starts_with("Меняем диск. Ожидаемое окончание"));
        assert!(!m.blocks.install && !m.blocks.update);
        let expired = Maintenance {
            server_time: "2001-01-01T12:00:00Z".into(),
            ..m
        };
        assert!(expired.banner().contains("затянулись"));
        assert!(
            !ApiError::Http(503, String::new())
                .to_string()
                .contains("подключение")
        );
    }
    #[test]
    fn game_build_order_matches_legacy_numeric_segments() {
        use std::cmp::Ordering::*;
        assert_eq!(compare_build_versions("1.10.0", "1.9.9"), Greater);
        assert_eq!(compare_build_versions("1.2", "1.2.0"), Equal);
        assert_eq!(compare_build_versions("1.2", "1.2-rc"), Greater);
        assert_eq!(compare_build_versions("v1-RC", "v1-rc"), Equal);
        assert_eq!(compare_build_versions("2.0", "release"), Greater);
    }
    #[test]
    fn payload_urls_cannot_change_origin_or_include_credentials() {
        let client = ApiClient::new("https://launcher.samoy.love").unwrap();
        assert!(client.resolve("/manifests/peak/1.json").is_ok());
        for bad in [
            "https://other.example/file",
            "//other.example/file",
            "https://user@launcher.samoy.love/file",
            "file:///C:/temp",
        ] {
            assert!(client.resolve(bad).is_err(), "{bad}");
        }
        assert!(ApiClient::new("http://example.org").is_err());
        assert!(ApiClient::new("http://127.0.0.1:12345").is_ok());
    }
    #[test]
    fn catalogs_from_different_servers_never_share_cache() {
        let a = ApiClient::new("https://one.example").unwrap();
        let b = ApiClient::new("https://two.example").unwrap();
        assert_ne!(
            a.cache_path(Path::new("cache"), "catalog"),
            b.cache_path(Path::new("cache"), "catalog")
        );
    }
    #[test]
    fn invalid_catalog_ids_never_become_paths() {
        for bad in ["", "..", "../game", "C:game", "_registry", "game/a", "a\\b"] {
            assert!(!safe_id(bad));
        }
        assert!(safe_id("lethal-company"));
    }
}
