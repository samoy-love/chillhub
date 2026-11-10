use crate::{net::ApiClient, paths::atomic_write};
use std::{
    fs,
    io::Read,
    path::{Path, PathBuf},
    sync::{Mutex, OnceLock},
    time::{Duration, Instant},
};

const MAX_IMAGE_BYTES: u64 = 16 * 1024 * 1024;
const CACHE_TTL: Duration = Duration::from_secs(24 * 60 * 60);
static IMAGE_CLIENT: OnceLock<Result<reqwest::blocking::Client, String>> = OnceLock::new();
// Fixed-size striping coalesces concurrent requests without retaining one lock
// per URL forever. Different URLs normally proceed independently.
static IMAGE_LOCKS: [Mutex<()>; 32] = [const { Mutex::new(()) }; 32];

#[derive(Debug, Default, serde::Deserialize, serde::Serialize)]
struct Validators {
    etag: Option<String>,
    modified: Option<String>,
    #[serde(default)]
    payload_hash: String,
}

fn validators(path: &Path) -> Result<Validators, std::io::Error> {
    let invalid = |message| std::io::Error::new(std::io::ErrorKind::InvalidData, message);
    let file = fs::File::open(path.with_extension("meta"))?;
    if file.metadata()?.len() > 8192 {
        return Err(invalid("Image metadata exceeds the size limit"));
    }
    let mut bytes = Vec::new();
    file.take(8193).read_to_end(&mut bytes)?;
    if bytes.len() > 8192 {
        return Err(invalid("Image metadata exceeds the size limit"));
    }
    let metadata: Validators = serde_json::from_slice(&bytes)
        .map_err(|error| std::io::Error::new(std::io::ErrorKind::InvalidData, error))?;
    // Bind validators to the actual cached bytes. A failed sidecar write
    // after replacing the image must not validate unrelated older content.
    let file = fs::File::open(path)?;
    if file.metadata()?.len() > MAX_IMAGE_BYTES {
        return Err(invalid("Cached image exceeds the size limit"));
    }
    bytes.clear();
    file.take(MAX_IMAGE_BYTES + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 > MAX_IMAGE_BYTES
        || metadata.payload_hash != blake3::hash(&bytes).to_hex().as_str()
    {
        return Err(invalid("Image metadata does not match the cached payload"));
    }
    Ok(metadata)
}

fn validators_with_retry(
    path: &Path,
    mut on_retry: impl FnMut(&std::io::Error, usize),
) -> std::io::Result<Validators> {
    let mut attempt = 1;
    loop {
        match validators(path) {
            // Windows scanners can briefly deny sharing/byte-range access.
            // Retry only these concrete transient errors, on the image worker.
            // Missing/access-denied/invalid JSON/hash mismatches keep their
            // original behavior; total additional waiting is at most 75ms.
            Err(error)
                if cfg!(windows)
                    && attempt < 4
                    && matches!(error.raw_os_error(), Some(32 | 33)) =>
            {
                on_retry(&error, attempt);
                std::thread::sleep(Duration::from_millis(25));
                attempt += 1;
            }
            result => return result,
        }
    }
}

fn client() -> Result<&'static reqwest::blocking::Client, String> {
    IMAGE_CLIENT
        .get_or_init(|| {
            reqwest::blocking::Client::builder()
                .user_agent(concat!("ChillHub/", env!("CARGO_PKG_VERSION")))
                .connect_timeout(Duration::from_secs(4))
                .timeout(Duration::from_secs(10))
                .redirect(reqwest::redirect::Policy::none())
                .build()
                .map_err(|e| e.to_string())
        })
        .as_ref()
        .map_err(Clone::clone)
}

fn validate_image(bytes: &[u8]) -> Result<(), String> {
    if bytes.len() as u64 > MAX_IMAGE_BYTES {
        return Err("Изображение слишком большое".into());
    }
    let (width, height) = image::ImageReader::new(std::io::Cursor::new(bytes))
        .with_guessed_format()
        .map_err(|e| e.to_string())?
        .into_dimensions()
        .map_err(|e| e.to_string())?;
    if width == 0 || height == 0 || width as u64 * height as u64 > 40_000_000 {
        return Err("Неподдерживаемый размер изображения".into());
    }
    Ok(())
}

