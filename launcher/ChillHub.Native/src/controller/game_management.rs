use super::{
    background::{background, operation},
    choice::selected,
    confirmation::{Confirmation, confirm},
};
use crate::{AppWindow, State, queue_ui, settings_ui, strings, toast};
use chillhub_native::service;

pub(super) fn prepare_panel(ui: &AppWindow, state: &State) {
    ui.set_manage_path("".into());
    ui.set_manage_details("Считаем размер файлов…".into());
    ui.set_versions(strings(Vec::new()));
    if let Some((game, target)) = selected(state) {
        let services = state.borrow().services.clone();
        let id = game.game_id.clone();
        let generation = state.borrow().generation;
        ui.set_selected_version("".into());
        background(
            ui,
            move || {
                let path = services
                    .option(&game, target)
                    .map(|o| o.game_dir)
                    .unwrap_or_default();
                let size = if path.as_os_str().is_empty() {
                    "Копия не найдена".into()
                } else {
                    settings_ui::folder_size(&path)
                        .map(|v| format!("На диске: {}", queue_ui::size(v)))
                        .unwrap_or_else(|_| "Не удалось прочитать размер файлов".into())
                };
                let time = services.playtime.totals(&game.game_id).unwrap_or_default();
                let last = time
                    .last_session_at
                    .map(|at| {
                        format!(
                            "Последняя игра: {} · {} мин",
                            at.with_timezone(&chrono::Local).format("%d.%m.%Y %H:%M"),
                            time.last_session_seconds.max(0) / 60
                        )
                    })
                    .unwrap_or_else(|| "Последних сеансов пока нет".into());
                let version = if target.via_steam() {
                    "Версию игры обновляет Steam".into()
                } else {
                    format!(
                        "Последняя версия в каталоге: {}",
                        if game.latest_version.is_empty() {
                            "не опубликована"
                        } else {
                            &game.latest_version
                        }
                    )
                };
                let details = format!("{size}\n{version}\n{last}");
                let chosen = services
                    .config
                    .load_readonly()
                    .ok()
                    .and_then(|c| c.selected_version(&game.game_id).map(str::to_owned))
                    .unwrap_or(game.latest_version);
                (chosen, services.api.builds(&game.game_id), path, details)
            },
            move |ui, state, (chosen, result, path, details)| {
                let current = selected(state).map(|(game, _)| game.game_id);
                if ui.get_panel_kind() != "manage"
                    || state.borrow().generation != generation
                    || current.as_deref() != Some(id.as_str())
                {
                    return;
                }
                ui.set_selected_version(chosen.into());
                ui.set_manage_path(path.to_string_lossy().into_owned().into());
                ui.set_manage_details(details.into());
                match result {
                    Ok(v) => ui.set_versions(strings(v)),
                    Err(e) => toast(ui, state, &e.to_string()),
                }
            },
        );
    }
}

pub(super) fn copy_game_path(ui: &AppWindow, state: &State) {
    let path = ui.get_manage_path();
    if !path.is_empty() {
        match settings_ui::copy_text(state.borrow().native_hwnd, &path) {
            Ok(()) => toast(ui, state, "Путь скопирован"),
            Err(e) => toast(ui, state, &e),
        }
    }
}

pub(super) fn open_modpack_source(ui: &AppWindow, state: &State) {
    if let Some(url) =
        selected(state).and_then(|(g, _)| g.mods.as_ref().and_then(settings_ui::modpack_url))
        && let Err(e) = settings_ui::open_package(&url)
    {
        toast(ui, state, &e);
    }
}

pub(super) fn verify(ui: &AppWindow, state: &State, mods: bool) {
    let services = state.borrow().services.clone();
    if let Some((game, target)) = selected(state) {
        if target.via_steam() && !mods {
            toast(
                ui,
                state,
                "Проверьте файлы игры в Steam: Свойства → Установленные файлы → Проверить целостность. Модпак проверяется отдельно.",
            );
            return;
        }
        operation(ui, move || {
            services
                .enqueue_game(&game, target, None, true, mods)
                .map(|_| "Проверка добавлена в очередь".into())
        });
    }
}

pub(super) fn open_game_folder(ui: &AppWindow, state: &State) {
    let services = state.borrow().services.clone();
    if let Some((game, target)) = selected(state) {
        operation(ui, move || {
            services
                .option(&game, target)
                .and_then(|o| service::open_directory(&o.game_dir))
                .map(|_| "Папка открыта".into())
        });
    }
}

pub(super) fn delete_game(ui: &AppWindow, state: &State) {
    let services = state.borrow().services.clone();
    if let Some((game, target)) = selected(state) {
        if target.via_steam() {
            toast(ui, state, "Steam-копия удаляется через библиотеку Steam");
            return;
        }
        let root = services
            .paths
            .game_root(&game.game_id)
            .map(|p| p.display().to_string())
            .unwrap_or_default();
        confirm(
            ui,
            state,
            Confirmation::Delete(game),
            "Удалить локальную копию?",
            format!(
                "Будет удалена папка:\n{root}\n\nСохранения и добавленные вручную файлы внутри неё тоже будут удалены. Сохранения вне этой папки останутся."
            ),
            "Удалить копию",
        );
    }
}

pub(super) fn install_version(ui: &AppWindow, state: &State) {
    if let Some((game, target)) = selected(state) {
        if target.via_steam() {
            toast(ui, state, "Версией Steam управляет Steam");
            return;
        }
        let version = ui.get_selected_version().to_string();
        confirm(
            ui,
            state,
            Confirmation::Version(game, target, version.clone()),
            "Установить выбранную версию?",
            format!(
                "Версия {version} заменит текущую локальную копию. При несовместимости сохранений используйте их резервную копию."
            ),
            "Установить версию",
        );
    }
}
