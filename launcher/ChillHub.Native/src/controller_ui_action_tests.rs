use super::*;
use chillhub_native::transfer::SyncScope;

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