fn cached_image_result(path: &Path) -> std::io::Result<()> {
    fs::File::open(path).and_then(|file| {
        if file.metadata()?.len() > MAX_IMAGE_BYTES {
            return Err(std::io::Error::other("size"));
        }
        let mut bytes = Vec::new();
        file.take(MAX_IMAGE_BYTES + 1).read_to_end(&mut bytes)?;
        validate_image(&bytes).map_err(std::io::Error::other)
    })
}

#[cfg(test)]
fn cached_image(path: &Path) -> bool {
    cached_image_result(path).is_ok()
}

/// Only catalog-authored bundled paths and public HTTPS media are accepted.
pub fn location(value: &str, bundle: &Path, api: &ApiClient) -> Result<String, String> {
    if value.is_empty() {
        return Err("Изображение не задано".into());
    }
    if value.starts_with("Assets/") || value.starts_with("art/") {
        let relative = crate::paths::relative_path(value)?;
        return Ok(crate::paths::contained_path(bundle, &relative)?
            .to_string_lossy()
            .into_owned());
    }
    let url = api
        .base
        .join("/")
        .map_err(|_| "Некорректный адрес сервера")?
        .join(value)
        .map_err(|_| "Некорректный адрес медиа")?;
    if (url.scheme() != "https" && !(url.origin() == api.base.origin() && url.scheme() == "http"))
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
    {
        return Err("Неподдерживаемый адрес медиа".into());
    }
    Ok(url.to_string())
}
pub fn image_path(
    value: &str,
    bundle: &Path,
    cache: &Path,
    api: &ApiClient,
) -> Result<PathBuf, String> {
    image_path_with_read_observer(value, bundle, cache, api, |_, _| {})
}

