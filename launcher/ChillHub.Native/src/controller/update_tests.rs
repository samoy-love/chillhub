use super::{AppWindow, State, UpdateRelease, offer_automatic_update};
use crate::close_panel;
use chillhub_native::launch::ShortcutRequest;

pub(crate) fn start_automatic_update(ui: &AppWindow, state: &State) -> Result<(), String> {
    // Staging rejects this origin before downloading or installing anything.
    let release = UpdateRelease {
        version: "99999.0.0".into(),
        manifest_url: "http://127.0.0.1:1/fixture.json".into(),
        content_base_url: "http://127.0.0.1:1/files/".into(),
    };
    ui.invoke_panel_requested("settings".into());
    ui.set_settings_path("unfinished settings draft".into());
    offer_automatic_update(ui, state, release.clone());
    if ui.get_panel_kind() != "settings"
        || ui.get_settings_path() != "unfinished settings draft"
        || ui.get_operation_busy()
        || !ui.get_connection_note().contains(&release.version)
    {
        return Err("automatic update replaced an open settings draft".into());
    }

    // Closing via the fixture avoids persisting the intentionally invalid draft.
    close_panel(ui, state);
    for loading_shortcut in [true, false] {
        let request = ShortcutRequest {
            game_id: "fixture".into(),
            ..Default::default()
        };
        {
            let mut s = state.borrow_mut();
            s.update = None;
            if loading_shortcut {
                s.shortcut = Some(request);
            } else {
                s.pending_shortcuts.push_back(request);
            }
        }
        ui.set_connection_note("".into());
        offer_automatic_update(ui, state, release.clone());
        let mut s = state.borrow_mut();
        let waiting = s.shortcut.is_some() || s.pending_shortcuts.len() == 1;
        s.shortcut = None;
        s.pending_shortcuts.clear();
        if !waiting
            || !ui.get_panel_kind().is_empty()
            || ui.get_operation_busy()
            || !ui.get_connection_note().contains(&release.version)
            || s.update
                .as_ref()
                .is_none_or(|update| update.version != release.version)
        {
            return Err(
                "automatic update interrupted a pending shortcut or discarded the release".into(),
            );
        }
    }
    ui.invoke_panel_requested("update".into());
    if ui.get_operation_busy() || !ui.get_update_available() || !ui.get_update_checked() {
        return Err("the update panel discarded the release found in the background".into());
    }
    close_panel(ui, state);
    offer_automatic_update(ui, state, release.clone());
    if ui.get_panel_kind() != "update"
        || !ui.get_operation_busy()
        || ui.get_update_target().as_str() != release.version
        || ui.get_task_status() != "Подготавливаем обновление…"
    {
        return Err("the automatic update did not stage the checked release".into());
    }
    Ok(())
}

pub(crate) fn check_automatic_update_failure(ui: &AppWindow, state: &State) -> Result<(), String> {
    if !ui.get_update_error()
        || !ui.get_update_available()
        || !ui.get_task_status().contains("Проверить установку")
    {
        return Err("failed staging did not leave the release available for verification".into());
    }
    close_panel(ui, state);
    state.borrow_mut().update = None;
    ui.set_connection_note("".into());
    Ok(())
}
