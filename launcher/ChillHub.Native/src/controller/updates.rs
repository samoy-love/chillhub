use super::{
    background::{Activity, background, background_for},
    confirmation::{Confirmation, confirm},
    lifecycle::save_window,
};
use crate::{AppWindow, State, open_panel, toast};
use chillhub_native::{
    service::Services,
    transfer::TransferControl,
    update::{self, StagedUpdate, UpdateCheck, UpdatePaths, UpdateRelease},
};
use slint::ComponentHandle;
use std::path::{Path, PathBuf};

fn staging_root(paths: &chillhub_native::paths::AppPaths) -> PathBuf {
    if std::env::var_os("CHILLHUB_DATA_DIR").is_some() {
        paths.data_dir.with_extension("updates")
    } else {
        PathBuf::from(std::env::var_os("LOCALAPPDATA").unwrap_or_default())
            .join("ChillHubUpdateStaging")
    }
}

pub(super) fn check_background(ui: &AppWindow, state: &State, startup: bool) {
    if state.borrow().update_inflight
        || ui.get_operation_busy()
        || std::env::var_os("CHILLHUB_DATA_DIR").is_some()
    {
        return;
    }
    state.borrow_mut().update_inflight = true;
    let services = state.borrow().services.clone();
    background_for(
        ui,
        Activity::UpdateCheck,
        move || {
            let install = std::env::current_exe()
                .ok()
                .and_then(|p| p.parent().map(Path::to_owned))
                .unwrap_or_default();
            let paths = UpdatePaths {
                install,
                config: services.paths.data_dir.clone(),
                games: services.paths.games_dir.clone(),
                staging: staging_root(&services.paths),
            };
            let pending = update::pending_recoveries(&paths);
            let previous = if startup {
                update::previous_outcome(&paths)
            } else {
                None
            };
            let release = update::check_release(
                &services.api.client,
                services.api.base.as_str(),
                env!("CARGO_PKG_VERSION"),
            );
            (pending, previous, release, paths)
        },
        move |ui, state, (pending, previous, release, paths)| {
            state.borrow_mut().update_inflight = false;
            if let Ok(pending) = pending
                && let Some(staged) = pending.into_iter().next()
            {
                confirm(ui,state,Confirmation::Recovery(staged),"Восстановить прерванное обновление?","Предыдущее обновление не завершилось. Помощник вернёт сохранённые файлы лаунчера и откроет его снова. Игры останутся на месте.".into(),"Восстановить и перезапустить");
                return;
            }
            if let Some(previous) = previous {
                toast(ui, state, &previous.message);
                background(
                    ui,
                    move || update::acknowledge_outcome(&paths, &previous),
                    |_, _, _| {},
                );
            }
            if let Ok(UpdateCheck::Available(release)) = release {
                offer_automatic_update(ui, state, release);
            }
        },
    );
}

fn offer_automatic_update(ui: &AppWindow, state: &State, release: UpdateRelease) {
    let defer = {
        let s = state.borrow();
        ui.get_operation_busy()
            || !ui.get_panel_kind().is_empty()
            || !s.services.idle()
            || !s.launching.is_empty()
            || s.shortcut.is_some()
            || !s.pending_shortcuts.is_empty()
    };
    if defer {
        ui.set_connection_note(format!("Доступно обновление Chill Hub {}", release.version).into());
    }
    state.borrow_mut().update = Some(release);
    if !defer {
        open_panel(ui, state, "update");
        install(ui, state);
    }
}

pub(super) fn prepare_panel(ui: &AppWindow, state: &State) {
    let release = state.borrow().update.clone();
    ui.set_update_error(false);
    ui.set_task_status("".into());
    ui.set_task_progress(0.);
    if let Some(release) = release {
        show_available_update(ui, &release);
    } else {
        check_manually(ui, state);
    }
}

