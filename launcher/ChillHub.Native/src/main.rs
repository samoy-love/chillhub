#![windows_subsystem = "windows"]

#[cfg(feature = "benchmark")]
mod benchmark;
mod catalog;
mod controller;
#[cfg(test)]
mod controller_tests;
mod desktop;
mod install_ui;
mod karaoke;
mod orphan_ui;
#[cfg(feature = "benchmark")]
mod perf_probe;
mod queue_ui;
mod security_offers;
mod settings_ui;
mod video;
use chillhub_native::web_video;
mod window_geometry;
use catalog::{Game, default_choice, filtered_indices, valid_choice};
use chillhub_native::{
    config::{AppConfig, LaunchTarget},
    launch::{LaunchOption, ShortcutRequest},
    model::GameRecord,
    queue::QueueItem,
    service::Services,
    ui::*,
};
use raw_window_handle::{HasWindowHandle, RawWindowHandle};
use slint::{
    ComponentHandle, Image, Model, ModelRc, SharedPixelBuffer, SharedString, Timer, TimerMode,
    VecModel,
};
use std::cell::RefCell;
use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::rc::Rc;
use std::time::Duration;
use video::{VideoPlayer, VideoRect};

type State = Rc<RefCell<AppState>>;
static PANIC_CONTEXT: std::sync::OnceLock<(chillhub_native::net::ApiClient, PathBuf)> =
    std::sync::OnceLock::new();
