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

fn client_builder() -> reqwest::blocking::ClientBuilder {
    reqwest::blocking::Client::builder()
        .user_agent(concat!("ChillHub/", env!("CARGO_PKG_VERSION")))
        .connect_timeout(Duration::from_secs(4))
        .timeout(Duration::from_secs(10))
        .redirect(reqwest::redirect::Policy::none())
}

fn client() -> Result<&'static reqwest::blocking::Client, String> {
    IMAGE_CLIENT
        .get_or_init(|| client_builder().build().map_err(|e| e.to_string()))
        .as_ref()
        .map_err(Clone::clone)
}

fn retryable_image_request(error: &reqwest::Error) -> bool {
    use std::error::Error as _;
    if error.is_timeout() || error.is_connect() || error.is_body() {
        return true;
    }
    let mut source = error.source();
    while let Some(cause) = source {
        if cause
            .downcast_ref::<std::io::Error>()
            .is_some_and(|error| error.kind() == std::io::ErrorKind::ConnectionReset)
        {
            return true;
        }
        source = cause.source();
    }
    false
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

fn renew_cached_image(path: &Path) -> std::io::Result<()> {
    let mut options = fs::OpenOptions::new();
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::FILE_WRITE_ATTRIBUTES;
        // Other readers can prevent data writes while the file is open.
        // A 304 only needs to update the last-write timestamp.
        options.access_mode(FILE_WRITE_ATTRIBUTES);
    }
    #[cfg(not(windows))]
    options.write(true);
    options
        .open(path)?
        .set_times(fs::FileTimes::new().set_modified(std::time::SystemTime::now()))
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
    image_path_with_client(value, bundle, cache, api, client, |_, _| {})
}

// A fixture can provide its own TLS trust root and release a competing Windows
// handle after the first failed read without changing process-wide settings.
fn image_path_with_client<'a>(
    value: &str,
    bundle: &Path,
    cache: &Path,
    api: &ApiClient,
    get_client: impl FnOnce() -> Result<&'a reqwest::blocking::Client, String>,
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
    let client = get_client()?;
    for attempt in 1..=2 {
        let mut request = client.get(&address);
        if let Some(etag) = &validators.etag {
            request = request.header("If-None-Match", etag);
        }
        if let Some(modified) = &validators.modified {
            request = request.header("If-Modified-Since", modified);
        }
        let response = request.build().and_then(|request| {
            api.event(
                "image.cache_request",
                serde_json::json!({"path": path, "key": hash, "cached": cached,
                    "cachedETag": validators.etag.is_some(),
                    "requestETag": request.headers().contains_key("if-none-match"),
                    "requestLastModified": request.headers().contains_key("if-modified-since")}),
            );
            client.execute(request)
        });
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
                if let Err(error) = renew_cached_image(&output) {
                    api.event(
                        "image.cache_touch_error",
                        serde_json::json!({"path": path, "error": error.to_string(),
                            "osCode": error.raw_os_error()}),
                    );
                }
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
            Err(error) => {
                let retry = retryable_image_request(&error);
                api.event(
                    "image.request_error",
                    serde_json::json!({"path": path, "cause": format!("{:?}", error.without_url())}),
                );
                (
                    Err("Не удалось связаться с сервером изображений".into()),
                    retry,
                )
            }
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

pub fn video_location(value: &str, api: &ApiClient) -> Result<String, String> {
    let url = api
        .base
        .join("/")
        .and_then(|base| base.join(value))
        .map_err(|_| "Некорректный адрес видео")?;
    if value.is_empty()
        || url.origin() != api.base.origin()
        || !matches!(url.scheme(), "https" | "http")
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
        || !url.path().starts_with("/assets/")
        || !url.path().ends_with(".mp4")
    {
        return Err("Видео доступно только с сервера Chill Hub".into());
    }
    Ok(url.to_string())
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
#[path = "media_tests.rs"]
mod tests;
