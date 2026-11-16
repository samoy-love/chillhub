use slint::{Image, SharedPixelBuffer};
use std::path::Path;
use std::sync::{
    Mutex,
    atomic::{AtomicU64, Ordering},
};

static IMAGE_DECODE_LOCK: Mutex<()> = Mutex::new(());
pub(super) static IMAGE_REQUEST_EPOCH: AtomicU64 = AtomicU64::new(0);

pub(super) fn image_pixels(
    path: &Path,
    width: u32,
    height: u32,
) -> Result<SharedPixelBuffer<slint::Rgba8Pixel>, String> {
    image_pixels_for_epoch(path, width, height, None)
}
pub(super) fn image_pixels_for_epoch(
    path: &Path,
    width: u32,
    height: u32,
    epoch: Option<u64>,
) -> Result<SharedPixelBuffer<slint::Rgba8Pixel>, String> {
    // Large source images are decoded one at a time, including cancelled game
    // selections. This bounds peak memory during rapid library navigation.
    let _decode = IMAGE_DECODE_LOCK
        .lock()
        .map_err(|_| "Не удалось открыть изображение")?;
    if epoch.is_some_and(|epoch| IMAGE_REQUEST_EPOCH.load(Ordering::SeqCst) != epoch) {
        return Err("Изображение больше не выбрано".into());
    }
    let decoded = image::ImageReader::open(path)
        .map_err(|e| e.to_string())?
        .with_guessed_format()
        .map_err(|e| e.to_string())?
        .decode()
        .map_err(|e| format!("{}: {e}", path.display()))?;
    // Optimized assets already fit their display budget. Avoid resampling them
    // and constructing an additional full-size image on every selection.
    let mut rgba = if decoded.width() > width || decoded.height() > height {
        decoded.thumbnail(width, height).into_rgba8()
    } else {
        decoded.into_rgba8()
    };
    // Convert alpha once on the decoding worker, rather than for every sampled
    // texel in every animation/resize frame. Opaque photos are unchanged.
    premultiply_image(rgba.as_mut());
    Ok(SharedPixelBuffer::clone_from_slice(
        rgba.as_raw(),
        rgba.width(),
        rgba.height(),
    ))
}
fn premultiply_image(bytes: &mut [u8]) {
    for pixel in bytes.as_chunks_mut::<4>().0 {
        let alpha = pixel[3] as u16;
        if alpha != 255 {
            for channel in &mut pixel[..3] {
                *channel = (*channel as u16 * alpha / 255) as u8;
            }
        }
    }
}
pub(super) fn source_image_pixels(
    value: &str,
    bundle: &Path,
    cache: &Path,
    api: &chillhub_native::net::ApiClient,
    width: u32,
    height: u32,
    epoch: Option<u64>,
) -> Result<SharedPixelBuffer<slint::Rgba8Pixel>, String> {
    let current = || epoch.is_none_or(|epoch| IMAGE_REQUEST_EPOCH.load(Ordering::SeqCst) == epoch);
    let mut last_error = String::new();
    for attempt in 1..=2 {
        if !current() {
            return Err("Изображение больше не выбрано".into());
        }
        let path = chillhub_native::media::image_path(value, bundle, cache, api)?;
        match image_pixels_for_epoch(&path, width, height, epoch) {
            Ok(pixels) => return Ok(pixels),
            Err(error) => {
                if !current() {
                    return Err(error);
                }
                chillhub_native::media::invalidate_image(value, bundle, cache, api);
                api.event(
                    "image.decode_error",
                    serde_json::json!({"attempt": attempt, "error": error}),
                );
                last_error = error;
                if value.starts_with("Assets/") || value.starts_with("art/") {
                    break;
                }
            }
        }
    }
    Err(last_error)
}
pub(super) fn from_pixels(pixels: SharedPixelBuffer<slint::Rgba8Pixel>) -> Image {
    // Pixel ownership crosses the worker boundary; the UI does no large copies.
    Image::from_rgba8_premultiplied(pixels)
}

#[cfg(test)]
#[path = "image_loader_tests.rs"]
mod tests;
