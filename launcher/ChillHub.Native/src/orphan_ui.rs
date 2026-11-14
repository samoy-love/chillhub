//! Deferred discovery and explicit, bounded cleanup of unpublished local games.
use super::*;

#[derive(Default)]
pub(super) struct Notice {
    seen: std::collections::HashSet<String>,
    pending: bool,
}

fn available(ui: &AppWindow, state: &State) -> bool {
    ui.window().is_visible()
        && !ui.window().is_minimized()
        && ui.get_panel_kind().is_empty()
        && !ui.get_operation_busy()
        && !ui.get_loading()
        && !state.borrow().update_inflight
}

pub(super) fn catalog_ready(ui: &AppWindow, state: &State) {
    let (services, records, generation, offline) = {
        let s = state.borrow();
        (
            s.services.clone(),
            s.records.clone(),
            s.catalog_generation,
            s.offline,
        )
    };
    // An unavailable/empty catalogue is not proof that locally installed games were removed.
    if offline || records.is_empty() {
        return;
    }
    controller::background(
        ui,
        move || chillhub_native::library::orphaned(&services.paths, &records),
        move |ui, state, result| {
            if state.borrow().catalog_generation != generation {
                return;
            }
            match result {
                Ok(rows) if !rows.is_empty() => {
                    let fingerprint = rows
                        .iter()
                        .map(|r| r.path.to_string_lossy().to_lowercase())
                        .collect::<Vec<_>>()
                        .join("\n");
                    if state.borrow_mut().orphan_notice.seen.insert(fingerprint) {
                        state.borrow_mut().orphan_notice.pending = true;
                        pump(ui, state);
                    }
                }
                Err(e) => chillhub_native::diagnostics::log(
                    &state.borrow().services.paths.data_dir,
                    "orphan-scan",
                    &e,
                ),
                _ => {}
            }
        },
    );
}

pub(super) fn pump(ui: &AppWindow, state: &State) {
    if available(ui, state) && state.borrow().orphan_notice.pending {
        state.borrow_mut().orphan_notice.pending = false;
        open_panel(ui, state, "orphans");
    }
}

pub(super) fn after_panel_closed(ui: &AppWindow, state: &State) {
    if !state.borrow().orphan_notice.pending {
        return;
    }
    let ui = ui.as_weak();
    let state = Rc::downgrade(state);
    Timer::single_shot(Duration::from_millis(300), move || {
        if let (Some(ui), Some(state)) = (ui.upgrade(), state.upgrade()) {
            pump(&ui, &state);
        }
    });
}

pub(super) fn prepare(ui: &AppWindow, state: &State) {
    state.borrow_mut().orphan_notice.pending = false;
    let (services, records, generation) = {
        let s = state.borrow();
        (s.services.clone(), s.records.clone(), s.catalog_generation)
    };
    ui.set_orphans(ModelRc::default());
    ui.set_orphan_selection_count(0);
    ui.set_panel_body("Ищем установленные игры, которых больше нет в каталоге…".into());
    controller::background(
        ui,
        move || chillhub_native::library::orphaned(&services.paths, &records),
        move |ui, state, result| {
            if ui.get_panel_kind() != "orphans" || state.borrow().catalog_generation != generation {
                return;
            }
            match result {
                Ok(rows) => {
                    let rows = rows
                        .into_iter()
                        .map(|r| OrphanRow {
                            id: r.id.clone().into(),
                            title: r.id.into(),
                            path: r.path.to_string_lossy().into_owned().into(),
                            selected: true,
                            result: "".into(),
                        })
                        .collect::<Vec<_>>();
                    let empty = rows.is_empty();
                    ui.set_orphan_selection_count(rows.len() as i32);
                    ui.set_orphans(Rc::new(VecModel::from(rows)).into());
                    ui.set_panel_body(if empty {"Все установленные игры есть в каталоге."}else{"Эти игры остались на диске, но больше не опубликованы. Их можно оставить или удалить выбранные папки. Сохранения внутри удаляемых папок тоже будут удалены."}.into());
                }
                Err(e) => ui.set_panel_body(format!("Не удалось прочитать список: {e}").into()),
            }
        },
    );
}