// The observer lets a TEMP test release a real competing Windows handle after
// the first failed read, without sleeps or process-wide hooks in the fixture.
fn image_path_with_read_observer(
    value: &str,
    bundle: &Path,
    cache: &Path,
    api: &ApiClient,
    mut read_error: impl FnMut(&std::io::Error, usize),
) -> Result<PathBuf, String> {
    let address = location(value, bundle, api)?;
    if !address.starts_with("https://") && !address.starts_with("http://") {
        return Ok(address.into());
    }
    let hash = blake3::hash(address.as_bytes()).to_hex().to_string();
    let stripe = usize::from(blake3::hash(address.as_bytes()).as_bytes()[0]) % IMAGE_LOCKS.len();
    let _request = IMAGE_LOCKS[stripe]
        .lock()
        .map_err(|_| "Не удалось загрузить изображение")?;
    let output = cache.join(format!("{hash}.img"));
    let cached_result = cached_image_result(&output);
    if let Err(error) = &cached_result
        && error.kind() != std::io::ErrorKind::NotFound
    {
        api.event(
            "image.cache_read_error",
            serde_json::json!({"key": hash,
            "error": error.to_string(), "osCode": error.raw_os_error()}),
        );
    }
    let cached = cached_result.is_ok();
    if cached
        && fs::metadata(&output)
            .and_then(|m| m.modified())
            .ok()
            .and_then(|t| t.elapsed().ok())
            .is_some_and(|age| age < CACHE_TTL)
    {
        return Ok(output);
    }
    if output.exists() && !cached {
        let _ = fs::remove_file(&output);
        api.event("image.cache_invalid", serde_json::json!({"key": hash}));
    }
    let started = Instant::now();
    let path = url::Url::parse(&address).ok().map(|u| u.path().to_owned());
    let mut last_error = "Не удалось получить изображение".to_string();
    let validators = if cached {
        validators_with_retry(&output, |error, attempt| {
            api.event(
                "image.cache_metadata_read_retry",
                serde_json::json!({"path": path, "attempt": attempt,
                    "error": error.to_string(), "osCode": error.raw_os_error()}),
            );
            read_error(error, attempt);
        })
        .unwrap_or_else(|error| {
            api.event(
                "image.cache_metadata_read_error",
                serde_json::json!({"path": path,
                "error": error.to_string(), "osCode": error.raw_os_error()}),
            );
            Validators::default()
        })
    } else {
        Validators::default()
    };
    for attempt in 1..=2 {
        let mut request = client()?.get(&address);
        if let Some(etag) = &validators.etag {
            request = request.header("If-None-Match", etag);
        }
        if let Some(modified) = &validators.modified {
            request = request.header("If-Modified-Since", modified);
        }
        #[cfg(test)]
        api.event(
            "image.request.fixture",
            serde_json::json!({"address": address, "cached": cached,
                "etag": validators.etag, "modified": validators.modified,
                "attempt": attempt, "headers": format!("{:?}",
                    request.try_clone().map(|r| r.build().map(|r| r.headers().clone())))}),
        );
        let response = request.send();
        let received_validators = response
            .as_ref()
            .ok()
            .map(|r| Validators {
                payload_hash: String::new(),
                etag: r
                    .headers()
                    .get("ETag")
                    .and_then(|h| h.to_str().ok())
                    .filter(|h| h.len() <= 2048)
                    .map(str::to_owned),
                modified: r
                    .headers()
                    .get("Last-Modified")
                    .and_then(|h| h.to_str().ok())
                    .filter(|h| h.len() <= 2048)
                    .map(str::to_owned),
            })
            .unwrap_or_default();
        let (bytes, retry) = match response {
            Ok(response) if response.status().as_u16() == 304 && cached => {
                // A checked response renews freshness without downloading the
                // full raster. Failure to touch only causes another validation.
                let _ = fs::OpenOptions::new()
                    .write(true)
                    .open(&output)
                    .and_then(|f| {
                        f.set_times(fs::FileTimes::new().set_modified(std::time::SystemTime::now()))
                    });
                api.event("image.not_modified", serde_json::json!({"path": path}));
                return Ok(output);
            }
            Ok(response) if response.status().is_success() => {
                if response
                    .content_length()
                    .is_some_and(|n| n > MAX_IMAGE_BYTES)
                {
                    (Err("Изображение слишком большое".into()), false)
                } else {
                    let mut bytes = Vec::new();
                    let result = response
                        .take(MAX_IMAGE_BYTES + 1)
                        .read_to_end(&mut bytes)
                        .map_err(|_| "Соединение прервалось при загрузке изображения".to_string())
                        .and_then(|_| validate_image(&bytes))
                        .map(|_| bytes);
                    (result, true)
                }
            }
            Ok(response) => (
                Err(format!(
                    "Изображение недоступно (код {})",
                    response.status().as_u16()
                )),
                response.status().is_server_error(),
            ),
            Err(error) => (
                Err("Не удалось связаться с сервером изображений".into()),
                error.is_timeout() || error.is_connect() || error.is_body(),
            ),
        };
        match bytes {
            Ok(bytes) => {
                if let Err(error) = atomic_write(&output, &bytes) {
                    api.event(
                        "image.cache_write_error",
                        serde_json::json!({"path": path,
                        "error": error.to_string(), "cachedFallback": cached}),
                    );
                    return if cached {
                        Ok(output)
                    } else {
                        Err(error.to_string())
                    };
                }
                let received_validators = Validators {
                    payload_hash: blake3::hash(&bytes).to_hex().to_string(),
                    ..received_validators
                };
                if let Err(error) = serde_json::to_vec(&received_validators)
                    .map_err(std::io::Error::other)
                    .and_then(|metadata| atomic_write(&output.with_extension("meta"), &metadata))
                {
                    api.event(
                        "image.cache_metadata_write_error",
                        serde_json::json!({"path": path,
                        "error": error.to_string(), "osCode": error.raw_os_error()}),
                    );
                }
                api.event(
                    "image.download",
                    serde_json::json!({"path": path, "bytes": bytes.len(),
                    "attempt": attempt, "elapsedMs": started.elapsed().as_millis()}),
                );
                return Ok(output);
            }
            Err(error) => {
                api.event("image.error", serde_json::json!({"path": path, "attempt": attempt,
                    "error": error, "cachedFallback": cached, "elapsedMs": started.elapsed().as_millis()}));
                last_error = error;
                if !retry {
                    break;
                }
            }
        }
    }
    if cached { Ok(output) } else { Err(last_error) }
}

