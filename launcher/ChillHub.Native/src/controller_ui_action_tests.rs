use super::*;
use chillhub_native::transfer::SyncScope;

pub(super) fn start_failed_launch(ui: &AppWindow, state: &State) -> Result<(), String> {
    ui.invoke_play_requested();
    if !state
        .borrow()
        .launching
        .contains(&("fixture".into(), false))
        || ui.get_play_label() != "Запускаем…"
        || ui.get_operation_busy()
        || ui.get_can_play()
    {
        return Err("launch did not reserve only the selected copy".into());
    }
    Ok(())
}

pub(super) fn check_failed_launch(ui: &AppWindow, state: &State) -> Result<(), String> {
    let s = state.borrow();
    let config = s
        .services
        .paths
        .game_root("fixture")?
        .join("doorstop_config.ini");
    if !ui.get_toast().contains("Не удалось запустить игру")
        || !ui.get_can_play()
        || ui.get_play_label() != "Играть"
        || s.tasks
            .iter()
            .any(|item| item.state != QueueState::Completed)
        || !s.services.playtime.pending()?.is_empty()
        || !fs::read_to_string(config)
            .map_err(|error| error.to_string())?
            .contains("enabled=false")
    {
        return Err(format!(
            "failed game launch left an active launch or mod loader: {}",
            ui.get_toast()
        ));
    }
    Ok(())
}

fn apply_fixture_catalog(ui: &AppWindow, state: &State, offline: bool) {
    let (generation, records, games) = {
        let s = state.borrow();
        (s.catalog_generation, s.records.clone(), s.catalog.clone())
    };
    controller::apply_catalog(
        ui,
        state,
        generation,
        Ok((
            chillhub_native::net::CatalogSnapshot {
                library: LibraryResponse { items: records },
                offline,
                all_entries_valid: !offline,
                fetched_at: String::new(),
                warning: None,
            },
            games,
        )),
    );
}

pub(super) fn start_shortcut_during_catalog_refresh(
    ui: &AppWindow,
    state: &State,
) -> Result<(), String> {
    close_panel(ui, state);
    ui.set_toast("".into());
    let mut s = state.borrow_mut();
    s.toast_timer.stop();
    let seen_version = std::mem::take(&mut s.prefs.last_seen_changelog_version);
    s.shortcut = Some(ShortcutRequest {
        game_id: "FIXTURE".into(),
        target: Some(LaunchTarget::LocalModded),
        ..Default::default()
    });
    drop(s);
    apply_fixture_catalog(ui, state, true);
    if ui.get_operation_busy() || ui.get_game_title() != "Проверочная игра" {
        return Err(
            "the cached catalog did not select its installed shortcut without launching".into(),
        );
    }
    apply_fixture_catalog(ui, state, false);
    state.borrow_mut().prefs.last_seen_changelog_version = seen_version;
    if ui.get_panel_kind() == "changelog" || ui.get_operation_busy() {
        return Err(
            "fresh catalog interrupted shortcut navigation with an unrelated action".into(),
        );
    }
    Ok(())
}

pub(super) fn check_shortcut_launch_finished(ui: &AppWindow, state: &State) -> Result<(), String> {
    let selected = controller::selected(state);
    let s = state.borrow();
    if ui.get_panel_kind() == "changelog"
        || !ui.get_can_play()
        || !ui.get_toast().is_empty()
        || !matches!(selected, Some((game, LaunchTarget::LocalModded)) if game.game_id == "fixture")
        || s.tasks
            .iter()
            .any(|item| item.state != QueueState::Completed)
        || !s.services.playtime.pending()?.is_empty()
    {
        return Err(format!(
            "shortcut launch did not recover its selected copy: {}",
            ui.get_toast()
        ));
    }
    Ok(())
}

pub(super) fn check_catalog_does_not_preempt_shortcut_batch(
    ui: &AppWindow,
    state: &State,
) -> Result<(), String> {
    let seen_version = std::mem::take(&mut state.borrow_mut().prefs.last_seen_changelog_version);
    apply_fixture_catalog(ui, state, false);
    state.borrow_mut().prefs.last_seen_changelog_version = seen_version;
    if ui.get_panel_kind() == "changelog" || state.borrow().pending_shortcuts.len() != 2 {
        return Err("catalog reload preempted or dropped pending shortcuts".into());
    }
    Ok(())
}

