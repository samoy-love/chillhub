use super::{
    background::{Activity, background_for},
    launch_presentation::{self, InstalledCopyFacts, library_copies_label},
    library::sync_library_rows,
};
use crate::{
    AppWindow, State,
    catalog::{default_choice, valid_choice},
    install_ui, toast,
};
use chillhub_native::{
    config::LaunchTarget,
    launch::{self, LaunchOption},
    model::GameRecord,
    service,
};
use std::{collections::HashSet, path::PathBuf, sync::atomic::Ordering};

pub(crate) fn selected(state: &State) -> Option<(GameRecord, LaunchTarget)> {
    let s = state.borrow();
    let game = s.records.get(s.selected)?.clone();
    let visual = s.catalog.get(s.selected)?;
    let choice = s
        .choices
        .get(&game.game_id)
        .cloned()
        .unwrap_or_else(|| default_choice(visual));
    let (source, mode) = valid_choice(visual, &choice.0, &choice.1);
    Some((
        game,
        match (source.as_str(), mode.as_str()) {
            ("steam", "mods") => LaunchTarget::SteamModded,
            ("steam", _) => LaunchTarget::SteamVanilla,
            (_, "mods") => LaunchTarget::LocalModded,
            _ => LaunchTarget::LocalVanilla,
        },
    ))
}

struct ChoiceProbe {
    option: LaunchOption,
    copy: InstalledCopyFacts,
    library_label: (String, String),
}

pub(super) fn running_copies(options: &[LaunchOption]) -> HashSet<PathBuf> {
    options
        .iter()
        .map(|option| option.game_dir.clone())
        .filter(|path| !path.as_os_str().is_empty())
        .collect::<HashSet<_>>()
        .into_iter()
        .filter(|path| chillhub_native::playtime::processes_in(path).is_ok_and(|p| !p.is_empty()))
        .collect()
}

pub(crate) fn refresh_choice(ui: &AppWindow, state: &State) {
    install_ui::clear_hint(ui);
    let (game, target) = match selected(state) {
        Some(v) => v,
        None => {
            ui.set_can_play(false);
            return;
        }
    };
    let (visual, services, generation, epoch) = {
        let mut s = state.borrow_mut();
        s.choice_generation = s.choice_epoch.fetch_add(1, Ordering::SeqCst) + 1;
        s.prefs.remember(&game.game_id, target);
        s.prefs.last_game_id = game.game_id.clone();
        (
            s.catalog[s.selected].clone(),
            s.services.clone(),
            s.choice_generation,
            s.choice_epoch.clone(),
        )
    };
    ui.set_source(if target.via_steam() { "steam" } else { "local" }.into());
    ui.set_mode(if target.modded() { "mods" } else { "plain" }.into());
    ui.set_supports_steam(visual.steam_supported);
    ui.set_supports_local(visual.local_supported);
    ui.set_has_mods(visual.has_mods);
    ui.set_busy(true);
    ui.set_can_play(false);
    ui.set_can_verify_game(false);
    ui.set_can_verify_mods(false);
    ui.set_has_game_files(false);
    ui.set_can_create_shortcut(false);
    ui.set_play_label("Проверяем…".into());
    ui.set_status("Проверяем установленную копию…".into());
    services.api.event(
        "launch.choice",
        serde_json::json!({"game": game.game_id, "target": target}),
    );
    background_for(
        ui,
        Activity::Choice,
        move || {
            let result = launch::options(&game, &services.paths).and_then(|options| {
                let running = running_copies(&options);
                let library_label = library_copies_label(&options, target, !running.is_empty());
                let option = options
                    .into_iter()
                    .find(|option| option.target == target)
                    .ok_or_else(|| "Этот вариант игры недоступен".to_owned())?;
                // A missing Steam installation has no root. It must not be
                // interpreted as the current working directory by file probes.
                let has_root = !option.game_dir.as_os_str().is_empty();
                let version_marker = if has_root {
                    launch::read_marker(&option.game_dir, ".version")
                } else {
                    String::new()
                };
                let has_executable = has_root
                    && launch::resolve_game_exe(
                        &option.game_dir,
                        &game.exe_relative_path,
                        game.mods
                            .as_ref()
                            .map(|mods| mods.exe_names.as_slice())
                            .unwrap_or_default(),
                    )
                    .is_ok();
                let copy = InstalledCopyFacts {
                    can_verify_game: has_root
                        && !target.via_steam()
                        && service::can_verify_game(&game, &option.game_dir),
                    can_verify_mods: has_root && service::can_verify_mods(&option.game_dir),
                    has_game_files: has_root && option.game_dir.is_dir(),
                    has_executable,
                    running: running.contains(&option.game_dir),
                    version_marker,
                    playtime_seconds: services
                        .playtime
                        .totals(&game.game_id)
                        .unwrap_or_default()
                        .total_seconds,
                    queued: has_root && services.queue.has_pending_for_root(&option.game_dir),
                };
                Ok(ChoiceProbe {
                    option,
                    copy,
                    library_label,
                })
            });
            let saved = services.config.update(|c| {
                if epoch.load(Ordering::SeqCst) == generation {
                    c.remember(&game.game_id, target);
                    c.last_game_id = game.game_id.clone();
                }
            });
            (result, saved.err())
        },
        move |ui, state, (result, save_error)| {
            if state.borrow().choice_generation != generation {
                return;
            }
            ui.set_busy(false);
            match result {
                Ok(ChoiceProbe {
                    option,
                    copy,
                    library_label,
                }) => {
                    let launching = selected(state).is_some_and(|(game, target)| {
                        state
                            .borrow()
                            .launching
                            .contains(&(game.game_id, target.via_steam()))
                    });
                    let presentation = launch_presentation::present(
                        &option,
                        &copy,
                        &state.borrow().maintenance,
                        launching,
                    );
                    ui.set_can_verify_game(presentation.can_verify_game);
                    ui.set_can_verify_mods(presentation.can_verify_mods);
                    ui.set_has_game_files(presentation.has_game_files);
                    ui.set_can_create_shortcut(presentation.can_create_shortcut);
                    ui.set_can_play(presentation.can_play);
                    ui.set_ready_to_play(presentation.ready_to_play);
                    ui.set_play_label(presentation.play_label.into());
                    ui.set_status(presentation.status.into());
                    ui.set_installed_version(presentation.installed_version.into());
                    ui.set_playtime_label(presentation.playtime_label.into());
                    let id = state
                        .borrow()
                        .records
                        .get(state.borrow().selected)
                        .map(|g| g.game_id.clone());
                    if let Some(id) = id {
                        state.borrow_mut().library_status.insert(id, library_label);
                    }
                    state.borrow_mut().option = Some(option);
                    sync_library_rows(ui, state);
                    install_ui::hint(ui, state);
                }
                Err(error) => {
                    ui.set_can_play(false);
                    ui.set_play_label("Недоступно".into());
                    ui.set_status(error.into());
                    state.borrow_mut().option = None;
                }
            }
            if let Some(error) = save_error {
                toast(ui, state, &format!("Не удалось сохранить выбор: {error}"));
            }
        },
    );
}