/// Remove only this URL's cache entry after a pixel decoder rejects its contents.
pub fn invalidate_image(value: &str, bundle: &Path, cache: &Path, api: &ApiClient) {
    if let Ok(address) = location(value, bundle, api)
        && (address.starts_with("https://") || address.starts_with("http://"))
    {
        let hash = blake3::hash(address.as_bytes()).to_hex().to_string();
        let _ = fs::remove_file(cache.join(format!("{hash}.img")));
        let _ = fs::remove_file(cache.join(format!("{hash}.meta")));
    }
}

pub fn external_video_url(provider: &str, value: &str) -> Result<url::Url, String> {
    let url = url::Url::parse(value).map_err(|_| "Некорректная ссылка на видео")?;
    let host = url.host_str().unwrap_or_default();
    let allowed = match provider {
        "youtube" => matches!(host, "www.youtube.com" | "youtube.com" | "youtu.be"),
        "rutube" => matches!(host, "rutube.ru" | "www.rutube.ru"),
        _ => false,
    };
    if !allowed
        || url.scheme() != "https"
        || !url.username().is_empty()
        || url.password().is_some()
        || url.port().is_some()
    {
        return Err("Неподдерживаемая ссылка на видео".into());
    }
    Ok(url)
}
pub fn open_external_video(provider: &str, value: &str) -> Result<(), String> {
    let url = external_video_url(provider, value)?;
    #[cfg(windows)]
    {
        use windows_sys::Win32::UI::{Shell::ShellExecuteW, WindowsAndMessaging::SW_SHOWNORMAL};
        let target: Vec<_> = url.as_str().encode_utf16().chain(Some(0)).collect();
        let result = unsafe {
            ShellExecuteW(
                std::ptr::null_mut(),
                std::ptr::null(),
                target.as_ptr(),
                std::ptr::null(),
                std::ptr::null(),
                SW_SHOWNORMAL,
            )
        } as isize;
        if result <= 32 {
            Err("Не удалось открыть видео в браузере".into())
        } else {
            Ok(())
        }
    }
    #[cfg(not(windows))]
    {
        let _ = url;
        Err("Доступно только в Windows".into())
    }
}

