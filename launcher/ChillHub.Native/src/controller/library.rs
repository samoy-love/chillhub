use super::{
    background::background,
    choice::{refresh_choice, running_copies},
    downloads,
    launch_presentation::library_copies_label,
};
use crate::{AppWindow, State, queue_ui};
use chillhub_native::{config::LaunchTarget, launch, service};
use slint::Model;
use std::{
    collections::HashMap,
    sync::atomic::{AtomicU64, Ordering},
};

static LIBRARY_STATE_EPOCH: AtomicU64 = AtomicU64::new(0);

pub(super) fn refresh_library_states(ui: &AppWindow, state: &State) {
    let epoch = LIBRARY_STATE_EPOCH.fetch_add(1, Ordering::SeqCst) + 1;
    let s = state.borrow();
    let services = s.services.clone();
    let records = s.records.clone();
    let prefs = s.prefs.clone();
    let shortcut_launches = s.shortcut_installs.launching.clone();
    let generation = s.catalog_generation;
    drop(s);
    background(
        ui,
        move || {
            records
                .into_iter()
                .map(|g| {
                    if !shortcut_launches.contains(&g.game_id)
                        && let Err(error) = services.verify_game_status(&g)
                    {
                        chillhub_native::diagnostics::log(
                            &services.paths.data_dir,
                            "library-status",
                            &error,
                        );
                    }
                    let target = prefs.remembered(&g.game_id).unwrap_or_else(|| {
                        if g.has_latest && !g.latest_version.is_empty() {
                            if g.mods.as_ref().is_some_and(|mods| mods.has_latest) {
                                LaunchTarget::LocalModded
                            } else {
                                LaunchTarget::LocalVanilla
                            }
                        } else if g
                            .mods
                            .as_ref()
                            .is_some_and(|mods| !mods.steam_app_id.is_empty())
                        {
                            LaunchTarget::SteamVanilla
                        } else {
                            LaunchTarget::LocalVanilla
                        }
                    });
                    let label = launch::options(&g, &services.paths)
                        .map(|options| {
                            let running = running_copies(&options);
                            library_copies_label(&options, target, !running.is_empty())
                        })
                        .unwrap_or(("Не удалось проверить".into(), "error".into()));
                    (g.game_id, label)
                })
                .collect::<HashMap<_, _>>()
        },
        move |ui, state, labels| {
            if LIBRARY_STATE_EPOCH.load(Ordering::SeqCst) != epoch
                || state.borrow().catalog_generation != generation
            {
                return;
            }
            state.borrow_mut().library_status = labels;
            sync_library_rows(ui, state);
            if !ui.get_busy() && !ui.get_operation_busy() {
                refresh_choice(ui, state);
            }
        },
    );
}

pub(crate) fn sync_library_rows(ui: &AppWindow, state: &State) {
    let model = ui.get_games();
    let s = state.borrow();
    for index in 0..model.row_count() {
        let Some(mut row) = model.row_data(index) else {
            continue;
        };
        let mut label = s
            .library_status
            .get(row.id.as_str())
            .cloned()
            .unwrap_or_default();
        let queue = downloads::library_task(&s.tasks, row.id.as_str());
        let progress = queue.and_then(queue_ui::stage_progress).unwrap_or(0.);
        if let Some(task) = queue {
            let (text, kind) = downloads::library_status(task);
            label = (text.into(), kind.into());
            if let Some(progress) = queue_ui::stage_progress(task) {
                label.0 = format!("{} · {}%", label.0, (progress * 100.).floor());
            }
        }
        if row.status.as_str() != label.0
            || row.status_kind.as_str() != label.1
            || row.status_progress != progress
        {
            row.status = label.0.into();
            row.status_kind = label.1.into();
            row.status_progress = progress;
            model.set_row_data(index, row);
        }
    }
}

pub(super) fn refresh_disk(ui: &AppWindow, state: &State) {
    let path = state.borrow().services.paths.games_dir.clone();
    let expected_path = path.clone();
    background(
        ui,
        move || service::available_space(&path),
        move |ui, state, space| {
            if state.borrow().services.paths.games_dir == expected_path {
                set_disk_label(ui, space);
            }
        },
    );
}

pub(crate) fn set_disk_label(ui: &AppWindow, available_bytes: Option<u64>) {
    ui.set_disk_label(service::format_available_space(available_bytes).into());
}