pub(super) fn check_unavailable_actions(ui: &AppWindow, state: &State) {
    let data_dir = state.borrow().services.paths.data_dir.clone();
    assert!(chillhub_native::diagnostics::existing_log_directory(&data_dir).is_none());
    ui.invoke_action_requested("open-logs".into());
    assert_eq!(ui.get_toast(), "Журнал недоступен или отключён.");

    ui.set_panel_kind("manage".into());
    ui.set_settings_path("folder fixture".into());
    ui.invoke_action_requested("choose-games-folder".into());
    assert!(!ui.get_operation_busy());
    assert_eq!(ui.get_panel_kind(), "manage");
    assert_eq!(ui.get_settings_path(), "folder fixture");

    ui.set_panel_kind("settings".into());
    ui.set_operation_busy(true);
    ui.invoke_action_requested("choose-games-folder".into());
    assert!(ui.get_operation_busy());
    assert_eq!(ui.get_settings_path(), "folder fixture");
    assert_eq!(ui.get_toast(), "Журнал недоступен или отключён.");

    ui.set_operation_busy(false);
    ui.invoke_action_requested(" verify-game".into());
    assert_eq!(ui.get_toast(), "Это действие сейчас недоступно");
    assert!(!ui.get_operation_busy());
    assert!(state.borrow().services.queue.snapshot().is_empty());
    state.borrow().toast_timer.stop();
    ui.set_panel_kind("".into());
    ui.set_settings_path("".into());
    ui.set_toast("".into());
}

pub(super) fn start_local_verification(
    ui: &AppWindow,
    state: &State,
) -> Result<Vec<String>, String> {
    let (id, selected, supported, queue) = {
        let s = state.borrow();
        (
            s.records[s.selected].game_id.clone(),
            s.selected,
            s.catalog[s.selected].steam_supported,
            s.services.queue.clone(),
        )
    };
    let previous_ids: Vec<_> = queue.snapshot().into_iter().map(|task| task.id).collect();
    {
        let mut s = state.borrow_mut();
        s.catalog[selected].steam_supported = true;
        s.choices
            .insert(id.clone(), ("steam".into(), "plain".into()));
    }
    ui.invoke_action_requested("verify-game".into());
    {
        let mut s = state.borrow_mut();
        s.catalog[selected].steam_supported = supported;
        s.choices.insert(id, ("local".into(), "plain".into()));
    }
    if ui.get_operation_busy()
        || !ui.get_toast().starts_with("Проверьте файлы игры в Steam:")
        || queue.snapshot().len() != previous_ids.len()
    {
        return Err("Steam verification dispatched a local operation".into());
    }

    ui.invoke_action_requested("verify-game".into());
    if !ui.get_operation_busy() {
        return Err("local verification did not dispatch a queue operation".into());
    }
    Ok(previous_ids)
}

pub(super) fn check_local_queue(
    ui: &AppWindow,
    state: &State,
    previous_ids: &[String],
    verify: bool,
) -> Result<bool, String> {
    if ui.get_operation_busy() {
        return Ok(false);
    }
    let s = state.borrow();
    let root = s.services.paths.game_root("fixture")?;
    let queue = s.services.queue.snapshot();
    let mut added = queue.iter().filter(|task| !previous_ids.contains(&task.id));
    let task = added.next().ok_or_else(|| {
        format!(
            "missing local queue request after action: {}",
            ui.get_toast()
        )
    })?;
    if added.next().is_some()
        || task.request.root != root
        || task.request.game_id != "fixture"
        || task.request.scope != SyncScope::WholeRoot
        || task.request.force_rehash != verify
        || task.request.version != "0.9.0"
    {
        return Err(format!(
            "local action did not enqueue its single expected request: {queue:?}"
        ));
    }
    if task.state == QueueState::Completed {
        Ok(true)
    } else if matches!(task.state, QueueState::Waiting | QueueState::Running) {
        Ok(false)
    } else {
        Err(format!("local queue request did not finish: {task:?}"))
    }
}

pub(super) fn confirm_requested_version(
    ui: &AppWindow,
    state: &State,
) -> Result<Vec<String>, String> {
    let queue = state.borrow().services.queue.clone();
    let previous_ids: Vec<_> = queue.snapshot().into_iter().map(|task| task.id).collect();
    ui.invoke_version_selected("0.9.0".into());
    ui.invoke_action_requested("version-install".into());
    if ui.get_panel_kind() != "confirm"
        || !ui.get_panel_body().contains("0.9.0")
        || !matches!(
            state.borrow().pending.as_ref(),
            Some(controller::Confirmation::Version(game, LaunchTarget::LocalVanilla, version))
                if game.game_id == "fixture" && version == "0.9.0"
        )
    {
        return Err("version action omitted the local version confirmation".into());
    }
    ui.invoke_action_requested("cancel-action".into());
    ui.invoke_action_requested("confirm-action".into());
    if state.borrow().pending.is_some()
        || !ui.get_panel_kind().is_empty()
        || ui.get_operation_busy()
        || queue.snapshot().len() != previous_ids.len()
    {
        return Err("cancelled version confirmation dispatched a queue task".into());
    }

    ui.invoke_action_requested("version-install".into());
    ui.invoke_action_requested("confirm-action".into());
    if state.borrow().pending.is_some() || !ui.get_operation_busy() {
        return Err("version confirmation was not consumed and dispatched".into());
    }
    Ok(previous_ids)
}

