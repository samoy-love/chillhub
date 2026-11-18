use super::{
    background::{Activity, background, background_for},
    catalog::reload,
    choice::selected,
    confirmation, game_management, security_panel, settings, shortcuts,
    ui_action::UiAction,
    updates,
};
use crate::{AppWindow, State, close_panel, open_panel, orphan_ui, settings_ui, toast};
use chillhub_native::service;
use std::path::PathBuf;

pub(super) fn dispatch(ui: &AppWindow, state: &State, requested: UiAction) {
    match requested {
        UiAction::ChooseGamesFolder => choose_games_folder(ui, state),
        UiAction::CopyGamePath => game_management::copy_game_path(ui, state),
        UiAction::OpenModpackSource => game_management::open_modpack_source(ui, state),
        UiAction::RetryCatalog => reload(ui, state),
        UiAction::DeleteSelectedOrphans => orphan_ui::confirm_selected(ui, state),
        UiAction::OpenLogs => open_logs(ui, state),
        UiAction::OpenPanel(panel) => open_panel(ui, state, panel.token()),
        UiAction::Cancel => {
            state.borrow_mut().pending = None;
            close_panel(ui, state);
        }
        UiAction::Confirm => confirmation::respond(ui, state),
        UiAction::VerifyGame => game_management::verify(ui, state, false),
        UiAction::VerifyMods => game_management::verify(ui, state, true),
        UiAction::OpenGameFolder => game_management::open_game_folder(ui, state),
        UiAction::DeleteGame => game_management::delete_game(ui, state),
        UiAction::InstallVersion => game_management::install_version(ui, state),
        UiAction::CreateShortcut => shortcuts::create(ui, state),
        UiAction::CheckSecurity => security_panel::check(ui),
        UiAction::OpenSecurityHelper => security_panel::open_helper(ui, state),
        UiAction::OpenWindowsSecurity => security_panel::open_windows_security(ui, state),
        UiAction::CheckUpdate => updates::check_manually(ui, state),
        UiAction::VerifyUpdate => updates::verify(ui, state),
        UiAction::InstallUpdate => updates::install(ui, state),
    }
}

pub(crate) fn prepare_panel(ui: &AppWindow, state: &State, kind: &str) {
    match kind {
        "update" => updates::prepare_panel(ui, state),
        "mods" => ui.set_has_modpack_link(
            selected(state)
                .and_then(|(g, _)| g.mods.as_ref().and_then(settings_ui::modpack_url))
                .is_some(),
        ),
        "settings" => settings::load(ui, state),
        "changelog" => mark_changelog_seen(ui, state),
        "orphans" => orphan_ui::prepare(ui, state),
        "manage" => game_management::prepare_panel(ui, state),
        _ => {}
    }
}

pub(super) fn choose_games_folder(ui: &AppWindow, state: &State) {
    if ui.get_operation_busy() || ui.get_panel_kind() != "settings" {
        return;
    }
    let owner = state.borrow().native_hwnd;
    let initial = PathBuf::from(ui.get_settings_path().as_str());
    ui.set_operation_busy(true);
    background_for(
        ui,
        Activity::Operation,
        move || settings_ui::choose_folder(owner, &initial),
        |ui, state, result| {
            ui.set_operation_busy(false);
            if ui.get_panel_kind() != "settings" {
                return;
            }
            match result {
                Ok(Some(path)) => ui.set_settings_path(path.to_string_lossy().into_owned().into()),
                Ok(None) => {}
                Err(e) => {
                    ui.set_settings_error(e.clone().into());
                    toast(ui, state, &e);
                }
            }
        },
    );
}

fn open_logs(ui: &AppWindow, state: &State) {
    let services = state.borrow().services.clone();
    if let Some(directory) =
        chillhub_native::diagnostics::existing_log_directory(&services.paths.data_dir)
    {
        if let Err(e) = service::open_directory(&directory) {
            toast(ui, state, &e);
        }
    } else {
        let e = "Журнал недоступен или отключён.";
        toast(ui, state, e);
    }
}

fn mark_changelog_seen(ui: &AppWindow, state: &State) {
    if !chillhub_native::changelog::should_show(
        &state.borrow().prefs.last_seen_changelog_version,
        env!("CARGO_PKG_VERSION"),
    ) {
        return;
    }
    let store = state.borrow().services.config.clone();
    state.borrow_mut().prefs.last_seen_changelog_version = env!("CARGO_PKG_VERSION").into();
    background(
        ui,
        move || {
            store.update(|c| {
                if chillhub_native::changelog::should_show(
                    &c.last_seen_changelog_version,
                    env!("CARGO_PKG_VERSION"),
                ) {
                    c.last_seen_changelog_version = env!("CARGO_PKG_VERSION").into();
                }
            })
        },
        |ui, state, result| {
            if let Err(error) = result {
                toast(
                    ui,
                    state,
                    &format!("Не удалось запомнить просмотр обновлений: {error}"),
                );
            }
        },
    );
}
