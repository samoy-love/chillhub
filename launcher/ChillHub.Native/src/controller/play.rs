use super::{
    background::{Activity, background, background_for, report_failure},
    choice::{refresh_choice, selected},
    confirmation::{Confirmation, confirm},
};
use crate::{AppWindow, State, install_ui, toast};
use chillhub_native::{
    config::LaunchTarget,
    launch::{self, LaunchAction, LaunchOutcome},
    model::GameRecord,
    service::Services,
};
use std::time::Duration;

pub(crate) fn play(ui: &AppWindow, state: &State) {
    let shown_action = state.borrow().option.as_ref().map(|o| o.action.clone());
    start_selected_play(ui, state, shown_action);
}

fn start_selected_play(ui: &AppWindow, state: &State, shown_action: Option<LaunchAction>) {
    if ui.get_operation_busy() {
        return;
    }
    let Some((game, target)) = selected(state) else {
        return;
    };
    let services = state.borrow().services.clone();
    let offline = state.borrow().offline;
    let launching = shown_action == Some(LaunchAction::Play);
    let launch_key = (game.game_id.clone(), target.via_steam());
    let late_version = game.latest_version.clone();
    if state.borrow().launching.contains(&launch_key) {
        return;
    }
    install_ui::clear_hint(ui);
    if launching {
        state.borrow_mut().launching.insert(launch_key.clone());
        ui.set_play_label("Запускаем…".into());
        ui.set_status("Ожидаем запуска игры".into());
    } else {
        ui.set_busy(true);
        ui.set_operation_busy(true);
    }
    ui.set_can_play(false);
    background_for(
        ui,
        if launching {
            Activity::Quiet
        } else {
            Activity::Operation
        },
        move || -> Result<Option<LaunchOutcome>, install_ui::PlayFailure> {
            std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let option = services.option(&game, target)?;
            if shown_action.is_some_and(|shown| shown != option.action) {
                return Err("Состояние игры изменилось. Проверьте действие на кнопке и нажмите её снова.".to_string().into());
            }
            if offline && option.action == LaunchAction::Play {
                services.cached_launch_maintenance_check()?;
            } else {
                services.maintenance_check(&option.action)?;
            }
            if !option.available() {
                return Err(option.note.into());
            }
            if option.action == LaunchAction::Play {
                let launched = services.launch_service().launch(&game, target)?;
                if matches!(&launched, LaunchOutcome::Started(_)) {
                    record_catalog_game_launch(&services, game, target, &option.game_dir);
                }
                Ok(Some(launched))
            } else {
                if !target.via_steam()
                    && let Err(e)=install_ui::preflight(&option.game_dir) {
                        if install_ui::can_choose_folder_after(&e) {return Err(install_ui::PlayFailure::Folder(option.game_dir,e.to_string()));}
                        return Err(format!("Папка установки недоступна: {e}").into());
                    }
                services
                    .enqueue_game(&game, target, None, false, false)
                    .map(|_| None)
                    .map_err(Into::into)
            }
            })).unwrap_or_else(|_| Err(install_ui::PlayFailure::General("Не удалось завершить запуск. Подробности сохранены в журнале.".into())))
        },
        move |ui, state, result| {
            if launching {
                state.borrow_mut().launching.remove(&launch_key);
            } else {
                ui.set_busy(false);
                ui.set_operation_busy(false);
            }
            match result {
                Ok(Some(LaunchOutcome::Started(session))) => {
                    show_game_started(ui, state, session);
                }
                Ok(Some(LaunchOutcome::SteamPending {
                    warning,
                    late_session,
                })) => {
                    toast(ui, state, warning);
                    let services = state.borrow().services.clone();
                    let version = late_version.clone();
                    background(
                        ui,
                        move || {
                            let session = late_session.recv().ok()?;
                            record_game_launch(&services, session.game_id.clone(), version);
                            Some(session)
                        },
                        |ui, state, session| {
                            if let Some(session) = session {
                                show_game_started(ui, state, session);
                                refresh_choice(ui, state);
                            }
                        },
                    );
                }
                Ok(None) => {
                    // Like the original queue dock, an installation must leave
                    // the library available so another game can be queued.
                    toast(ui, state, "Игра добавлена в загрузки");
                }
                Err(install_ui::PlayFailure::General(error)) => {
                    report_failure(ui, state, "launch", &error)
                }
                Err(install_ui::PlayFailure::Folder(path, error)) => {
                    chillhub_native::diagnostics::log(
                        &state.borrow().services.paths.data_dir,
                        "install-path",
                        &error,
                    );
                    confirm(
                        ui,
                        state,
                        Confirmation::ChangeGamesFolder,
                        "В эту папку нельзя записать файлы",
                        format!(
                            "Папка:\n{}\n\nВыберите другую папку для новых установок. Уже установленные игры не будут перемещены. После сохранения настроек нажмите «Установить» снова.",
                            path.display()
                        ),
                        "Выбрать другую папку",
                    );
                }
            }
            refresh_choice(ui, state);
        },
    );
}
fn record_game_launch(services: &Services, game_id: String, version: String) {
    chillhub_native::diagnostics::metric(
        &services.api,
        &services.paths.data_dir,
        chillhub_native::diagnostics::Metric {
            event: "game_launch".into(),
            game_id,
            version,
            result: "ok".into(),
            ..Default::default()
        },
    );
}
pub(super) fn record_catalog_game_launch(
    services: &Services,
    game: GameRecord,
    target: LaunchTarget,
    directory: &std::path::Path,
) {
    let installed = (!target.via_steam())
        .then(|| launch::read_marker(directory, ".version"))
        .filter(|version| !version.is_empty());
    record_game_launch(
        services,
        game.game_id,
        installed.unwrap_or(game.latest_version),
    );
}
pub(super) fn show_game_started(
    ui: &AppWindow,
    state: &State,
    session: chillhub_native::playtime::SessionHandle,
) {
    toast(
        ui,
        state,
        session.warning.as_deref().unwrap_or("Игра запущена"),
    );
    watch_game(ui, session);
}
pub(super) fn watch_game(ui: &AppWindow, session: chillhub_native::playtime::SessionHandle) {
    background(
        ui,
        move || {
            while session.process.is_running() {
                std::thread::sleep(Duration::from_secs(1));
            }
        },
        |ui, state, ()| refresh_choice(ui, state),
    );
}