pub(super) fn start_modpack_verification(
    ui: &AppWindow,
    state: &State,
) -> Result<Vec<String>, String> {
    let s = state.borrow();
    let root = s.services.paths.game_root("fixture")?;
    let previous_ids = s
        .services
        .queue
        .snapshot()
        .into_iter()
        .map(|task| task.id)
        .collect();
    drop(s);
    fs::write(root.join("winhttp.dll"), b"corrupt loader").map_err(|error| error.to_string())?;
    ui.invoke_action_requested("verify-mods".into());
    if !ui.get_operation_busy() {
        return Err("modpack verification did not dispatch a queue operation".into());
    }
    Ok(previous_ids)
}

pub(super) fn check_modpack_verification(
    ui: &AppWindow,
    state: &State,
    previous_ids: &[String],
) -> Result<bool, String> {
    if ui.get_operation_busy() {
        return Ok(false);
    }
    let s = state.borrow();
    let root = s.services.paths.game_root("fixture")?;
    let queue = s.services.queue.snapshot();
    let mut added = queue.iter().filter(|task| !previous_ids.contains(&task.id));
    let task = added
        .next()
        .ok_or_else(|| format!("missing modpack verification: {}", ui.get_toast()))?;
    if added.next().is_some()
        || task.request.root != root
        || task.request.scope != SyncScope::OwnFilesOnly
        || !task.request.force_rehash
    {
        return Err(format!("verification changed the wrong files: {queue:?}"));
    }
    match task.state {
        QueueState::Waiting | QueueState::Running => Ok(false),
        QueueState::Completed => {
            if fs::read(root.join("winhttp.dll")).map_err(|error| error.to_string())?
                != b"fixture loader; not a DLL"
                || fs::read_to_string(root.join(".version"))
                    .map_err(|error| error.to_string())?
                    .trim()
                    != "0.9.0"
            {
                return Err(
                    "verification did not repair the loader while preserving the base version"
                        .into(),
                );
            }
            Ok(true)
        }
        _ => Err(format!("modpack verification did not finish: {task:?}")),
    }
}

pub(super) fn revert_live_setting(ui: &AppWindow) -> Result<(), String> {
    ui.invoke_panel_requested("settings".into());
    if !ui.get_reduced_motion() {
        return Err("settings fixture lost the saved motion preference".into());
    }
    ui.invoke_setting_changed("reduced-motion".into(), "false".into());
    ui.invoke_setting_changed("reduced-motion".into(), "true".into());
    if ui.get_settings_save_status() != "Сохраняем…" {
        return Err("the reverted preference did not supersede its pending save".into());
    }
    Ok(())
}

pub(super) fn check_reverted_live_setting(ui: &AppWindow, state: &State) -> Result<(), String> {
    let s = state.borrow();
    let saved = s.services.config.load()?;
    if saved.extra.get("ReducedMotion") != Some(&serde_json::Value::Bool(true))
        || s.prefs.extra.get("ReducedMotion") != Some(&serde_json::Value::Bool(true))
        || !ui.get_reduced_motion()
    {
        return Err("the superseded edit changed the saved motion preference".into());
    }
    drop(s);
    ui.invoke_panel_closed();
    Ok(())
}

pub(super) fn request_shortcut_batch(ui: &AppWindow, state: &State) -> Result<(), String> {
    let paths = state.borrow().services.paths.clone();
    let root = paths.game_root("old-two")?;
    ui.set_operation_busy(true);
    for directory in [&root, &paths.data_dir] {
        let file = directory.join("Shortcut.exe");
        fs::write(&file, b"no executable code").map_err(|error| error.to_string())?;
        controller::external_request(
            ui,
            vec![
                "--game=old-two".into(),
                format!("--exe={}", file.display()),
                "--target=LocalVanilla".into(),
            ],
        );
    }
    if ui.get_loading() || state.borrow().pending_shortcuts.len() != 2 {
        return Err("an active operation did not defer both received shortcuts".into());
    }
    ui.set_operation_busy(false);
    Ok(())
}