fn show_available_update(ui: &AppWindow, release: &UpdateRelease) {
    ui.set_update_checked(true);
    ui.set_update_available(true);
    ui.set_update_target(release.version.clone().into());
    ui.set_panel_body(
        format!("Доступна версия {}. Игры и настройки сохранятся. После загрузки лаунчер перезапустится.", release.version).into(),
    );
}

pub(super) fn begin_recovery(ui: &AppWindow, staged: StagedUpdate) {
    ui.set_operation_busy(true);
    background_for(
        ui,
        Activity::Operation,
        move || update::launch_recovery(&staged, &[]).map(|_| ()),
        |ui, state, r| {
            ui.set_operation_busy(false);
            match r {
                Ok(()) => {
                    save_window(ui, state);
                    let _ = slint::quit_event_loop();
                }
                Err(e) => toast(ui, state, &e),
            }
        },
    );
}

pub(super) fn check_manually(ui: &AppWindow, state: &State) {
    let services = state.borrow().services.clone();
    state.borrow_mut().update = None;
    ui.set_operation_busy(true);
    ui.set_update_available(false);
    ui.set_update_checked(false);
    ui.set_update_error(false);
    ui.set_update_target("".into());
    ui.set_task_status("".into());
    ui.set_task_progress(0.);
    ui.set_panel_body("Ищем новую версию Chill Hub…".into());
    background_for(
        ui,
        Activity::Operation,
        move || {
            update::check_release(
                &services.api.client,
                services.api.base.as_str(),
                env!("CARGO_PKG_VERSION"),
            )
        },
        |ui, state, r| {
            ui.set_operation_busy(false);
            ui.set_update_checked(true);
            match r {
                Ok(UpdateCheck::Current { .. }) => {
                    state.borrow_mut().update = None;
                    ui.set_panel_body("У вас актуальная версия Chill Hub.".into());
                }
                Ok(UpdateCheck::Available(release)) => {
                    show_available_update(ui, &release);
                    state.borrow_mut().update = Some(release);
                }
                Err(e) => {
                    ui.set_update_error(true);
                    ui.set_panel_body(format!("Не удалось проверить обновления.\n\n{e}").into());
                }
            }
        },
    );
}

pub(super) fn verify(ui: &AppWindow, state: &State) {
    if !can_change_installation(ui, state) {
        return;
    }
    let services = state.borrow().services.clone();
    let Some(release) = state.borrow().update.clone() else {
        toast(ui, state, "Сначала проверьте обновления");
        return;
    };
    let paths = match update_paths(&services) {
        Ok(paths) => paths,
        Err(e) => {
            toast(ui, state, &e);
            return;
        }
    };
    ui.set_operation_busy(true);
    ui.set_update_error(false);
    ui.set_task_progress(0.);
    ui.set_task_status("Проверяем установленные файлы…".into());
    let weak = ui.as_weak();
    background_for(
        ui,
        Activity::Operation,
        move || {
            update::verify_installed_release(
                &release,
                &paths,
                &TransferControl::default(),
                &move |e| update_progress(&weak, e),
            )
        },
        |ui, state, result| {
            ui.set_operation_busy(false);
            match result {
                Ok(report) if report.mismatches.is_empty() => {
                    ui.set_task_progress(1.);
                    ui.set_task_status(
                        format!("Проверено файлов: {}", report.checked_files).into(),
                    );
                    ui.set_panel_body("Все установленные файлы соответствуют выбранному выпуску. Номер версии восстановлен, ограничение повторных попыток снято.".into());
                }
                Ok(report) => {
                    ui.set_task_status(
                        format!(
                            "Проверено файлов: {} · отличаются: {}",
                            report.checked_files,
                            report.mismatches.len()
                        )
                        .into(),
                    );
                    let names = report
                        .mismatches
                        .iter()
                        .take(12)
                        .cloned()
                        .collect::<Vec<_>>()
                        .join("\n");
                    ui.set_panel_body(format!("Некоторые файлы отличаются от выпуска. Файлы лаунчера и игр не изменены. Если повторное обновление заблокировано, скачайте установщик с официального сайта.\n\n{names}").into());
                }
                Err(e) => {
                    ui.set_update_error(true);
                    ui.set_task_status(e.clone().into());
                    toast(ui, state, &e);
                }
            }
        },
    );
}

