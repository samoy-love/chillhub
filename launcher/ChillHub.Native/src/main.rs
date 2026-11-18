#![windows_subsystem = "windows"]

#[cfg(feature = "benchmark")]
mod benchmark;
mod catalog;
mod controller;
#[cfg(test)]
mod controller_tests;
mod desktop;
mod frame_pacing;
mod image_loader;
mod install_ui;
mod karaoke;
#[cfg(test)]
mod main_runtime_tests;
mod orphan_ui;
#[cfg(feature = "benchmark")]
mod perf_probe;
mod queue_ui;
mod security_offers;
mod settings_ui;
mod video;
mod window_geometry;
use catalog::{Game, default_choice, filtered_indices, switching_source};
use chillhub_native::{
    config::AppConfig,
    launch::{LaunchOption, ShortcutRequest},
    model::GameRecord,
    queue::QueueItem,
    service::Services,
    ui::*,
};
use image_loader::{
    IMAGE_REQUEST_EPOCH, from_pixels, image_pixels, image_pixels_for_epoch, source_image_pixels,
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
use video::{VideoAction, VideoPlayer, VideoRect};

type State = Rc<RefCell<AppState>>;
static PANIC_CONTEXT: std::sync::OnceLock<(chillhub_native::net::ApiClient, PathBuf)> =
    std::sync::OnceLock::new();
struct AppState {
    catalog: Vec<Game>,
    data_root: PathBuf,
    art_root: PathBuf,
    selected: usize,
    displayed_game_id: Option<String>,
    visible: Vec<usize>,
    icons: Vec<Image>,
    choices: HashMap<String, (String, String)>,
    query: String,
    mod_query: String,
    mod_category: String,
    generation: u64,
    catalog_generation: u64,
    gallery_generation: u64,
    gallery_index: usize,
    video: Option<VideoPlayer>,
    native_hwnd: isize,
    viewer_restore: Option<ViewerRestore>,
    viewer_previous_volume: Option<f64>,
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
    defer_startup_changelog: bool,
    pending_shortcuts: std::collections::VecDeque<ShortcutRequest>,
    shortcut_installs: controller::shortcut_install::Pending,
    update: Option<chillhub_native::update::UpdateRelease>,
    offline: bool,
}
impl AppState {
    fn new(
        services: Services,
        prefs: AppConfig,
        data_root: PathBuf,
        art_root: PathBuf,
        seed: Vec<Game>,
        shortcut: Option<ShortcutRequest>,
    ) -> Self {
        Self {
            visible: vec![],
            selected: 0,
            displayed_game_id: None,
            catalog: vec![],
            records: vec![],
            seed,
            data_root,
            art_root,
            icons: vec![],
            choices: HashMap::new(),
            launching: Default::default(),
            query: String::new(),
            mod_query: String::new(),
            mod_category: "Все".into(),
            generation: 0,
            catalog_generation: 0,
            gallery_generation: 0,
            gallery_index: 0,
            video: None,
            native_hwnd: 0,
            viewer_restore: None,
            viewer_previous_volume: None,
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
            defer_startup_changelog: false,
            pending_shortcuts: Default::default(),
            shortcut_installs: Default::default(),
            update: None,
            offline: false,
        }
    }
}
struct ViewerRestore {
    size: slint::winit_030::winit::dpi::PhysicalSize<u32>,
    position: Option<slint::winit_030::winit::dpi::PhysicalPosition<i32>>,
    maximized: bool,
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
fn roots(exe: &Path) -> Result<(PathBuf, PathBuf), String> {
    if let Some(parent) = exe.parent()
        && parent.join("Data/games.json").is_file()
    {
        return Ok((parent.to_owned(), parent.join("art")));
    }
    #[cfg(feature = "benchmark")]
    {
        let project = Path::new(env!("CARGO_MANIFEST_DIR"));
        Ok((project.to_owned(), project.join("art")))
    }
    #[cfg(not(feature = "benchmark"))]
    Err("Не найдены файлы лаунчера. Переустановите Chill Hub.".into())
}
fn refresh_library(ui: &AppWindow, state: &State) {
    let mut s = state.borrow_mut();
    let visible = filtered_indices(&s.catalog, &s.query);
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
                status_progress: 0.,
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
        "" | "settings" | "feedback" | "confirm" | "update" | "changelog"
    ) {
        close_panel(ui, state);
    }
    if state.borrow().selected == index && state.borrow().generation != 0 {
        refresh_library(ui, state);
        return;
    }
    let (game, art, data, generation, services, game_changed) = {
        let mut s = state.borrow_mut();
        if index >= s.catalog.len() {
            return;
        }
        let game = s.catalog[index].clone();
        let game_changed = s.displayed_game_id.as_deref() != Some(game.id.as_str());
        if game_changed {
            s.displayed_game_id = Some(game.id.clone());
        }
        s.selected = index;

        s.generation += 1;
        (
            game,
            s.art_root.clone(),
            s.data_root.clone(),
            s.generation,
            s.services.clone(),
            game_changed,
        )
    };
    if game_changed {
        ui.set_hero(image_loader::card_preview(&game.id, "hero"));
    }
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
                    poster: media
                        .optimized_index
                        .map(|index| image_loader::card_preview(&game.id, &format!("tile-{index}")))
                        .unwrap_or_default(),
                    caption: media.caption.clone().into(),
                    is_video: media.kind == "video",
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
                let pixels = if bundled && hero_source.ends_with("/hero.jpg") {
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
                ui.set_hero(from_pixels(pixels));
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
    {
        let mut s = state.borrow_mut();
        s.generation += 1;
        s.choice_generation = s
            .choice_epoch
            .fetch_add(1, std::sync::atomic::Ordering::SeqCst)
            + 1;
        s.option = None;
        s.selected = usize::MAX;
        s.displayed_game_id = None;
    }
    if matches!(ui.get_panel_kind().as_str(), "gallery" | "mods" | "manage") {
        close_panel(ui, state);
    }
    ui.set_game_title("".into());
    ui.set_description("".into());
    ui.set_genre("".into());
    ui.set_hero(Image::default());
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
    set_viewer_fullscreen(ui, state, false);
    ui.set_viewer_fullscreen(false);
    {
        let mut s = state.borrow_mut();
        s.gallery_generation += 1;
        s.video_timer.stop();
        if let Some(video) = &mut s.video {
            video.close();
        }
    }
    ui.set_selected_media_is_video(false);
    ui.set_gallery_image_state("ready".into());
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
            "Антивирус".into(),
            "Помощь с установкой и запуском".into(),
            "".into(),
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
                    changes: strings(r.items),
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
    if kind == "downloads" {
        controller::refresh_queue_rows(ui, state);
    }
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
fn set_viewer_fullscreen(ui: &AppWindow, state: &State, enabled: bool) {
    use slint::winit_030::{WinitWindowAccessor, winit};
    if enabled {
        if state.borrow().viewer_restore.is_some() {
            return;
        }
        controller::save_window(ui, state);
        ui.window().with_winit_window(|window| {
            let restore = ViewerRestore {
                size: window.inner_size(),
                position: window.outer_position().ok(),
                maximized: window.is_maximized(),
            };
            window.set_fullscreen(Some(winit::window::Fullscreen::Borderless(
                window.current_monitor(),
            )));
            window.focus_window();
            state.borrow_mut().viewer_restore = Some(restore);
        });
    } else {
        let restore = state.borrow_mut().viewer_restore.take();
        let Some(restore) = restore else {
            return;
        };
        ui.window().with_winit_window(|window| {
            window.set_fullscreen(None);
            if restore.maximized {
                window.set_maximized(true);
            } else {
                window.set_maximized(false);
                let _ = window.request_inner_size(restore.size);
                if let Some(position) = restore.position {
                    window.set_outer_position(position);
                }
            }
            window.focus_window();
        });
    }
}
fn open_media(ui: &AppWindow, state: &State, index: usize) {
    use slint::winit_030::WinitWindowAccessor;
    // Ignore a stale click from a replaced media model before closing the
    // current panel or changing the selected slide.
    let (game_id, title, media, data, services) = {
        let s = state.borrow();
        let Some(game) = s.catalog.get(s.selected) else {
            return;
        };
        let Some(media) = game.media.get(index) else {
            return;
        };
        (
            game.id.clone(),
            game.title.clone(),
            media.clone(),
            s.data_root.clone(),
            s.services.clone(),
        )
    };
    ui.set_viewer_quality_open(false);
    if ui.get_gallery_open() {
        let mut s = state.borrow_mut();
        s.gallery_generation += 1;
        s.video_timer.stop();
        if let Some(video) = &mut s.video {
            video.close();
        }
    } else {
        close_panel(ui, state);
    }
    let generation = {
        let mut s = state.borrow_mut();
        s.gallery_index = index;
        s.gallery_generation
    };
    if media.kind != "video" {
        ui.set_gallery_image(Image::default());
        ui.set_gallery_image_state("loading".into());
    } else {
        ui.set_video_position(0.0);
        ui.set_video_playing(false);
        ui.set_video_failed(false);
        ui.set_video_duration(0.0);
        ui.set_video_time_label("Загрузка видео…".into());
    }
    ui.set_panel_title(title.into());
    ui.set_panel_subtitle(media.caption.clone().into());
    ui.set_gallery_caption(media.caption.clone().into());
    ui.set_gallery_index(index as i32);
    ui.set_panel_kind("gallery".into());
    ui.set_viewer_photo_zoom(1.0);
    ui.set_selected_media_is_video(media.kind == "video");
    let mut qualities: Vec<i32> = media.variants.iter().map(|v| v.height as i32).collect();
    qualities.sort_unstable();
    qualities.dedup();
    let preferred = ui.get_video_quality();
    let quality = qualities
        .iter()
        .rev()
        .find(|&&h| h <= preferred)
        .copied()
        .or_else(|| qualities.last().copied())
        .unwrap_or(0);
    ui.set_video_quality(quality);
    ui.set_video_qualities(ModelRc::new(VecModel::from(qualities)));
    let address = if media.kind == "video" {
        media
            .variants
            .iter()
            .find(|v| v.height == quality as u32)
            .map(|v| &v.url)
            .unwrap_or(&media.url)
    } else {
        &media.path
    };
    let location = if media.kind == "video" {
        chillhub_native::media::video_location(address, &services.api)
    } else {
        chillhub_native::media::location(address, &data, &services.api)
    };
    let path = match location {
        Ok(p) => PathBuf::from(p),
        Err(error) => {
            if media.kind == "video" {
                ui.set_video_failed(true);
                ui.set_video_time_label("Ошибка видео".into());
            } else {
                ui.set_gallery_image_state("error".into());
            }
            toast(ui, state, &error);
            return;
        }
    };
    if media.kind == "video" {
        ui.set_gallery_image_state("ready".into());
        ui.set_gallery_image(Image::default());
        services.api.event(
            "media.trailer_open",
            serde_json::json!({"game": game_id, "index": index}),
        );
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
                    .and_then(|video| {
                        video.open(&path, rect)?;
                        video.set_volume(if ui.get_video_muted() {
                            0.0
                        } else {
                            ui.get_video_volume() as f64 * 100.0
                        });
                        Ok(())
                    });
                if let Err(error) = result {
                    ui.set_video_failed(true);
                    ui.set_video_time_label("Ошибка видео".into());
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
                            let actions = state
                                .borrow_mut()
                                .video
                                .as_mut()
                                .map(VideoPlayer::take_actions)
                                .unwrap_or_default();
                            for action in actions {
                                match action {
                                    VideoAction::TogglePause => {
                                        if let Some(video) = state.borrow().video.as_ref() {
                                            video.set_paused(video.status().is_playing());
                                        }
                                        ui.window()
                                            .with_winit_window(|window| window.focus_window());
                                    }
                                    VideoAction::ToggleFullscreen => {
                                        ui.set_viewer_fullscreen(!ui.get_viewer_fullscreen());
                                        ui.window()
                                            .with_winit_window(|window| window.focus_window());
                                    }
                                }
                            }
                            let status = state.borrow().video.as_ref().map(VideoPlayer::status);
                            if let Some(status) = status {
                                ui.set_video_position(status.position as f32);
                                ui.set_video_duration(status.duration as f32);
                                ui.set_video_volume((status.volume / 100.0) as f32);
                                ui.set_video_muted(status.volume <= 0.001);
                                ui.set_video_playing(status.is_playing());
                                ui.set_video_failed(status.error.is_some());
                                let label = if status.error.is_some() {
                                    "Ошибка видео".into()
                                } else if status.ready {
                                    format!(
                                        "{} / {}",
                                        time_label(status.position),
                                        time_label(status.duration)
                                    )
                                } else {
                                    "Загрузка видео…".into()
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
                            && state.borrow().gallery_generation == generation
                        {
                            match pixels {
                                Ok(pixels) => {
                                    ui.set_gallery_image_aspect(
                                        pixels.width() as f32 / pixels.height().max(1) as f32,
                                    );
                                    ui.set_gallery_image(from_pixels(pixels));
                                    ui.set_gallery_image_state("ready".into());
                                }
                                Err(_) => {
                                    ui.set_gallery_image_state("error".into());
                                    toast(
                                        &ui,
                                        state,
                                        "Не удалось загрузить изображение. Проверьте подключение.",
                                    );
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
        if active.is_none() && let Ok(Some(command)) = bench.poll() {
                let _ = bench.preparing(&command, &WindowInfo::from_hwnd(hwnd));
                if command.phase == Phase::Close {
                    close_panel(&ui, &state); let _ = bench.running(&command, &WindowInfo::from_hwnd(hwnd), serde_json::Value::Null);
                    let _ = bench.complete(&command, &WindowInfo::from_hwnd(hwnd), serde_json::Value::Null); let _ = slint::quit_event_loop(); return;
                }
                ui.window().set_minimized(false);
                ui.window().with_winit_window(|window| window.focus_window());
                match command.phase {
                    Phase::Idle | Phase::Switch | Phase::Warmup => {
                        state.borrow_mut().query.clear();
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
        if current.command.phase == Phase::Switch && current.clicks < current.command.count && elapsed.as_millis() >= current.clicks as u128 * current.command.interval_ms as u128 {
                let id = &current.command.game_ids[current.clicks % current.command.game_ids.len()];
                let index = state.borrow().catalog.iter().position(|g| &g.id == id);
                if let Some(index) = index { select_game(&ui, &state, index); current.clicks += 1; }
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
            *choice = switching_source(&game, choice, source.as_str());
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
        if let Ok(index) = usize::try_from(index) {
            open_media(&ui, &state, index);
        }
    });
    handler!(on_media_retry, |ui, state| {
        let index = state.borrow().gallery_index;
        open_media(&ui, &state, index);
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
            (s.gallery_index as i64 + i64::from(delta)).rem_euclid(count as i64) as usize
        };
        open_media(&ui, &state, index);
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
            video.set_paused(video.status().is_playing());
        }
    });
    handler!(on_video_quality_selected, |ui, state, quality| {
        if !ui.get_show_video() || quality == ui.get_video_quality() {
            return;
        }
        let result = {
            let s = state.borrow();
            s.catalog
                .get(s.selected)
                .and_then(|g| g.media.get(s.gallery_index))
                .and_then(|m| m.variants.iter().find(|v| v.height == quality as u32))
                .map(|variant| {
                    let address =
                        chillhub_native::media::video_location(&variant.url, &s.services.api)?;
                    s.video
                        .as_ref()
                        .ok_or_else(|| "Видеоплеер недоступен".to_string())?
                        .switch_source(&address)
                })
        };
        match result {
            Some(Ok(())) => {
                ui.set_video_quality(quality);
                state.borrow().services.api.event(
                    "media.quality_changed",
                    serde_json::json!({"height": quality}),
                );
            }
            Some(Err(error)) => toast(&ui, &state, &error),
            None => {}
        }
    });
    handler!(on_video_seek, |_ui, state, seconds| {
        if let Some(video) = state.borrow().video.as_ref() {
            video.seek(seconds as f64);
        }
    });
    handler!(on_video_volume_changed, |ui, state, volume| {
        if let Some(video) = state.borrow().video.as_ref() {
            video.set_volume(volume as f64 * 100.0);
        }
        ui.set_video_muted(volume <= 0.001);
    });
    handler!(on_video_mute_toggle, |ui, state| {
        let mut s = state.borrow_mut();
        let Some(current) = s.video.as_ref().map(|video| video.status().volume) else {
            return;
        };
        if current > 0.0 {
            s.viewer_previous_volume = Some(current);
            if let Some(video) = s.video.as_ref() {
                video.set_volume(0.0);
            }
            ui.set_video_volume(0.0);
            ui.set_video_muted(true);
        } else {
            let restored = s
                .viewer_previous_volume
                .take()
                .unwrap_or(60.0)
                .clamp(1.0, 100.0);
            if let Some(video) = s.video.as_ref() {
                video.set_volume(restored);
            }
            ui.set_video_volume((restored / 100.0) as f32);
            ui.set_video_muted(false);
        }
    });
    handler!(on_viewer_fullscreen_changed, |ui, state, fullscreen| {
        set_viewer_fullscreen(&ui, &state, fullscreen);
    });
    handler!(on_video_geometry_changed, |ui, state| {
        if let Ok(mut s) = state.try_borrow_mut()
            && let Some(video) = s.video.as_mut()
            && ui.get_show_video()
        {
            video.set_rect(video_rect(&ui));
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
fn option_value<'a>(args: &'a [String], option: &str) -> Result<Option<&'a str>, String> {
    let Some(index) = args.iter().position(|arg| arg == option) else {
        return Ok(None);
    };
    args.get(index + 1)
        .map(String::as_str)
        .filter(|value| !value.is_empty() && !value.starts_with("--"))
        .map(Some)
        .ok_or_else(|| format!("Не задано значение для {option}"))
}

fn renderer_from_args(args: &[String]) -> Result<&str, String> {
    let renderer = option_value(args, "--renderer")?.unwrap_or("software");
    let accepted = matches!(renderer, "femtovg-wgpu" | "software")
        || (cfg!(feature = "benchmark") && renderer == "femtovg");
    if !accepted {
        return Err(format!("Неизвестный способ отрисовки: {renderer}"));
    }
    Ok(renderer)
}

fn preview_directory(args: &[String]) -> Result<Option<&Path>, String> {
    let folder = option_value(args, "--preview-directory")?.map(Path::new);
    if folder.is_some_and(|path| !path.is_absolute()) {
        return Err("Проверочная папка должна иметь полный путь".into());
    }
    Ok(folder)
}

fn run(args: &[String]) -> Result<(), String> {
    let renderer = renderer_from_args(args)?;
    let mut backend = slint::BackendSelector::new()
        .backend_name("winit".into())
        .renderer_name(renderer.into());
    if renderer == "femtovg-wgpu" {
        let mut settings = slint::wgpu_30::WGPUSettings::default();
        settings.device_memory_hints = slint::wgpu_30::wgpu::MemoryHints::Manual {
            suballocated_device_memory_block_size: 4 * 1024 * 1024..8 * 1024 * 1024,
        };
        backend = backend.require_wgpu_30(slint::wgpu_30::WGPUConfiguration::Automatic(settings));
    }
    backend.select().map_err(|e| e.to_string())?;
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let (data_root, art_root) = roots(&exe)?;
    let seed = catalog::load(&data_root.join("Data/games.json")).unwrap_or_default();
    let paths = chillhub_native::paths::AppPaths::discover()?;
    let mut instance = desktop::Instance::acquire(&paths.data_dir, args)?;
    if !instance.primary {
        return Ok(());
    }
    let (services, prefs) = Services::new(paths)?;
    services.api.event(
        "ui.renderer",
        serde_json::json!({"name": renderer, "colorImageSampling": "bilinear", "animationFpsLimit": 60}),
    );
    let _ = PANIC_CONTEXT.set((services.api.clone(), services.paths.data_dir.clone()));
    let shortcut = ShortcutRequest::parse_args(args)?;
    let state = Rc::new(RefCell::new(AppState::new(
        services, prefs, data_root, art_root, seed, shortcut,
    )));
    ACTIVE_STATE.with(|slot| *slot.borrow_mut() = Some(state.clone()));
    let ui = AppWindow::new().map_err(|e| e.to_string())?;
    if std::env::var_os("CHILLHUB_DATA_DIR").is_some() {
        ui.set_window_title("Chill Hub · Проверочная копия".into());
    }
    ui.set_launcher_version(env!("CARGO_PKG_VERSION").into());
    bind(&ui, &state);
    controller::initialize(&ui, &state);
    instance.listen(ui.as_weak())?;
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
    let preview = match preview_directory(&args) {
        Ok(preview) => preview,
        Err(error) => {
            desktop::show_error(&error);
            return;
        }
    };
    if let Some(folder) = preview {
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
    if let Err(error) = run(&args) {
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