pub fn trim_cache(cache: &Path) -> Result<(), String> {
    let mut entries = fs::read_dir(cache)
        .into_iter()
        .flatten()
        .filter_map(Result::ok)
        .filter_map(|e| {
            let m = e.metadata().ok()?;
            (m.is_file() && e.path().extension().is_some_and(|x| x == "img")).then_some((
                e.path(),
                m.len(),
                m.modified().ok()?,
            ))
        })
        .collect::<Vec<_>>();
    entries.sort_by_key(|(_, _, t)| *t);
    let mut size: u64 = entries.iter().map(|(_, n, _)| *n).sum();
    for (path, bytes, _) in entries {
        if size <= 160 * 1024 * 1024 {
            break;
        }
        if fs::remove_file(&path).is_ok() {
            let _ = fs::remove_file(path.with_extension("meta"));
            size = size.saturating_sub(bytes);
        }
    }
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    const FISH_ICON: &[u8] = include_bytes!("../tests/fixtures/how-to-fish-icon.png");
    // A fixture starts listening before its URL acquires a striped lock. Allow
    // another URL's bounded two-attempt HTTP operation (2 x 10s) to finish first.
    const SERVER_WAIT: Duration = Duration::from_secs(30);

    fn image_server(statuses: Vec<u16>) -> (ApiClient, std::thread::JoinHandle<()>) {
        let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let api = ApiClient::new(&format!("http://{}", server.server_addr())).unwrap();
        let worker = std::thread::spawn(move || {
            for status in statuses {
                let request = server
                    .recv_timeout(SERVER_WAIT)
                    .unwrap()
                    .expect("expected image request");
                request
                    .respond(tiny_http::Response::from_data(FISH_ICON).with_status_code(status))
                    .unwrap();
            }
        });
        (api, worker)
    }

    #[test]
    fn how_to_fish_production_icon_decodes_without_extension() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("cache.img");
        fs::write(&path, FISH_ICON).unwrap();
        assert!(cached_image(&path));
        let decoded = image::ImageReader::open(&path)
            .unwrap()
            .with_guessed_format()
            .unwrap()
            .decode()
            .unwrap();
        assert_eq!((decoded.width(), decoded.height()), (256, 256));
    }

    #[test]
    fn transient_image_failure_retries_and_cache_works_offline() {
        let temp = tempfile::tempdir().unwrap();
        let (api, server) = image_server(vec![503, 200]);
        let api = api.with_diagnostics(temp.path().join("profile"));
        let cache = temp.path().join("cache");
        let path = image_path("/fish.png", temp.path(), &cache, &api).unwrap();
        server.join().unwrap();
        assert_eq!(fs::read(&path).unwrap(), FISH_ICON);
        assert_eq!(
            image_path("/fish.png", temp.path(), &cache, &api).unwrap(),
            path
        );
        let logs = fs::read_to_string(temp.path().join("profile/logs/launcher.log")).unwrap();
        assert!(logs.contains("image.error") && logs.contains("image.download"));
    }

    #[test]
    fn corrupt_cached_image_is_replaced_and_missing_image_is_not_cached() {
        let temp = tempfile::tempdir().unwrap();
        let (api, server) = image_server(vec![200, 404]);
        let url = api.base.join("/fish.png").unwrap();
        let hash = blake3::hash(url.as_str().as_bytes()).to_hex();
        fs::write(temp.path().join(format!("{hash}.img")), b"broken download").unwrap();
        let path = image_path("/fish.png", temp.path(), temp.path(), &api).unwrap();
        assert_eq!(fs::read(path).unwrap(), FISH_ICON);
        assert!(image_path("/missing.png", temp.path(), temp.path(), &api).is_err());
        server.join().unwrap();
    }

    fn stale_image(api: &ApiClient, directory: &Path) -> PathBuf {
        let address = api.base.join("/fish.png").unwrap();
        let hash = blake3::hash(address.as_str().as_bytes()).to_hex();
        let path = directory.join(format!("{hash}.img"));
        fs::write(&path, FISH_ICON).unwrap();
        let old = std::time::SystemTime::now() - CACHE_TTL - Duration::from_secs(60);
        fs::OpenOptions::new()
            .write(true)
            .open(&path)
            .unwrap()
            .set_times(fs::FileTimes::new().set_modified(old))
            .unwrap();
        path
    }

    #[test]
    fn oversized_remote_image_preserves_valid_stale_cache() {
        let temp = tempfile::tempdir().unwrap();
        let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let api = ApiClient::new(&format!("http://{}", server.server_addr())).unwrap();
        let output = stale_image(&api, temp.path());
        let worker = std::thread::spawn(move || {
            let request = server.recv_timeout(SERVER_WAIT).unwrap().unwrap();
            let response = tiny_http::Response::new(
                tiny_http::StatusCode(200),
                vec![],
                std::io::Cursor::new(Vec::<u8>::new()),
                Some(MAX_IMAGE_BYTES as usize + 1),
                None,
            )
            // tiny_http defaults to chunked above 32KiB. This fixture must
            // advertise Content-Length so the client rejects it before the body.
            .with_chunked_threshold(usize::MAX);
            let _ = request.respond(response);
        });
        assert_eq!(
            image_path("/fish.png", temp.path(), temp.path(), &api).unwrap(),
            output
        );
        assert_eq!(fs::read(output).unwrap(), FISH_ICON);
        worker.join().unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn replacement_sharing_violation_preserves_readable_stale_cache() {
        use std::os::windows::fs::OpenOptionsExt;
        let temp = tempfile::tempdir().unwrap();
        let (api, server) = image_server(vec![200]);
        let output = stale_image(&api, temp.path());
        // A reader without FILE_SHARE_DELETE models a scanner holding the old
        // cache file. Reads succeed, but atomic replacement must fail safely.
        let held = fs::OpenOptions::new()
            .read(true)
            .share_mode(1)
            .open(&output)
            .unwrap();
        assert_eq!(
            image_path("/fish.png", temp.path(), temp.path(), &api).unwrap(),
            output
        );
        assert_eq!(fs::read(&output).unwrap(), FISH_ICON);
        drop(held);
        server.join().unwrap();
    }

    #[test]
    fn external_playback_only_opens_https_on_declared_video_provider() {
        assert!(external_video_url("youtube", "https://www.youtube.com/watch?v=test").is_ok());
        assert!(external_video_url("rutube", "https://rutube.ru/video/test/").is_ok());
        for url in [
            "file:///C:/x.exe",
            "https://youtube.com.evil.test/watch?v=x",
            "https://user@youtube.com/x",
            "http://youtube.com/x",
            "https://youtube.com:444/x",
        ] {
            assert!(external_video_url("youtube", url).is_err(), "{url}");
        }
        assert!(external_video_url("file", "https://youtube.com/watch?v=test").is_err());
    }
    #[test]
    fn payload_cannot_read_arbitrary_files() {
        let a = ApiClient::new("https://launcher.samoy.love").unwrap();
        let b = std::env::temp_dir();
        for bad in [
            "file:///C:/secret",
            "data:text/a,hi",
            "javascript:x",
            "Assets/../../secret",
            "https://user:password@x.test/a",
        ] {
            assert!(location(bad, &b, &a).is_err(), "{bad}");
        }
    }

    #[test]
    fn media_urls_use_server_origin_and_keep_case_sensitive_paths() {
        let api = ApiClient::new("https://launcher.samoy.love/api/v1").unwrap();
        let bundle = std::env::temp_dir();
        assert_eq!(
            location("icon.png", &bundle, &api).unwrap(),
            "https://launcher.samoy.love/icon.png"
        );
        assert_eq!(
            location("//cdn.example.test/icon.png", &bundle, &api).unwrap(),
            "https://cdn.example.test/icon.png"
        );
        assert_ne!(
            location("Icon.PNG", &bundle, &api).unwrap(),
            location("icon.png", &bundle, &api).unwrap()
        );
    }

    #[test]
    fn stale_image_is_revalidated_without_downloading_it_again() {
        let temp = tempfile::tempdir().unwrap();
        let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let api = ApiClient::new(&format!("http://{}", server.server_addr()))
            .unwrap()
            .with_diagnostics(temp.path().join("profile"));
        let diagnostic_root = temp.path().to_owned();
        let diagnostic_path = diagnostic_root.join(format!(
            "{}.img",
            blake3::hash(api.base.join("/fish.png").unwrap().as_str().as_bytes()).to_hex()
        ));
        let worker = std::thread::spawn(move || {
            let first = server.recv_timeout(SERVER_WAIT).unwrap().unwrap();
            assert!(
                first
                    .headers()
                    .iter()
                    .all(|h| !h.field.equiv("If-None-Match"))
            );
            first
                .respond(
                    tiny_http::Response::from_data(FISH_ICON)
                        .with_header(tiny_http::Header::from_bytes("ETag", "\"fish-v1\"").unwrap()),
                )
                .unwrap();
            let second = server.recv_timeout(SERVER_WAIT).unwrap().unwrap();
            assert!(
                second
                    .headers()
                    .iter()
                    .any(|h| h.field.equiv("If-None-Match") && h.value.as_str() == "\"fish-v1\""),
                "Second request: {} {}; headers: {:?}; {}",
                second.method(),
                second.url(),
                second.headers(),
                cache_diagnostics(&diagnostic_root, &diagnostic_path)
            );
            second.respond(tiny_http::Response::empty(304)).unwrap();
        });
        let path = image_path("/fish.png", temp.path(), temp.path(), &api).unwrap();
        let metadata_bytes = fs::read(path.with_extension("meta")).unwrap_or_else(|error| {
            panic!(
                "Image validators were not persisted after 200: {error}; diagnostics: {}",
                fs::read_to_string(temp.path().join("profile/logs/launcher.log"))
                    .unwrap_or_default()
            )
        });
        let metadata: Validators = serde_json::from_slice(&metadata_bytes).unwrap();
        assert_eq!(metadata.etag.as_deref(), Some("\"fish-v1\""));
        assert_eq!(
            metadata.payload_hash,
            blake3::hash(FISH_ICON).to_hex().as_str()
        );
        assert_eq!(
            validators(&path).unwrap().etag.as_deref(),
            Some("\"fish-v1\"")
        );
        stale_image(&api, temp.path());
        let after_stale = cache_diagnostics(temp.path(), &path);
        assert!(cached_image(&path), "After stale_image: {after_stale}");
        assert_eq!(
            validators(&path).unwrap().etag.as_deref(),
            Some("\"fish-v1\""),
            "After stale_image: {after_stale}"
        );
        assert_eq!(
            image_path("/fish.png", temp.path(), temp.path(), &api).unwrap(),
            path
        );
        assert_eq!(fs::read(&path).unwrap(), FISH_ICON);
        assert!(
            fs::metadata(&path)
                .unwrap()
                .modified()
                .unwrap()
                .elapsed()
                .unwrap()
                < CACHE_TTL,
            "Before revalidation: {after_stale}; after: {}",
            cache_diagnostics(temp.path(), &path)
        );
        worker.join().unwrap();
    }

    #[cfg(windows)]
    #[test]
    fn temporary_validator_sharing_violation_keeps_conditional_request() {
        use std::os::windows::fs::OpenOptionsExt;
        let temp = tempfile::tempdir().unwrap();
        let server = tiny_http::Server::http("127.0.0.1:0").unwrap();
        let api = ApiClient::new(&format!("http://{}", server.server_addr()))
            .unwrap()
            .with_diagnostics(temp.path().join("profile"));
        let path = stale_image(&api, temp.path());
        let meta = Validators {
            etag: Some("\"fish-v1\"".into()),
            payload_hash: blake3::hash(FISH_ICON).to_hex().to_string(),
            ..Default::default()
        };
        fs::write(
            path.with_extension("meta"),
            serde_json::to_vec(&meta).unwrap(),
        )
        .unwrap();
        let mut held = Some(
            fs::OpenOptions::new()
                .read(true)
                .share_mode(0)
                .open(path.with_extension("meta"))
                .unwrap(),
        );
        assert!(cached_image_result(&path).is_ok());
        assert_eq!(validators(&path).unwrap_err().raw_os_error(), Some(32));
        let worker = std::thread::spawn(move || {
            let request = server.recv_timeout(SERVER_WAIT).unwrap().unwrap();
            let conditional = request
                .headers()
                .iter()
                .any(|h| h.field.equiv("If-None-Match") && h.value.as_str() == "\"fish-v1\"");
            let headers = format!("{:?}", request.headers());
            request
                .respond(tiny_http::Response::empty(if conditional {
                    304
                } else {
                    404
                }))
                .unwrap();
            assert!(
                conditional,
                "Expected If-None-Match after unlocking metadata: {headers}"
            );
        });
        let mut reads = 0;
        assert_eq!(
            image_path_with_read_observer(
                "/fish.png",
                temp.path(),
                temp.path(),
                &api,
                |error, attempt| {
                    assert_eq!(error.raw_os_error(), Some(32));
                    assert_eq!(attempt, 1);
                    reads += 1;
                    drop(held.take());
                }
            )
            .unwrap(),
            path
        );
        worker.join().unwrap();
        assert_eq!(reads, 1);
        assert_eq!(fs::read(&path).unwrap(), FISH_ICON);
        assert_eq!(
            validators(&path).unwrap().etag.as_deref(),
            Some("\"fish-v1\"")
        );
        assert!(
            fs::metadata(&path)
                .unwrap()
                .modified()
                .unwrap()
                .elapsed()
                .unwrap()
                < CACHE_TTL
        );
    }

    #[test]
    fn malformed_and_mismatched_validator_data_is_never_retried_or_trusted() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("cached.img");
        fs::write(&path, FISH_ICON).unwrap();
        for bytes in [
            b"not json".to_vec(),
            serde_json::to_vec(&Validators {
                etag: Some("\"untrusted\"".into()),
                payload_hash: "wrong-payload".into(),
                ..Default::default()
            })
            .unwrap(),
        ] {
            fs::write(path.with_extension("meta"), bytes).unwrap();
            let mut retries = 0;
            let error = validators_with_retry(&path, |_, _| retries += 1).unwrap_err();
            assert_eq!(error.kind(), std::io::ErrorKind::InvalidData);
            assert_eq!(retries, 0);
        }
    }

    #[cfg(windows)]
    #[test]
    fn validator_access_denied_is_not_retried() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("cached.img");
        fs::write(&path, FISH_ICON).unwrap();
        // Opening a directory as a regular Windows file produces ERROR_ACCESS_DENIED;
        // no ACL, real profile or process-wide environment changes are needed.
        fs::create_dir(path.with_extension("meta")).unwrap();
        let mut retries = 0;
        let error = validators_with_retry(&path, |_, _| retries += 1).unwrap_err();
        assert_eq!(error.raw_os_error(), Some(5));
        assert_eq!(retries, 0);
        assert_eq!(fs::read(&path).unwrap(), FISH_ICON);
        assert!(path.with_extension("meta").is_dir());
    }

    #[cfg(windows)]
    #[test]
    fn persistent_validator_sharing_violation_has_a_fixed_retry_limit() {
        use std::os::windows::fs::OpenOptionsExt;
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("cached.img");
        fs::write(&path, FISH_ICON).unwrap();
        let metadata = serde_json::to_vec(&Validators {
            etag: Some("\"fish-v1\"".into()),
            payload_hash: blake3::hash(FISH_ICON).to_hex().to_string(),
            ..Default::default()
        })
        .unwrap();
        fs::write(path.with_extension("meta"), &metadata).unwrap();
        let held = fs::OpenOptions::new()
            .read(true)
            .share_mode(0)
            .open(path.with_extension("meta"))
            .unwrap();
        let mut retries = Vec::new();
        let error = validators_with_retry(&path, |error, attempt| {
            retries.push((attempt, error.raw_os_error()));
        })
        .unwrap_err();
        assert_eq!(error.raw_os_error(), Some(32));
        assert_eq!(retries, [(1, Some(32)), (2, Some(32)), (3, Some(32))]);
        drop(held);
        assert_eq!(fs::read(&path).unwrap(), FISH_ICON);
        assert_eq!(fs::read(path.with_extension("meta")).unwrap(), metadata);
    }

    fn cache_diagnostics(root: &Path, path: &Path) -> String {
        format!(
            "cached={:?}; validators={:?}; image={:?}; meta={:?}; log={}",
            cached_image_result(path),
            validators(path),
            fs::metadata(path).map(|m| (m.len(), m.modified())),
            fs::read_to_string(path.with_extension("meta")),
            fs::read_to_string(root.join("profile/logs/launcher.log")).unwrap_or_default()
        )
    }

    #[test]
    fn concurrent_image_requests_share_one_download() {
        let temp = tempfile::tempdir().unwrap();
        let (api, worker) = image_server(vec![200]);
        let barrier = std::sync::Barrier::new(8);
        std::thread::scope(|scope| {
            let handles: Vec<_> = (0..8)
                .map(|_| {
                    let api = &api;
                    let path = temp.path();
                    let barrier = &barrier;
                    scope.spawn(move || {
                        barrier.wait();
                        image_path("/fish.png", path, path, api).unwrap()
                    })
                })
                .collect();
            let paths: Vec<_> = handles.into_iter().map(|h| h.join().unwrap()).collect();
            assert!(paths.iter().all(|p| p == &paths[0]));
        });
        worker.join().unwrap();
    }
}
