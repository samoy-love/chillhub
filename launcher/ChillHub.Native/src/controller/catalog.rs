use super::{
    background::{Activity, background, background_for},
    downloads::refresh_queue_rows,
    library::refresh_library_states,
    shortcut_install,
    shortcuts::{offer_shortcut, take_catalog_shortcut},
};
use crate::{
    ACTIVE_STATE, AppWindow, State,
    catalog::{self, Game},
    clear_game,
    image_loader::{from_pixels, source_image_pixels},
    install_ui, open_panel, orphan_ui, refresh_library, security_offers, select_game,
};
use chillhub_native::config::LaunchTarget;
use slint::{ComponentHandle, Image, Model};
use std::{
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

static ICON_EPOCH: AtomicU64 = AtomicU64::new(0);

pub(super) fn reload(ui: &AppWindow, state: &State) {
    state.borrow_mut().catalog_checked_at = std::time::Instant::now();
    install_ui::clear_hint(ui);
    ui.set_loading(true);
    ui.set_load_error("".into());
    ui.set_can_play(false);
    let (services, seed, generation) = {
        let mut s = state.borrow_mut();
        s.catalog_generation += 1;
        (s.services.clone(), s.seed.clone(), s.catalog_generation)
    };
    let cached_ui = ui.as_weak();
    background_for(
        ui,
        Activity::Catalog,
        move || {
            let cache_dir = services.paths.data_dir.join("cache");
            if let Some(snapshot) = services.api.cached_catalog(&cache_dir) {
                let games = catalog::from_live(&snapshot.library.items, &seed);
                let _ = slint::invoke_from_event_loop(move || {
                    if let Some(ui) = cached_ui.upgrade() {
                        ACTIVE_STATE.with(|slot| {
                            if let Some(state) = slot.borrow().as_ref() {
                                // An older snapshot may not include a newly added game.
                                let use_cached =
                                    state.borrow().shortcut.as_ref().is_none_or(|request| {
                                        snapshot
                                            .library
                                            .items
                                            .iter()
                                            .any(|game| game.game_id == request.game_id)
                                    });
                                if use_cached {
                                    apply_catalog(&ui, state, generation, Ok((snapshot, games)));
                                }
                            }
                        });
                    }
                });
            }
            let result = services.api.catalog(&services.paths.data_dir.join("cache"));
            result.map(|snapshot| {
                let games = catalog::from_live(&snapshot.library.items, &seed);
                (snapshot, games)
            })
        },
        move |ui, state, result| apply_catalog(ui, state, generation, result),
    );
}

pub(crate) fn apply_catalog(
    ui: &AppWindow,
    state: &State,
    generation: u64,
    result: Result<
        (chillhub_native::net::CatalogSnapshot, Vec<Game>),
        chillhub_native::net::ApiError,
    >,
) {
    if state.borrow().catalog_generation != generation {
        return;
    }
    install_ui::clear_hint(ui);
    ui.set_loading(false);
    let mut shortcut = take_catalog_shortcut(ui, state);
    if shortcut.is_some() {
        state.borrow_mut().defer_startup_changelog = true;
    }
    match result {
        Err(error) => {
            ui.set_load_error(error.to_string().into());
            ui.set_can_play(false);
            if let Some(request) = shortcut {
                offer_shortcut(ui, state, request);
            }
        }
        Ok((snapshot, games)) => {
            if let Some(request) = &mut shortcut
                && let Some(game) = snapshot
                    .library
                    .items
                    .iter()
                    .find(|game| game.game_id.eq_ignore_ascii_case(&request.game_id))
            {
                request.game_id.clone_from(&game.game_id);
            }
            let index = {
                let mut s = state.borrow_mut();
                s.offline = snapshot.offline;
                s.records = snapshot.library.items;
                s.catalog = games;
                s.icons = vec![Image::default(); s.catalog.len()];
                s.visible.clear();
                s.generation += 1;
                s.selected = usize::MAX;
                let remembered = s.prefs.launch_targets.clone();
                for (id, target) in remembered {
                    if let Some(t) = LaunchTarget::parse(&target) {
                        s.choices.entry(id).or_insert_with(|| {
                            (
                                if t.via_steam() { "steam" } else { "local" }.into(),
                                if t.modded() { "mods" } else { "plain" }.into(),
                            )
                        });
                    }
                }
                let preferred = shortcut
                    .as_ref()
                    .map(|s| s.game_id.as_str())
                    .unwrap_or(&s.prefs.last_game_id);
                let index = s
                    .catalog
                    .iter()
                    .position(|g| g.id == preferred)
                    .unwrap_or(0);
                if let Some(ref request) = shortcut
                    && let Some(t) = request.target
                {
                    s.choices.insert(
                        request.game_id.clone(),
                        (
                            if t.via_steam() { "steam" } else { "local" }.into(),
                            if t.modded() { "mods" } else { "plain" }.into(),
                        ),
                    );
                }
                index
            };
            ui.set_connection_note(snapshot.warning.unwrap_or_default().into());
            refresh_library(ui, state);
            load_icons(ui, state, generation);
            refresh_library_states(ui, state);
            if state.borrow().catalog.is_empty() {
                clear_game(ui, state);
                ui.set_load_error("В библиотеке пока нет опубликованных игр".into());
                ui.set_can_play(false);
            } else {
                select_game(ui, state, index);
            }
            if let Some(request) = shortcut {
                if state
                    .borrow()
                    .records
                    .iter()
                    .any(|g| g.game_id == request.game_id)
                {
                    // A game shortcut selects its card. A missing local copy gets
                    // an explicit install-and-play offer, never a silent download.
                    shortcut_install::offer(ui, state, &request);
                } else {
                    offer_shortcut(ui, state, request);
                }
            } else if ui.get_panel_kind().is_empty()
                && !ui.get_operation_busy()
                && !state.borrow().defer_startup_changelog
                && state.borrow().pending_shortcuts.is_empty()
                && chillhub_native::changelog::should_show(
                    &state.borrow().prefs.last_seen_changelog_version,
                    env!("CARGO_PKG_VERSION"),
                )
            {
                open_panel(ui, state, "changelog");
            }
            security_offers::catalog_ready(ui, state);
            if snapshot.all_entries_valid {
                orphan_ui::catalog_ready(ui, state);
            }
        }
    }
}

pub(super) fn refresh_catalog_quietly(ui: &AppWindow, state: &State) {
    if !ui.window().is_visible()
        || ui.window().is_minimized()
        || ui.get_loading()
        || ui.get_operation_busy()
        || !ui.get_panel_kind().is_empty()
    {
        return;
    }
    let mut s = state.borrow_mut();
    if s.catalog_poll_inflight || s.catalog_checked_at.elapsed() < Duration::from_secs(300) {
        return;
    }
    s.catalog_poll_inflight = true;
    s.catalog_checked_at = std::time::Instant::now();
    let services = s.services.clone();
    let seed = s.seed.clone();
    let generation = s.catalog_generation;
    let current = serde_json::to_vec(&s.records).ok();
    drop(s);
    background(
        ui,
        move || {
            services
                .api
                .catalog(&services.paths.data_dir.join("cache"))
                .map(|snapshot| {
                    let changed = serde_json::to_vec(&snapshot.library.items).ok() != current;
                    let games = if changed {
                        catalog::from_live(&snapshot.library.items, &seed)
                    } else {
                        Vec::new()
                    };
                    (snapshot, games, changed)
                })
        },
        move |ui, state, result| {
            state.borrow_mut().catalog_poll_inflight = false;
            if state.borrow().catalog_generation != generation {
                return;
            }
            match result {
                Ok((snapshot, games, true))
                    if ui.get_panel_kind().is_empty() && !ui.get_operation_busy() =>
                {
                    apply_catalog(ui, state, generation, Ok((snapshot, games)))
                }
                Ok((snapshot, _, _)) => {
                    state.borrow_mut().offline = snapshot.offline;
                    ui.set_connection_note(snapshot.warning.unwrap_or_default().into());
                    refresh_library_states(ui, state);
                }
                Err(e) => ui.set_connection_note(e.to_string().into()),
            }
        },
    );
}

fn load_icons(ui: &AppWindow, state: &State, generation: u64) {
    let icon_epoch = ICON_EPOCH.fetch_add(1, Ordering::SeqCst) + 1;
    let s = state.borrow();
    let services = s.services.clone();
    let bundle = s.data_root.clone();
    let games = s.catalog.clone();
    drop(s);
    let weak = ui.as_weak();
    std::thread::spawn(move || {
        let cache = services.paths.data_dir.join("cache/media");
        for remote in [false, true] {
            for (index, game) in games.iter().enumerate() {
                if ICON_EPOCH.load(Ordering::SeqCst) != icon_epoch {
                    return;
                }
                let value = if remote || game.fallback_icon.is_empty() {
                    &game.icon
                } else {
                    &game.fallback_icon
                };
                let bundled = value.starts_with("Assets/") || value.starts_with("art/");
                if remote == bundled {
                    continue;
                }
                let result =
                    source_image_pixels(value, &bundle, &cache, &services.api, 64, 64, None);
                let pixels = match result {
                    Ok(pixels) => pixels,
                    Err(error) => {
                        services.api.event(
                            "image.icon_error",
                            serde_json::json!({
                                "game": game.id, "error": error, "remote": remote,
                                "bundledFallback": !game.fallback_icon.is_empty()
                            }),
                        );
                        continue;
                    }
                };
                let weak = weak.clone();
                let _ = slint::invoke_from_event_loop(move || {
                    if let Some(ui) = weak.upgrade() {
                        ACTIVE_STATE.with(|slot| {
                            if let Some(state) = slot.borrow().as_ref() {
                                let mut s = state.borrow_mut();
                                if s.catalog_generation != generation
                                    || ICON_EPOCH.load(Ordering::SeqCst) != icon_epoch
                                {
                                    return;
                                }
                                let image = from_pixels(pixels);
                                if let Some(icon) = s.icons.get_mut(index) {
                                    *icon = image.clone();
                                }
                                let visible_index = s.visible.iter().position(|&i| i == index);
                                drop(s);
                                if let Some(visible_index) = visible_index {
                                    let model = ui.get_games();
                                    if let Some(mut row) = model.row_data(visible_index) {
                                        row.icon = image;
                                        model.set_row_data(visible_index, row);
                                    }
                                }
                                if ui.get_panel_kind() == "downloads" {
                                    refresh_queue_rows(&ui, state);
                                }
                            }
                        });
                    }
                });
            }
        }
        let _ = chillhub_native::media::trim_cache(&cache);
    });
}