static IMAGE_DECODE_LOCK: std::sync::Mutex<()> = std::sync::Mutex::new(());
static IMAGE_REQUEST_EPOCH: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
static HERO_DISPLAY_EPOCH: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
struct AppState {
    catalog: Vec<Game>,
    data_root: PathBuf,
    art_root: PathBuf,
    selected: usize,
    visible: Vec<usize>,
    icons: Vec<Image>,
    choices: HashMap<String, (String, String)>,
    query: String,
    only_mods: bool,
    mod_query: String,
    mod_category: String,
    generation: u64,
    catalog_generation: u64,
    gallery_generation: u64,
    gallery_index: usize,
    video: Option<VideoPlayer>,
    web_video: Option<web_video::WebVideo>,
    native_hwnd: isize,
    video_timer: Timer,
    toast_timer: Timer,
    report_notices: std::collections::VecDeque<String>,
    maintenance_timer: Timer,
    update_timer: Timer,
    poll_inflight: bool,
    maintenance: chillhub_native::net::Maintenance,
    catalog_poll_inflight: bool,
    catalog_checked_at: std::time::Instant,
    last_activation: Option<std::time::Instant>,
    update_inflight: bool,
    security_offers: security_offers::Offers,
    orphan_notice: orphan_ui::Notice,
    estimate_inflight: bool,
    services: Services,
    prefs: AppConfig,
    seed: Vec<Game>,
    records: Vec<GameRecord>,
    option: Option<LaunchOption>,
    launching: std::collections::HashSet<(String, bool)>,
    choice_generation: u64,
    choice_epoch: std::sync::Arc<std::sync::atomic::AtomicU64>,
    tasks: Vec<QueueItem>,
    queue_speeds: queue_ui::Speeds,
    library_status: HashMap<String, (String, String)>,
    pending: Option<controller::Confirmation>,
    shortcut: Option<ShortcutRequest>,
    update: Option<chillhub_native::update::UpdateRelease>,
    offline: bool,
}
fn strings(values: impl IntoIterator<Item = String>) -> ModelRc<SharedString> {
    Rc::new(VecModel::from(
        values
            .into_iter()
            .map(SharedString::from)
            .collect::<Vec<_>>(),
    ))
    .into()
}
fn image_pixels(
    path: &Path,
    width: u32,
    height: u32,
) -> Result<SharedPixelBuffer<slint::Rgba8Pixel>, String> {
    image_pixels_for_epoch(path, width, height, None)
}
fn image_pixels_for_epoch(
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
    if epoch
        .is_some_and(|epoch| IMAGE_REQUEST_EPOCH.load(std::sync::atomic::Ordering::SeqCst) != epoch)
    {
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
    let rgba = if decoded.width() > width || decoded.height() > height {
        decoded.thumbnail(width, height).into_rgba8()
    } else {
        decoded.into_rgba8()
    };
    Ok(SharedPixelBuffer::clone_from_slice(
        rgba.as_raw(),
        rgba.width(),
        rgba.height(),
    ))
}
fn source_image_pixels(
    value: &str,
    bundle: &Path,
    cache: &Path,
    api: &chillhub_native::net::ApiClient,
    width: u32,
    height: u32,
    epoch: Option<u64>,
) -> Result<SharedPixelBuffer<slint::Rgba8Pixel>, String> {
    let current = || {
        epoch.is_none_or(|epoch| {
            IMAGE_REQUEST_EPOCH.load(std::sync::atomic::Ordering::SeqCst) == epoch
        })
    };
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
fn from_pixels(pixels: SharedPixelBuffer<slint::Rgba8Pixel>) -> Image {
    // Pixel ownership crosses the worker boundary; the UI does no large copies.
    Image::from_rgba8(pixels)
}
fn roots() -> Result<(PathBuf, PathBuf), String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let parent = exe.parent().unwrap();
    if parent.join("Data/games.json").is_file() {
        return Ok((parent.to_owned(), parent.join("art")));
    }
    #[cfg(any(test, feature = "benchmark"))]
    {
        let project = Path::new(env!("CARGO_MANIFEST_DIR"));
        Ok((project.to_owned(), project.join("art")))
    }
    #[cfg(not(any(test, feature = "benchmark")))]
    Err("Не найдены файлы лаунчера. Переустановите Chill Hub.".into())
}
fn refresh_library(ui: &AppWindow, state: &State) {
    let mut s = state.borrow_mut();
    let visible = filtered_indices(&s.catalog, &s.query, s.only_mods);
    if visible != s.visible {
        s.visible = visible;
        let rows = s
            .visible
            .iter()
            .map(|&i| GameRow {
                id: s.catalog[i].id.clone().into(),
                title: s.catalog[i].title.clone().into(),
                icon: s.icons[i].clone(),
                has_mods: s.catalog[i].has_mods,
                status: s
                    .library_status
                    .get(&s.catalog[i].id)
                    .map(|v| v.0.clone())
                    .unwrap_or_default()
                    .into(),
                status_kind: s
                    .library_status
                    .get(&s.catalog[i].id)
                    .map(|v| v.1.clone())
                    .unwrap_or_default()
                    .into(),
            })
            .collect::<Vec<_>>();
        ui.set_games(Rc::new(VecModel::from(rows)).into());
    }
    ui.set_selected_game(
        s.visible
            .iter()
            .position(|&i| i == s.selected)
            .map(|i| i as i32)
            .unwrap_or(-1),
    );
    ui.set_search(s.query.clone().into());
    ui.set_only_mods(s.only_mods);
    drop(s);
    controller::sync_library_rows(ui, state);
}
fn refresh_choice(ui: &AppWindow, state: &State) {
    controller::refresh_choice(ui, state);
}
thread_local! { static ACTIVE_STATE: RefCell<Option<State>> = const { RefCell::new(None) }; }
fn select_game(ui: &AppWindow, state: &State, index: usize) {
    if index >= state.borrow().catalog.len() {
        return;
    }
    if !matches!(
        ui.get_panel_kind().as_str(),
        "settings" | "feedback" | "confirm" | "update" | "changelog"
    ) {
        close_panel(ui, state);
    }
    if state.borrow().selected == index && state.borrow().generation != 0 {
        refresh_library(ui, state);
        return;
    }
    let (game, art, data, generation, services) = {
        let mut s = state.borrow_mut();
        if index >= s.catalog.len() {
            return;
        }
        s.selected = index;

        s.generation += 1;
        (
            s.catalog[index].clone(),
            s.art_root.clone(),
            s.data_root.clone(),
            s.generation,
            s.services.clone(),
        )
    };
    ui.set_game_title(game.title.clone().into());
    ui.set_description(game.description.clone().into());
    ui.set_genre(game.genre.clone().into());
    ui.set_pack_name(game.modpack_name.clone().into());
    ui.set_pack_version(game.modpack_version.clone().into());
    ui.set_metadata_note(game.metadata_note.clone().into());
    ui.set_modpack_description(game.modpack_description.clone().into());
    ui.set_mod_count(game.mods.len() as i32);
    ui.set_mod_benefits(
        Rc::new(VecModel::from(
            game.mod_benefits
                .iter()
                .map(|b| ModBenefit {
                    title: b.title.clone().into(),
                    description: b.description.clone().into(),
                    icon: b.icon.clone().into(),
                })
                .collect::<Vec<_>>(),
        ))
        .into(),
    );
    ui.set_highlights(strings(game.highlights.clone()));
    ui.set_has_gallery(
        game.media
            .iter()
            .any(|media| media.kind == "video" || media.path != game.hero),
    );
    // Captions and indices must belong to the new game even while its posters decode.
    ui.set_media(
        Rc::new(VecModel::from(
            game.media
                .iter()
                .map(|media| MediaTile {
                    poster: Image::default(),
                    caption: media.caption.clone().into(),
                    is_video: media.kind == "video",
                    is_external: matches!(media.provider.as_str(), "youtube" | "rutube"),
                })
                .collect::<Vec<_>>(),
        ))
        .into(),
    );
    refresh_library(ui, state);
    refresh_choice(ui, state);
    let weak = ui.as_weak();
    let id = game.id.clone();
    let media = game.media.clone();
    let image_epoch = IMAGE_REQUEST_EPOCH.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1;
    // Show the small bundled artwork first. Fetch each remote replacement in the
    // background and publish it immediately; one slow poster cannot hide the hero.
    std::thread::spawn(move || {
        let current =
            || IMAGE_REQUEST_EPOCH.load(std::sync::atomic::Ordering::SeqCst) == image_epoch;
        let cache = services.paths.data_dir.join("cache/media");
        let load = |value: &str, width, height| {
            source_image_pixels(
                value,
                &data,
                &cache,
                &services.api,
                width,
                height,
                Some(image_epoch),
            )
        };
        for remote in [false, true] {
            if !current() {
                return;
            }
            let hero_source = if remote {
                &game.hero
            } else if !game.fallback_hero.is_empty() {
                &game.fallback_hero
            } else {
                &game.hero
            };
            let bundled = hero_source.starts_with("Assets/") || hero_source.starts_with("art/");
            if remote != bundled {
                let pixels = if bundled {
                    image_pixels_for_epoch(
                        &art.join(&id).join("hero.webp"),
                        1800,
                        700,
                        Some(image_epoch),
                    )
                    .or_else(|_| load(hero_source, 1800, 700))
                } else {
                    load(hero_source, 1800, 700)
                };
                if !current() {
                    return;
                }
                match pixels {
                    Ok(pixels) => post_hero(weak.clone(), generation, pixels),
                    Err(error) => services.api.event(
                        "image.hero_error",
                        serde_json::json!({"game": id, "error": error, "remote": remote}),
                    ),
                }
            }
            for (index, media) in media.iter().enumerate() {
                if !current() {
                    return;
                }
                let source = if remote {
                    &media.path
                } else if !media.fallback_path.is_empty() {
                    &media.fallback_path
                } else {
                    &media.path
                };
                let bundled = source.starts_with("Assets/") || source.starts_with("art/");
                if remote == bundled {
                    continue;
                }
                let pixels =
                    if let Some(optimized_index) = media.optimized_index.filter(|_| bundled) {
                        image_pixels_for_epoch(
                            &art.join(&id).join(format!("tile-{optimized_index}.webp")),
                            480,
                            300,
                            Some(image_epoch),
                        )
                        .or_else(|_| load(source, 480, 300))
                    } else {
                        load(source, 480, 300)
                    };
                if !current() {
                    return;
                }
                match pixels {
                    Ok(pixels) => post_tile(weak.clone(), generation, index, pixels),
                    Err(error) => services.api.event("image.poster_error", serde_json::json!({"game": id, "index": index, "error": error, "remote": remote})),
                }
            }
        }
        let _ = chillhub_native::media::trim_cache(&cache);
    });
}
fn post_tile(
    weak: slint::Weak<AppWindow>,
    generation: u64,
    index: usize,
    pixels: SharedPixelBuffer<slint::Rgba8Pixel>,
) {
    let _ = slint::invoke_from_event_loop(move || {
        if let Some(ui) = weak.upgrade() {
            ACTIVE_STATE.with(|slot| {
                if !slot
                    .borrow()
                    .as_ref()
                    .is_some_and(|s| s.borrow().generation == generation)
                {
                    return;
                }
                let model = ui.get_media();
                if let Some(mut tile) = model.row_data(index) {
                    tile.poster = from_pixels(pixels);
                    model.set_row_data(index, tile);
                }
            });
        }
    });
}
fn post_hero(
    weak: slint::Weak<AppWindow>,
    generation: u64,
    pixels: SharedPixelBuffer<slint::Rgba8Pixel>,
) {
    let _ = slint::invoke_from_event_loop(move || {
        if let Some(ui) = weak.upgrade() {
            ACTIVE_STATE.with(|slot| {
                if !slot
                    .borrow()
                    .as_ref()
                    .is_some_and(|s| s.borrow().generation == generation)
                {
                    return;
                }
                let hero_epoch =
                    HERO_DISPLAY_EPOCH.fetch_add(1, std::sync::atomic::Ordering::SeqCst) + 1;
                ui.set_hero_previous(ui.get_hero());
                ui.set_hero_progress(0.0);
                ui.set_hero(from_pixels(pixels));
                let weak = ui.as_weak();
                Timer::single_shot(Duration::from_millis(16), move || {
                    if let Some(ui) = weak.upgrade() {
                        ACTIVE_STATE.with(|slot| {
                            if HERO_DISPLAY_EPOCH.load(std::sync::atomic::Ordering::SeqCst)
                                == hero_epoch
                                && slot
                                    .borrow()
                                    .as_ref()
                                    .is_some_and(|s| s.borrow().generation == generation)
                            {
                                ui.set_hero_progress(1.0);
                            }
                        });
                    }
                });
                let weak = ui.as_weak();
                Timer::single_shot(Duration::from_millis(250), move || {
                    if let Some(ui) = weak.upgrade() {
                        ACTIVE_STATE.with(|slot| {
                            if HERO_DISPLAY_EPOCH.load(std::sync::atomic::Ordering::SeqCst)
                                == hero_epoch
                                && slot
                                    .borrow()
                                    .as_ref()
                                    .is_some_and(|s| s.borrow().generation == generation)
                            {
                                ui.set_hero_previous(Image::default());
                            }
                        });
                    }
                });
            });
        }
    });
}

fn toast(ui: &AppWindow, state: &State, text: &str) {
    chillhub_native::diagnostics::log(&state.borrow().services.paths.data_dir, "notice", text);
    ui.set_toast(text.into());
    let weak = ui.as_weak();
    state.borrow().toast_timer.start(
        TimerMode::SingleShot,
        Duration::from_millis(3200),
        move || {
            if let Some(ui) = weak.upgrade() {
                ui.set_toast("".into());
                ACTIVE_STATE.with(|slot| {
                    if let Some(state) = slot.borrow().as_ref() {
                        let next = state.borrow_mut().report_notices.pop_front();
                        if let Some(text) = next {
                            toast(&ui, state, &text);
                        }
                    }
                });
            }
        },
    );
}
fn clear_game(ui: &AppWindow, state: &State) {
    IMAGE_REQUEST_EPOCH.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    HERO_DISPLAY_EPOCH.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    {
        let mut s = state.borrow_mut();
        s.generation += 1;
        s.choice_generation = s
            .choice_epoch
            .fetch_add(1, std::sync::atomic::Ordering::SeqCst)
            + 1;
        s.option = None;
        s.selected = usize::MAX;
    }
    if matches!(ui.get_panel_kind().as_str(), "gallery" | "mods" | "manage") {
        close_panel(ui, state);
    }
    ui.set_game_title("".into());
    ui.set_description("".into());
    ui.set_genre("".into());
    ui.set_hero(Image::default());
    ui.set_hero_previous(Image::default());
    ui.set_hero_progress(1.0);
    ui.set_media(ModelRc::default());
    ui.set_mod_benefits(ModelRc::default());
    ui.set_mods(ModelRc::default());
    ui.set_mod_count(0);
    ui.set_has_gallery(false);
    ui.set_has_mods(false);
    ui.set_busy(false);
    ui.set_can_play(false);
}
fn close_panel(ui: &AppWindow, state: &State) {
    {
        let mut s = state.borrow_mut();
        s.gallery_generation += 1;
        s.video_timer.stop();
        if let Some(video) = &mut s.video {
            video.close();
        }
        s.web_video = None;
    }
    ui.set_selected_media_is_video(false);
    ui.set_selected_media_is_web_video(false);
    ui.set_panel_kind("".into());
    ui.set_gallery_image(Image::default());
    security_offers::after_panel_closed(ui, state);
    orphan_ui::after_panel_closed(ui, state);
}
fn refresh_mods(ui: &AppWindow, state: &State) {
    let s = state.borrow();
    let Some(game) = s.catalog.get(s.selected) else {
        return;
    };
    let query = s.mod_query.to_lowercase();
    let rows = game
        .mods
        .iter()
        .filter(|m| {
            (s.mod_category == "Все" || m.category == s.mod_category)
                && format!("{} {} {}", m.name, m.russian_name, m.description)
                    .to_lowercase()
                    .contains(&query)
        })
        .map(|m| ModRow {
            name: if m.russian_name.is_empty() {
                m.name.clone()
            } else {
                format!("{} · {}", m.russian_name, m.name)
            }
            .into(),
            description: m.description.clone().into(),
            category: m.category.clone().into(),
            version: m.version.clone().into(),
        })
        .collect::<Vec<_>>();
    ui.set_mods(Rc::new(VecModel::from(rows)).into());
    let mut categories = vec!["Все".to_string()];
    for m in &game.mods {
        if !categories.contains(&m.category) {
            categories.push(m.category.clone());
        }
    }
    ui.set_mod_categories(strings(categories));
    ui.set_mod_category(s.mod_category.clone().into());
}
fn open_panel(ui: &AppWindow, state: &State, kind: &str) {
    state
        .borrow()
        .services
        .api
        .event("ui.panel", serde_json::json!({"panel": kind}));
    close_panel(ui, state);
    let game = {
        let s = state.borrow();
        s.catalog.get(s.selected).cloned().unwrap_or_default()
    };
    let (title, subtitle, body) = match kind {
        "mods" => (
            game.modpack_name.clone(),
            format!(
                "Версия {} · {} модов",
                game.modpack_version,
                game.mods.len()
            ),
            game.metadata_note.clone(),
        ),
        "settings" => (
            "Настройки".into(),
            "Chill Hub под ваш компьютер".into(),
            "".into(),
        ),
        "downloads" => (
            "Загрузки".into(),
            "Игры и модпаки · очередь сохраняется при закрытии".into(),
            "".into(),
        ),
        "update" => (
            "Обновление лаунчера".into(),
            format!("Установлена версия {}", env!("CARGO_PKG_VERSION")),
            "Проверьте, доступна ли новая версия.".into(),
        ),
        "security" => (
            "Помощь с запуском".into(),
            "Проверка файлов и защиты Windows".into(),
            "Сначала проверьте файлы игры. Диагностика защиты покажет, не было ли блокировок."
                .into(),
        ),
        "manage" => (
            format!("Управление · {}", game.title),
            "Файлы, версия и восстановление".into(),
            "Выберите действие для текущей копии игры.".into(),
        ),
        "orphans" => (
            "Игры вне каталога".into(),
            "Установленные копии, которые больше не опубликованы".into(),
            "Файлы остаются на диске, пока вы сами не удалите их.".into(),
        ),
        "feedback" => (
            "Идея или ошибка".into(),
            "Сообщение команде Chill Hub".into(),
            "".into(),
        ),
        "changelog" => {
            ui.set_releases(ModelRc::new(VecModel::from(
                chillhub_native::changelog::entries_since(
                    &state.borrow().prefs.last_seen_changelog_version,
                )
                .into_iter()
                .map(|r| chillhub_native::ui::ReleaseRow {
                    version: r.version.into(),
                    date: r.date.into(),
                    changes: r.changes.into(),
                    fresh: r.fresh,
                })
                .collect::<Vec<_>>(),
            )));
            (
                "Что нового".into(),
                format!("Chill Hub {}", env!("CARGO_PKG_VERSION")),
                "".into(),
            )
        }
        _ => (
            "Chill Hub".into(),
            format!("Версия {}", env!("CARGO_PKG_VERSION")),
            "Ваши игры, друзья и моды в одном месте.".into(),
        ),
    };
    ui.set_panel_title(title.into());
    ui.set_panel_subtitle(subtitle.into());
    ui.set_panel_body(body.into());
    ui.set_panel_kind(kind.into());
    controller::prepare_panel(ui, state, kind);
    if kind == "mods" {
        {
            let mut s = state.borrow_mut();
            s.mod_query.clear();
            s.mod_category = "Все".into();
        }
        refresh_mods(ui, state);
    }
}
fn video_rect(ui: &AppWindow) -> VideoRect {
    let scale = ui.window().scale_factor();
    VideoRect {
        x: (ui.get_video_x() * scale).round() as i32,
        y: (ui.get_video_y() * scale).round() as i32,
        width: (ui.get_video_width() * scale).round() as i32,
        height: (ui.get_video_height() * scale).round() as i32,
        radius: (10.0 * scale).round() as i32,
    }
}
fn open_media(ui: &AppWindow, state: &State, index: usize) {
    if state.borrow().catalog.is_empty() {
        return;
    }
    close_panel(ui, state);
    let (game, data, generation) = {
        let mut s = state.borrow_mut();
        let Some(game) = s.catalog.get(s.selected).cloned() else {
            return;
        };
        s.gallery_index = index;
        (game, s.data_root.clone(), s.gallery_generation)
    };
    let Some(media) = game.media.get(index) else {
        return;
    };
    let services = state.borrow().services.clone();
    let is_web = media.kind == "video" && matches!(media.provider.as_str(), "youtube" | "rutube");
    ui.set_panel_title(game.title.into());
    ui.set_panel_subtitle(media.caption.clone().into());
    ui.set_gallery_caption(media.caption.clone().into());
    ui.set_gallery_index(index as i32);
    ui.set_panel_kind("gallery".into());
    ui.set_selected_media_is_video(media.kind == "video");
    ui.set_selected_media_is_web_video(is_web);
    if is_web {
        ui.set_media_provider_label(
            if media.provider == "rutube" {
                "Rutube"
            } else {
                "YouTube"
            }
            .into(),
        );
        open_web_video(ui, state, generation, media.url.clone());
        return;
    }
    let address = if media.kind == "video" {
        &media.url
    } else {
        &media.path
    };
    let location = chillhub_native::media::location(address, &data, &services.api);
    let path = match location {
        Ok(p) => PathBuf::from(p),
        Err(error) => {
            toast(ui, state, &error);
            return;
        }
    };
    if media.kind == "video" {
        services.api.event(
            "media.trailer_open",
            serde_json::json!({"game": game.id, "index": index}),
        );
        ui.set_video_position(0.0);
        ui.set_video_duration(0.0);
        ui.set_video_time_label("Загрузка трейлера…".into());
        let weak = ui.as_weak();
        let weak_state = Rc::downgrade(state);
        Timer::single_shot(Duration::from_millis(32), move || {
            if let (Some(ui), Some(state)) = (weak.upgrade(), weak_state.upgrade()) {
                if state.borrow().gallery_generation != generation || !ui.get_show_video() {
                    return;
                }
                let rect = video_rect(&ui);
                let result = state
                    .borrow_mut()
                    .video
                    .as_mut()
                    .ok_or_else(|| "Видеоплеер недоступен".to_string())
                    .and_then(|video| video.open(&path, rect));
                if let Err(error) = result {
                    toast(&ui, &state, &error);
                    return;
                }
                let weak = ui.as_weak();
                let weak_state = Rc::downgrade(&state);
                state.borrow().video_timer.start(
                    TimerMode::Repeated,
                    Duration::from_millis(200),
                    move || {
                        if let (Some(ui), Some(state)) = (weak.upgrade(), weak_state.upgrade()) {
                            // DPI can change without logical gallery dimensions changing.
                            // Reuse the playback timer; unchanged rectangles are a no-op.
                            if let Some(video) = state.borrow_mut().video.as_mut() {
                                video.set_rect(video_rect(&ui));
                                video.reveal_if_ready();
                            }
                            let status = state.borrow().video.as_ref().map(VideoPlayer::status);
                            if let Some(status) = status {
                                ui.set_video_position(status.position as f32);
                                ui.set_video_duration(status.duration as f32);
                                ui.set_video_volume((status.volume / 100.0) as f32);
                                ui.set_video_playing(
                                    status.ready && !status.paused && !status.ended,
                                );
                                let label = if let Some(error) = status.error {
                                    error
                                } else if status.ready {
                                    format!(
                                        "{} / {}",
                                        time_label(status.position),
                                        time_label(status.duration)
                                    )
                                } else {
                                    "Загрузка трейлера…".into()
                                };
                                ui.set_video_time_label(label.into());
                            }
                        }
                    },
                );
            }
        });
    } else {
        let weak = ui.as_weak();
        std::thread::spawn(move || {
            let pixels = if path.is_file() {
                image_pixels(&path, 1920, 1080)
            } else {
                source_image_pixels(
                    &path.to_string_lossy(),
                    &data,
                    &services.paths.data_dir.join("cache/media"),
                    &services.api,
                    1920,
                    1080,
                    None,
                )
            };
            if let Err(error) = &pixels {
                services
                    .api
                    .event("image.gallery_error", serde_json::json!({"error": error}));
            }
            let _ = slint::invoke_from_event_loop(move || {
                if let Some(ui) = weak.upgrade() {
                    ACTIVE_STATE.with(|slot| {
                        if let Some(state) = slot.borrow().as_ref()
                            && state.borrow().gallery_generation == generation {
                                match pixels {
                                    Ok(pixels) => ui.set_gallery_image(from_pixels(pixels)),
                                    Err(_) => {
                                        ui.set_gallery_caption("Не удалось загрузить изображение. Попробуйте открыть его ещё раз.".into());
                                        toast(&ui, state, "Не удалось загрузить изображение. Проверьте подключение.");
                                    }
                                }
                            }
                    });
                }
            });
        });
    }
}
fn time_label(seconds: f64) -> String {
    let value = seconds.max(0.0) as u64;
    format!("{}:{:02}", value / 60, value % 60)
}
fn web_video_rect(ui: &AppWindow) -> web_video::WebVideoRect {
    let rect = video_rect(ui);
    web_video::WebVideoRect {
        x: rect.x,
        y: rect.y,
        width: rect.width.max(1) as u32,
        height: rect.height.max(1) as u32,
    }
}
fn open_web_video(ui: &AppWindow, state: &State, generation: u64, source: String) {
    ui.set_web_video_status("Открываем видео…".into());
    let weak = ui.as_weak();
    let weak_state = Rc::downgrade(state);
    Timer::single_shot(Duration::from_millis(32), move || {
        let (Some(ui), Some(state)) = (weak.upgrade(), weak_state.upgrade()) else {
            return;
        };
        if state.borrow().gallery_generation != generation || !ui.get_show_web_video() {
            return;
        }
        let (hwnd, services) = {
            let s = state.borrow();
            (s.native_hwnd, s.services.clone())
        };
        let result =
            web_video::WebVideo::open(hwnd, web_video_rect(&ui), &source, &services.paths.data_dir);
        match result {
            Ok(player) => state.borrow_mut().web_video = Some(player),
            Err(error) => {
                services
                    .api
                    .event("media.web_error", serde_json::json!({"error": error}));
                ui.set_web_video_status(format!("{error}\nМожно открыть видео на сайте.").into());
                return;
            }
        }
        let weak = ui.as_weak();
        let weak_state = Rc::downgrade(&state);
        let mut previous = None;
        state.borrow().video_timer.start(TimerMode::Repeated, Duration::from_millis(200), move || {
            let (Some(ui), Some(state)) = (weak.upgrade(), weak_state.upgrade()) else { return; };
            if state.borrow().gallery_generation != generation { return; }
            let status = {
                let mut s = state.borrow_mut();
                s.web_video.as_mut().map(|player| {
                    let _ = player.set_rect(web_video_rect(&ui));
                    let status = player.status();
                    // The native browser surface covers Slint. Hide it on error
                    // so the retry explanation remains visible underneath it.
                    let _ = player.set_visible(ui.get_show_web_video() && status.error.is_none());
                    status
                })
            };
            let Some(status) = status else { return; };
            if status.close_requested { close_panel(&ui, &state); return; }
            let current = (status.ready, status.error.clone(), status.error_code);
            if previous.as_ref() != Some(&current) {
                state.borrow().services.api.event("media.web_state", serde_json::json!({
                    "ready": status.ready, "error": status.error, "errorCode": status.error_code, "refererConfigured": status.referer_configured
                }));
                ui.set_web_video_status(status.error.unwrap_or_else(|| if status.ready { "".into() } else { "Открываем видео…".into() }).into());
                previous = Some(current);
            }
        });
    });
}
#[cfg(feature = "benchmark")]
fn start_benchmark(ui: &AppWindow, state: &State, hwnd: isize) -> Result<Option<Timer>, String> {
    use benchmark::{Benchmark, Command, Phase, WindowInfo};
    use std::time::Instant;
    let Some(mut bench) = Benchmark::from_args().map_err(|e| e.to_string())? else {
        return Ok(None);
    };
    // Match the WPF prototype's outer window size at the same system DPI.
    use slint::winit_030::{WinitWindowAccessor, winit};
    ui.window().with_winit_window(|window| {
        window.set_window_level(winit::window::WindowLevel::AlwaysOnTop);
        let outer = window.outer_size();
        let inner = window.inner_size();
        let scale = window.scale_factor();
        let width = (1360.0 * scale) as u32 - outer.width.saturating_sub(inner.width);
        let height = (850.0 * scale) as u32 - outer.height.saturating_sub(inner.height);
        let _ = window.request_inner_size(winit::dpi::PhysicalSize::new(width, height));
        window.focus_window();
    });
    bench
        .ready(&WindowInfo::from_hwnd(hwnd))
        .map_err(|e| e.to_string())?;
    struct Active {
        command: Command,
        prepared: Instant,
        started: Option<Instant>,
        clicks: usize,
        start_video: serde_json::Value,
    }
    let mut active: Option<Active> = None;
    let weak = ui.as_weak();
    let weak_state = Rc::downgrade(state);
    let timer = Timer::default();
    timer.start(TimerMode::Repeated, benchmark::COMMAND_POLL_INTERVAL, move || {
        let (Some(ui), Some(state)) = (weak.upgrade(), weak_state.upgrade()) else { return; };
        let video_detail = || state.borrow().video.as_ref().map(|v| {
            let s = v.status(); serde_json::json!({"currentTime":s.position,"duration":s.duration,"frames":s.estimated_frame_number,"droppedFrames":s.dropped_frames,"decoderDroppedFrames":s.decoder_dropped_frames,"paused":s.paused,"ready":s.ready,"hwdec":s.hwdec,"pid":s.pid,"volume":s.volume,"width":s.width,"height":s.height,"fps":s.fps,"error":s.error,"displayWidth":ui.get_video_width(),"displayHeight":ui.get_video_height(),"scaleFactor":ui.window().scale_factor(),"logPath":s.log_path,"diagnosticsPath":s.diagnostics_path})
        }).unwrap_or(serde_json::Value::Null);
        if active.is_none() {
            if let Ok(Some(command)) = bench.poll() {
                let _ = bench.preparing(&command, &WindowInfo::from_hwnd(hwnd));
                if command.phase == Phase::Close {
                    close_panel(&ui, &state); let _ = bench.running(&command, &WindowInfo::from_hwnd(hwnd), serde_json::Value::Null);
                    let _ = bench.complete(&command, &WindowInfo::from_hwnd(hwnd), serde_json::Value::Null); let _ = slint::quit_event_loop(); return;
                }
                ui.window().set_minimized(false);
                ui.window().with_winit_window(|window| window.focus_window());
                match command.phase {
                    Phase::Idle | Phase::Switch | Phase::Warmup => {
                        state.borrow_mut().query.clear(); state.borrow_mut().only_mods = false;
                        let peak = state.borrow().catalog.iter().position(|g| g.id == "peak").unwrap_or(0); select_game(&ui, &state, peak);
                    },
                    Phase::Trailer => {
                        let peak = state.borrow().catalog.iter().position(|g| g.id == "peak").unwrap_or(0); select_game(&ui, &state, peak);
                        let media = state.borrow().catalog[peak].media.iter().position(|m| m.kind == "video");
                        if let Some(index) = media { open_media(&ui, &state, index); }
                    },
                    Phase::AfterClose => close_panel(&ui, &state),
                    Phase::Minimized => { close_panel(&ui, &state); ui.window().set_minimized(true); },
                    Phase::Close => {},
                }
                active = Some(Active { command, prepared: Instant::now(), started: None, clicks: 0, start_video: serde_json::Value::Null });
            }
        }
        let Some(current) = active.as_mut() else { return; };
        if current.started.is_none() {
            let ready = if current.command.phase == Phase::Trailer {
                state.borrow().video.as_ref().is_some_and(|v| { let s = v.status(); v.is_revealed() && s.ready && s.position > 0.2 && s.estimated_frame_number > 0 })
            } else { current.prepared.elapsed() > Duration::from_secs(2) };
            if !ready {
                if current.prepared.elapsed() > Duration::from_secs(15) { let _ = bench.error(&current.command, &WindowInfo::from_hwnd(hwnd), format!("Phase failed to become ready: {}", video_detail())); active = None; }
                return;
            }
            current.started = Some(Instant::now()); current.start_video = video_detail();
            let _ = bench.running(&current.command, &WindowInfo::from_hwnd(hwnd), serde_json::json!({"startVideo":current.start_video,"clickCount":0}));
        }
        let elapsed = current.started.unwrap().elapsed();
        if current.command.phase == Phase::Switch {
            if current.clicks < current.command.count && elapsed.as_millis() >= current.clicks as u128 * current.command.interval_ms as u128 {
                let id = &current.command.game_ids[current.clicks % current.command.game_ids.len()];
                let index = state.borrow().catalog.iter().position(|g| &g.id == id);
                if let Some(index) = index { select_game(&ui, &state, index); current.clicks += 1; }
            }
        }
        let seconds = if current.command.phase == Phase::Switch { current.command.count as f64 * current.command.interval_ms as f64 / 1000.0 } else { current.command.seconds };
        if elapsed.as_secs_f64() >= seconds {
            if current.command.phase == Phase::Switch && current.clicks != current.command.count {
                let _ = bench.error(&current.command, &WindowInfo::from_hwnd(hwnd), format!("Only {} of {} switches completed", current.clicks, current.command.count));
                active = None; return;
            }
            let _ = bench.complete(&current.command, &WindowInfo::from_hwnd(hwnd), serde_json::json!({"clickCount":current.clicks,"startVideo":current.start_video,"endVideo":video_detail(),"elapsedSeconds":elapsed.as_secs_f64(),"generation":state.borrow().generation}));
            active = None;
        }
    });
    Ok(Some(timer))
}
fn bind(ui: &AppWindow, state: &State) {
    macro_rules! handler {
        ($name:ident, |$u:ident, $s:ident $(, $arg:ident)*| $body:block) => {{
            let weak = ui.as_weak(); let weak_state = Rc::downgrade(state);
            ui.$name(move |$($arg),*| { if let (Some($u), Some($s)) = (weak.upgrade(), weak_state.upgrade()) $body });
        }};
    }
    handler!(on_game_selected, |ui, state, index| {
        let index = state.borrow().visible.get(index as usize).copied();
        if let Some(index) = index {
            select_game(&ui, &state, index);
        }
    });
    handler!(on_search_edited, |ui, state, text| {
        state.borrow_mut().query = text.to_string();
        refresh_library(&ui, &state);
    });
    handler!(on_mods_filter_toggled, |ui, state, enabled| {
        state.borrow_mut().only_mods = enabled;
        refresh_library(&ui, &state);
    });
    handler!(on_source_selected, |ui, state, source| {
        {
            let mut s = state.borrow_mut();
            let Some(game) = s.catalog.get(s.selected).cloned() else {
                return;
            };
            let choice = s
                .choices
                .entry(game.id.clone())
                .or_insert_with(|| default_choice(&game));
            choice.0 = source.to_string();
        }
        refresh_choice(&ui, &state);
    });
    handler!(on_mode_selected, |ui, state, mode| {
        {
            let mut s = state.borrow_mut();
            let Some(game) = s.catalog.get(s.selected).cloned() else {
                return;
            };
            let choice = s
                .choices
                .entry(game.id.clone())
                .or_insert_with(|| default_choice(&game));
            choice.1 = mode.to_string();
        }
        refresh_choice(&ui, &state);
    });
    handler!(on_play_requested, |ui, state| {
        controller::play(&ui, &state);
    });
    handler!(on_panel_requested, |ui, state, kind| {
        open_panel(&ui, &state, kind.as_str());
    });
    handler!(on_panel_closed, |ui, state| {
        if ui.get_panel_kind() == "settings" {
            ui.invoke_settings_leave("".into());
            return;
        }
        close_panel(&ui, &state);
    });
    handler!(on_media_selected, |ui, state, index| {
        open_media(&ui, &state, index.max(0) as usize);
    });
    handler!(on_gallery_step, |ui, state, delta| {
        let index = {
            let s = state.borrow();
            let count = s
                .catalog
                .get(s.selected)
                .map(|game| game.media.len())
                .unwrap_or(0);
            if count == 0 {
                return;
            }
            (s.gallery_index as i32 + delta).rem_euclid(count as i32) as usize
        };
        open_media(&ui, &state, index);
    });
    handler!(on_open_media_source, |ui, state| {
        let media = {
            let s = state.borrow();
            s.catalog
                .get(s.selected)
                .and_then(|g| g.media.get(s.gallery_index))
                .cloned()
        };
        if let Some(media) = media
            && let Err(error) =
                chillhub_native::media::open_external_video(&media.provider, &media.url)
        {
            toast(&ui, &state, &error);
        }
    });
    handler!(on_mod_search_edited, |ui, state, query| {
        state.borrow_mut().mod_query = query.to_string();
        refresh_mods(&ui, &state);
    });
    handler!(on_mod_category_selected, |ui, state, category| {
        state.borrow_mut().mod_category = category.to_string();
        refresh_mods(&ui, &state);
    });
    handler!(on_video_toggle, |_ui, state| {
        if let Some(video) = state.borrow().video.as_ref() {
            video.set_paused(!video.status().paused);
        }
    });
    handler!(on_video_seek, |_ui, state, seconds| {
        if let Some(video) = state.borrow().video.as_ref() {
            video.seek(seconds as f64);
        }
    });
    handler!(on_video_volume_changed, |_ui, state, volume| {
        if let Some(video) = state.borrow().video.as_ref() {
            video.set_volume(volume as f64 * 100.0);
        }
    });
    handler!(on_video_geometry_changed, |ui, state| {
        if let Ok(mut s) = state.try_borrow_mut() {
            if let Some(video) = s.video.as_mut()
                && ui.get_show_video()
            {
                video.set_rect(video_rect(&ui));
            }
            if let Some(video) = s.web_video.as_mut() {
                let _ =
                    video.set_visible(ui.get_show_web_video() && video.status().error.is_none());
                if ui.get_show_web_video() {
                    let _ = video.set_rect(web_video_rect(&ui));
                }
            }
        }
    });
    controller::bind(ui, state);
}
#[link(name = "dwmapi", kind = "raw-dylib")]
unsafe extern "system" {
    fn DwmSetWindowAttribute(hwnd: isize, attribute: u32, value: *const i32, size: u32) -> i32;
}
fn initialize_native(ui: &AppWindow, state: &State) -> Result<Option<Timer>, String> {
    use slint::winit_030::{WinitWindowAccessor, winit};
    let raw = ui
        .window()
        .window_handle()
        .window_handle()
        .map(|h| h.as_raw())
        .map_err(|e| e.to_string())?;
    let RawWindowHandle::Win32(handle) = raw else {
        return Err("Недоступно окно Windows".into());
    };
    let hwnd = handle.hwnd.get();
    state.borrow_mut().native_hwnd = hwnd;
    unsafe {
        DwmSetWindowAttribute(hwnd, 20, &1, 4);
    }
    ui.window().with_winit_window(|window| {
        let prefs = &state.borrow().prefs;
        if std::env::var_os("CHILLHUB_DATA_DIR").is_some() {
            window.set_title("Chill Hub · Проверочная копия");
        }
        let outer = window.outer_size();
        let inner = window.inner_size();
        let scale = window.scale_factor();
        let frame = (
            outer.width.saturating_sub(inner.width),
            outer.height.saturating_sub(inner.height),
        );
        use windows_sys::Win32::Graphics::Gdi::{
            GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromWindow,
        };
        let mut monitor: MONITORINFO = unsafe { std::mem::zeroed() };
        monitor.cbSize = std::mem::size_of::<MONITORINFO>() as u32;
        let has_workarea = unsafe {
            GetMonitorInfoW(
                MonitorFromWindow(hwnd as _, MONITOR_DEFAULTTONEAREST),
                &mut monitor,
            ) != 0
        };
        let work = if has_workarea {
            (
                (monitor.rcWork.right - monitor.rcWork.left).max(1) as u32,
                (monitor.rcWork.bottom - monitor.rcWork.top).max(1) as u32,
            )
        } else {
            window
                .current_monitor()
                .map(|m| (m.size().width, m.size().height))
                .unwrap_or((1920, 1080))
        };
        let (width, height) = window_geometry::restored_inner_size(
            (prefs.window_width, prefs.window_height),
            scale,
            frame,
            work,
        );
        let _ = window.request_inner_size(winit::dpi::PhysicalSize::new(width, height));
        if has_workarea {
            let x = monitor.rcWork.left + (work.0.saturating_sub(width + frame.0) / 2) as i32;
            let y = monitor.rcWork.top + (work.1.saturating_sub(height + frame.1) / 2) as i32;
            window.set_outer_position(winit::dpi::PhysicalPosition::new(x, y));
        }
        window.set_maximized(prefs.window_maximized);
    });
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let player = exe.parent().unwrap().join("mpv/mpv.exe");
    let logs = state.borrow().services.paths.data_dir.join("logs/video");
    state.borrow_mut().video = Some(VideoPlayer::new(hwnd, player, logs)?);
    #[cfg(feature = "benchmark")]
    {
        start_benchmark(ui, state, hwnd)
    }
    #[cfg(not(feature = "benchmark"))]
    {
        Ok(None)
    }
}
fn run() -> Result<(), String> {
    let args: Vec<String> = std::env::args().collect();
    let renderer = args
        .windows(2)
        .find(|pair| pair[0] == "--renderer")
        .map(|pair| pair[1].as_str())
        .unwrap_or("femtovg-wgpu");
    let accepted = matches!(renderer, "femtovg-wgpu" | "software")
        || (cfg!(feature = "benchmark") && renderer == "femtovg");
    if !accepted {
        return Err(format!("Неизвестный способ отрисовки: {renderer}"));
    }
    slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name(renderer.into())
        .select()
        .map_err(|e| e.to_string())?;
    let (data_root, art_root) = roots()?;
    let seed = catalog::load(&data_root.join("Data/games.json")).unwrap_or_default();
    let paths = chillhub_native::paths::AppPaths::discover()?;
    let instance = desktop::Instance::acquire(&paths.data_dir, &args)?;
    if !instance.primary {
        return Ok(());
    }
    let (services, prefs) = Services::new(paths)?;
    let _ = PANIC_CONTEXT.set((services.api.clone(), services.paths.data_dir.clone()));
    let shortcut = ShortcutRequest::parse_args(&args)?;
    let state = Rc::new(RefCell::new(AppState {
        visible: vec![],
        selected: 0,
        catalog: vec![],
        records: vec![],
        seed,
        data_root,
        art_root,
        icons: vec![],
        choices: HashMap::new(),
        launching: Default::default(),
        query: String::new(),
        only_mods: false,
        mod_query: String::new(),
        mod_category: "Все".into(),
        generation: 0,
        catalog_generation: 0,
        gallery_generation: 0,
        gallery_index: 0,
        video: None,
        web_video: None,
        native_hwnd: 0,
        video_timer: Timer::default(),
        toast_timer: Timer::default(),
        report_notices: Default::default(),
        maintenance_timer: Timer::default(),
        update_timer: Timer::default(),
        poll_inflight: false,
        maintenance: Default::default(),
        catalog_poll_inflight: false,
        catalog_checked_at: std::time::Instant::now(),
        last_activation: None,
        update_inflight: false,
        security_offers: Default::default(),
        orphan_notice: Default::default(),
        estimate_inflight: false,
        services,
        prefs,
        option: None,
        choice_generation: 0,
        choice_epoch: std::sync::Arc::new(std::sync::atomic::AtomicU64::new(0)),
        tasks: vec![],
        queue_speeds: Default::default(),
        library_status: Default::default(),
        pending: None,
        shortcut,
        update: None,
        offline: false,
    }));
    ACTIVE_STATE.with(|slot| *slot.borrow_mut() = Some(state.clone()));
    let ui = AppWindow::new().map_err(|e| e.to_string())?;
    ui.set_launcher_version(env!("CARGO_PKG_VERSION").into());
    bind(&ui, &state);
    controller::initialize(&ui, &state);
    instance.listen(ui.as_weak());
    ui.show().map_err(|e| e.to_string())?;
    controller::install_window_refresh(&ui);
    #[cfg(feature = "benchmark")]
    let frame_path = args
        .windows(2)
        .find(|pair| pair[0] == "--frame-log")
        .map(|pair| PathBuf::from(&pair[1]));
    #[cfg(feature = "benchmark")]
    let frame_probe = Rc::new(RefCell::new(None));
    #[cfg(feature = "benchmark")]
    let probe_slot = frame_probe.clone();
    let benchmark_timer = Rc::new(RefCell::new(None));
    let timer_slot = benchmark_timer.clone();
    let weak_ui = ui.as_weak();
    let weak_state = Rc::downgrade(&state);
    // Winit exposes its HWND only after the first event-loop iteration.
    Timer::single_shot(Duration::from_millis(80), move || {
        if let (Some(ui), Some(state)) = (weak_ui.upgrade(), weak_state.upgrade()) {
            match initialize_native(&ui, &state) {
                Ok(timer) => *timer_slot.borrow_mut() = timer,
                Err(error) => {
                    eprintln!("{error}");
                    toast(&ui, &state, &error);
                }
            }
            #[cfg(feature = "benchmark")]
            if let Some(path) = frame_path {
                match perf_probe::install(&ui, path) {
                    Ok(probe) => *probe_slot.borrow_mut() = Some(probe),
                    Err(error) => {
                        eprintln!("{error}");
                        toast(&ui, &state, &error);
                    }
                }
            }
        }
    });
    let tray = match desktop::create_tray(&ui, &state) {
        Ok(tray) => Some(tray),
        Err(error) => {
            toast(
                &ui,
                &state,
                &format!("{error}. При закрытии окна лаунчер завершит работу."),
            );
            let weak = ui.as_weak();
            let weak_state = Rc::downgrade(&state);
            ui.window().on_close_requested(move || {
                if let (Some(ui), Some(state)) = (weak.upgrade(), weak_state.upgrade()) {
                    controller::request_exit(&ui, &state);
                    return slint::CloseRequestResponse::KeepWindowShown;
                }
                let _ = slint::quit_event_loop();
                slint::CloseRequestResponse::HideWindow
            });
            None
        }
    };
    // The tray is a native Win32 window, so Slint must not exit when its last
    // visible window is hidden. Exit is explicit in the close/tray callbacks.
    slint::run_event_loop_until_quit().map_err(|e| e.to_string())?;
    drop(tray);
    controller::save_window(&ui, &state);
    state.borrow().services.queue.shutdown();
    #[cfg(feature = "benchmark")]
    drop(frame_probe);
    drop(benchmark_timer);
    close_panel(&ui, &state);
    ACTIVE_STATE.with(|slot| *slot.borrow_mut() = None);
    Ok(())
}
fn main() {
    let args = std::env::args().collect::<Vec<_>>();
    if let Some(folder) = args
        .windows(2)
        .find(|pair| pair[0] == "--preview-directory")
        .map(|p| PathBuf::from(&p[1]))
    {
        if !folder.is_absolute() {
            desktop::show_error("Проверочная папка должна иметь полный путь");
            return;
        }
        // Explicit local QA profile, isolated before any worker starts. Never migrated
        // into the production roaming configuration or included in an update payload.
        unsafe {
            std::env::set_var("CHILLHUB_DATA_DIR", folder.join("state"));
            std::env::set_var("CHILLHUB_GAMES_DIR", folder.join("games"));
            std::env::set_var("CHILLHUB_METRICS", "0");
        }
    }
    std::panic::set_hook(Box::new(|info| {
        chillhub_native::diagnostics::startup_log("panic", &info.to_string());
        if let Some((api, data)) = PANIC_CONTEXT.get() {
            chillhub_native::diagnostics::report_error(api, data, "panic", &info.to_string());
        }
    }));
    // Set process-local renderer preferences before any worker threads exist.
    // A launcher should prefer the integrated adapter and leave the gaming GPU asleep.
    unsafe {
        if std::env::var_os("WGPU_POWER_PREF").is_none() {
            std::env::set_var("WGPU_POWER_PREF", "low");
        }
        if std::env::var_os("WGPU_BACKEND").is_none() {
            std::env::set_var("WGPU_BACKEND", "dx12");
        }
    }
    if let Err(error) = run() {
        let log = chillhub_native::diagnostics::startup_log("startup-error", &error);
        if let Some((api, data)) = PANIC_CONTEXT.get() {
            chillhub_native::diagnostics::report_error(api, data, "startup", &error);
        }
        let details = match log {
            Some(path) => format!("{error}\n\nПодробности: {}", path.display()),
            None => format!("{error}\n\nЖурнал недоступен или отключён."),
        };
        eprintln!("{details}");
        desktop::show_error(&details);
    }
}