pub(super) fn toggle(ui: &AppWindow, id: &str) {
    let rows = ui.get_orphans();
    for i in 0..rows.row_count() {
        if let Some(mut row) = rows.row_data(i)
            && row.id == id
            && row.result.is_empty()
        {
            row.selected = !row.selected;
            rows.set_row_data(i, row);
            break;
        }
    }
    ui.set_orphan_selection_count(
        rows.iter()
            .filter(|r| r.selected && r.result.is_empty())
            .count() as i32,
    );
}

pub(super) fn confirm_selected(ui: &AppWindow, state: &State) {
    let rows = ui
        .get_orphans()
        .iter()
        .filter(|r| r.selected && r.result.is_empty())
        .collect::<Vec<_>>();
    if rows.is_empty() || ui.get_operation_busy() {
        return;
    }
    let paths = rows
        .iter()
        .map(|r| r.path.to_string())
        .collect::<Vec<_>>()
        .join("\n");
    controller::confirm(
        ui,
        state,
        controller::Confirmation::DeleteOrphans(rows),
        "Удалить выбранные игры?",
        format!(
            "Будут удалены только выбранные папки:\n{paths}\n\nСохранения и вручную добавленные файлы внутри них тоже будут удалены. Остальные игры останутся на месте."
        ),
        "Удалить выбранные",
    );
}

pub(super) fn delete_selected(ui: &AppWindow, state: &State, rows: Vec<OrphanRow>) {
    let services = state.borrow().services.clone();
    let records = state.borrow().records.clone();
    ui.set_panel_kind("orphans".into());
    ui.set_panel_title("Удаление выбранных игр".into());
    ui.set_panel_body("Удаляем только подтверждённые папки. Дождитесь результата.".into());
    ui.set_orphan_selection_count(0);
    ui.set_operation_busy(true);
    let requested = rows
        .iter()
        .map(|r| (r.id.to_string(), PathBuf::from(r.path.as_str())))
        .collect::<Vec<_>>();
    ui.set_orphans(Rc::new(VecModel::from(rows)).into());
    let weak = ui.as_weak();
    controller::background_for(
        ui,
        controller::Activity::Operation,
        move || {
            let mut results = Vec::new();
            for (i, (id, path)) in requested.into_iter().enumerate() {
                let result=chillhub_native::library::orphaned(&services.paths,&records).and_then(|current|{
                if !current.iter().any(|r|r.id==id&&r.path==path){return Err("Папка изменилась или игра снова есть в каталоге. Удаление пропущено.".into());}
                services.delete_local(&GameRecord{game_id:id.clone(),title:id.clone(),..Default::default()})
            }).map(|_|"Удалено".to_string()).unwrap_or_else(|e|format!("Не удалено: {e}"));
                chillhub_native::diagnostics::event(
                    &services.paths.data_dir,
                    "game.orphan-delete",
                    serde_json::json!({"gameId":id,"result":result}),
                );
                results.push((id.clone(), result.clone()));
                let weak = weak.clone();
                let _ = slint::invoke_from_event_loop(move || {
                    if let Some(ui) = weak.upgrade() {
                        let model = ui.get_orphans();
                        if let Some(mut row) = model.row_data(i) {
                            row.result = result.into();
                            row.selected = false;
                            model.set_row_data(i, row);
                        }
                        ui.set_panel_body(
                            format!("Обработано папок: {} из {}", i + 1, model.row_count()).into(),
                        );
                    }
                });
            }
            chillhub_native::installed_apps::refresh(
                &services.paths.games_dir,
                &services.paths.data_dir,
            );
            results
        },
        |ui, state, _| {
            ui.set_operation_busy(false);
            ui.set_panel_body("Обработка завершена. Результат указан у каждой папки.".into());
            controller::refresh_choice(ui, state);
        },
    );
}