pub(super) fn confirm_stale_shortcut(ui: &AppWindow, state: &State) -> Result<(), String> {
    let root = state.borrow().services.paths.game_root("old-two")?;
    if !ui.get_panel_body().contains("Shortcut.exe")
        || !matches!(
            state.borrow().pending.as_ref(),
            Some(controller::Confirmation::Shortcut(request)) if request.game_id == "old-two"
        )
    {
        return Err("catalog fallback did not offer the correct installed copy".into());
    }
    fs::write(root.join(".updating"), []).map_err(|error| error.to_string())?;
    ui.invoke_action_requested("confirm-action".into());
    if !ui.get_operation_busy() || state.borrow().pending.is_some() {
        return Err("shortcut confirmation did not dispatch exactly once".into());
    }
    Ok(())
}

pub(super) fn check_stale_shortcut(ui: &AppWindow, state: &State) -> Result<(), String> {
    let root = state.borrow().services.paths.game_root("old-two")?;
    if !ui.get_toast().contains("обновление не завершено")
        || !root.join("Shortcut.exe").is_file()
        || !state.borrow().services.playtime.pending()?.is_empty()
    {
        return Err("shortcut confirmation did not recheck the installed copy".into());
    }
    fs::remove_file(root.join(".updating")).map_err(|error| error.to_string())?;
    Ok(())
}

pub(super) fn delete_shortcut_fixture(ui: &AppWindow, state: &State) -> Result<(), String> {
    ui.invoke_orphan_action("old-two".into(), "delete".into());
    if ui.get_panel_kind() != "confirm" || !ui.get_panel_body().contains("old-two") {
        return Err("single cleanup did not confirm the selected folder".into());
    }
    ui.invoke_action_requested("confirm-action".into());
    if !ui.get_operation_busy() || state.borrow().pending.is_some() {
        return Err("single cleanup confirmation did not dispatch exactly once".into());
    }
    Ok(())
}

pub(super) fn check_shortcut_fixture_deleted(ui: &AppWindow, state: &State) -> Result<(), String> {
    let s = state.borrow();
    if s.services.paths.game_root("old-two")?.exists()
        || !s
            .services
            .paths
            .game_root("fixture")?
            .join("Game.exe")
            .is_file()
        || fs::read(s.services.paths.data_dir.join("Shortcut.exe"))
            .map_err(|error| error.to_string())?
            != b"no executable code"
        || ui.get_toast() != "Локальная копия удалена"
    {
        return Err("single cleanup did not remove only the confirmed game folder".into());
    }
    Ok(())
}

pub(super) fn submit_library_switch(ui: &AppWindow, games: &Path, api: &str) -> Result<(), String> {
    fs::create_dir(games).map_err(|error| error.to_string())?;
    ui.invoke_panel_requested("settings".into());
    ui.set_settings_path(games.to_string_lossy().into_owned().into());
    ui.set_settings_api(api.into());
    ui.invoke_settings_leave("".into());
    if !ui.get_operation_busy() {
        return Err("saving the new library and server did not start".into());
    }
    Ok(())
}

pub(super) fn check_library_switch_rejected(
    ui: &AppWindow,
    state: &State,
    original_games: &str,
    original_base: &str,
    replacement_games: &Path,
) -> Result<(), String> {
    let s = state.borrow();
    let saved = s.services.config.load()?;
    if !ui.get_settings_error().contains("HTTPS")
        || ui.get_panel_kind() != "settings"
        || ui.get_settings_path().as_str() != replacement_games.to_string_lossy()
        || ui.get_settings_api() != "file:///invalid-api"
        || s.services.paths.games_dir != Path::new(original_games)
        || s.services.api.base.as_str().trim_end_matches('/') != original_base
        || saved.games_path != original_games
        || saved.api_base_url != original_base
    {
        return Err("failed library switch changed saved state or lost the draft".into());
    }
    Ok(())
}

pub(super) fn check_library_switch_saved(
    ui: &AppWindow,
    state: &State,
    games: &Path,
    api: &str,
    original_games: &str,
) -> Result<(), String> {
    let s = state.borrow();
    let saved = s.services.config.load()?;
    if !ui.get_panel_kind().is_empty()
        || !ui.get_settings_error().is_empty()
        || s.services.paths.games_dir != games
        || s.services.api.base.as_str().trim_end_matches('/') != api
        || saved.games_path != games.to_string_lossy()
        || saved.api_base_url != api
        || !Path::new(original_games).join("fixture/Game.exe").is_file()
        || games.join("fixture").exists()
    {
        return Err(
            "successful library switch did not persist both changes and preserve old files".into(),
        );
    }
    Ok(())
}