pub(super) fn install(ui: &AppWindow, state: &State) {
    if !can_change_installation(ui, state) {
        return;
    }
    let services = state.borrow().services.clone();
    let Some(release) = state.borrow().update.clone() else {
        toast(ui, state, "Сначала проверьте обновления");
        return;
    };
    if !services.idle() {
        toast(
            ui,
            state,
            "Сначала завершите или приостановите загрузки игр",
        );
        return;
    }
    let exe = match std::env::current_exe() {
        Ok(p) => p,
        Err(e) => {
            toast(ui, state, &e.to_string());
            return;
        }
    };
    let stage = staging_root(&services.paths);
    let paths = UpdatePaths {
        install: exe.parent().unwrap().into(),
        config: services.paths.data_dir.clone(),
        games: services.paths.games_dir.clone(),
        staging: stage,
    };
    ui.set_operation_busy(true);
    ui.set_update_error(false);
    ui.set_task_progress(0.);
    ui.set_task_status("Подготавливаем обновление…".into());
    let weak = ui.as_weak();
    background_for(
        ui,
        Activity::Operation,
        move || {
            let staged =
                update::stage_update(&release, &paths, &TransferControl::default(), &move |e| {
                    let weak = weak.clone();
                    let _ = slint::invoke_from_event_loop(move || {
                        if let Some(ui) = weak.upgrade() {
                            ui.set_task_progress(if e.total_bytes > 0 {
                                e.completed_bytes as f32 / e.total_bytes as f32
                            } else {
                                0.
                            });
                            ui.set_task_status(e.current_file.into());
                        }
                    });
                })?;
            update::launch_updater(&staged, &[]).map(|_| ())
        },
        |ui, state, result| {
            ui.set_operation_busy(false);
            match result {
                Ok(()) => {
                    save_window(ui, state);
                    state.borrow().services.queue.shutdown();
                    let _ = slint::quit_event_loop();
                }
                Err(e) => {
                    ui.set_update_error(true);
                    ui.set_task_status(format!("{e}\nЕсли обновление не удаётся повторить, нажмите «Проверить установку».").into());
                    toast(ui, state, &e);
                }
            }
        },
    );
}

fn can_change_installation(ui: &AppWindow, state: &State) -> bool {
    if state.borrow().launching.is_empty() {
        return true;
    }
    toast(
        ui,
        state,
        "Дождитесь запуска игр перед обновлением лаунчера.",
    );
    false
}

pub(crate) fn update_paths(services: &Services) -> Result<UpdatePaths, String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    Ok(UpdatePaths {
        install: exe.parent().ok_or("Папка лаунчера не найдена")?.into(),
        config: services.paths.data_dir.clone(),
        games: services.paths.games_dir.clone(),
        staging: staging_root(&services.paths),
    })
}
fn update_progress(weak: &slint::Weak<AppWindow>, e: chillhub_native::transfer::TransferEvent) {
    let weak = weak.clone();
    let _ = slint::invoke_from_event_loop(move || {
        if let Some(ui) = weak.upgrade() {
            ui.set_task_progress(if e.total_bytes > 0 {
                e.completed_bytes as f32 / e.total_bytes as f32
            } else {
                0.
            });
            ui.set_task_status(
                format!(
                    "Проверено файлов: {} из {}\n{}",
                    e.completed_files, e.total_files, e.current_file
                )
                .into(),
            );
        }
    });
}

#[cfg(test)]
#[path = "update_tests.rs"]
pub(crate) mod tests;
