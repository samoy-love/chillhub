use super::{actions, background::operation, security_panel, shortcut_install, shortcuts, updates};
use crate::{AppWindow, OrphanRow, State, close_panel, open_panel, orphan_ui};
use chillhub_native::{
    config::LaunchTarget, launch::ShortcutRequest, model::GameRecord, transfer::CleanupFile,
    update::StagedUpdate,
};
use std::path::PathBuf;

#[derive(Clone)]
pub(crate) enum Confirmation {
    Delete(GameRecord),
    DeleteOrphans(Vec<OrphanRow>),
    ChangeGamesFolder,
    Cleanup(String, Vec<CleanupFile>),
    ResetQueue,
    Version(GameRecord, LaunchTarget, String),
    Security(Vec<PathBuf>),
    Recovery(StagedUpdate),
    Shortcut(ShortcutRequest),
    InstallShortcut(GameRecord, LaunchTarget),
}

pub(crate) fn confirm(
    ui: &AppWindow,
    state: &State,
    action: Confirmation,
    title: &str,
    body: String,
    label: &str,
) {
    close_panel(ui, state);
    state.borrow_mut().pending = Some(action);
    ui.set_panel_title(title.into());
    ui.set_panel_subtitle("Проверьте выбранное действие".into());
    ui.set_panel_body(body.into());
    ui.set_confirm_text(label.into());
    ui.set_panel_kind("confirm".into());
}

pub(super) fn respond(ui: &AppWindow, state: &State) {
    let services = state.borrow().services.clone();
    let pending = state.borrow_mut().pending.take();
    close_panel(ui, state);
    match pending {
        Some(Confirmation::DeleteOrphans(rows)) => orphan_ui::delete_selected(ui, state, rows),
        Some(Confirmation::ChangeGamesFolder) => {
            open_panel(ui, state, "settings");
            actions::choose_games_folder(ui, state);
        }
        Some(Confirmation::Delete(game)) => operation(ui, move || {
            services.delete_local(&game)?;
            chillhub_native::installed_apps::refresh(
                &services.paths.games_dir,
                &services.paths.data_dir,
            );
            Ok("Локальная копия удалена".into())
        }),
        Some(Confirmation::Cleanup(id, files)) => operation(ui, move || {
            services
                .queue
                .confirm_cleanup(&id, &files)
                .map(|_| "Задача продолжена".into())
                .map_err(|e| e.to_string())
        }),
        Some(Confirmation::ResetQueue) => operation(ui, move || {
            services
                .queue
                .reset_unreadable()
                .map(|()| "Очередь загрузок восстановлена".into())
                .map_err(|error| error.to_string())
        }),
        Some(Confirmation::Version(game, target, version)) => operation(ui, move || {
            services
                .enqueue_game(&game, target, Some(&version), false, false)
                .map(|_| "Выбранная версия добавлена в очередь".into())
        }),
        Some(Confirmation::Recovery(staged)) => updates::begin_recovery(ui, staged),
        Some(Confirmation::Shortcut(request)) => shortcuts::launch_fallback(ui, state, request),
        Some(Confirmation::InstallShortcut(game, target)) => {
            shortcut_install::install(ui, state, game, target);
        }
        Some(Confirmation::Security(paths)) => security_panel::open_for_paths(ui, paths),
        None => {}
    }
}
