use super::{
    background::{background, operation},
    choice::refresh_choice,
    confirmation::{Confirmation, confirm},
    library::{refresh_disk, refresh_library_states, sync_library_rows},
    shortcut_install,
};
use crate::{ACTIVE_STATE, AppState, AppWindow, State, desktop, queue_ui, security_offers, toast};
use chillhub_native::{
    diagnostics::Metric,
    queue::{QueueEvent, QueueItem, QueueState},
};
use slint::{ComponentHandle, Image};

pub(super) fn subscribe(ui: &AppWindow, state: &State) {
    let receiver = state.borrow().services.queue.subscribe();
    let weak = ui.as_weak();
    // At most one full progress snapshot waits for the UI. Terminal results and
    // errors remain separate events and cannot be swallowed by a newer progress tick.
    let pending = std::sync::Arc::new(std::sync::Mutex::new((None::<Vec<QueueItem>>, false)));
    std::thread::spawn(move || {
        while let Ok(event) = receiver.recv() {
            let weak = weak.clone();
            match event {
                QueueEvent::Changed(tasks) => {
                    {
                        let mut slot = pending.lock().unwrap_or_else(|e| e.into_inner());
                        slot.0 = Some(tasks);
                        if slot.1 {
                            continue;
                        }
                        slot.1 = true;
                    }
                    let pending = pending.clone();
                    if slint::invoke_from_event_loop(move || {
                        // Clear the flag while holding the snapshot lock: a publisher
                        // either joins this snapshot or schedules the next callback.
                        let tasks = {
                            let mut slot = pending.lock().unwrap_or_else(|e| e.into_inner());
                            let tasks = slot.0.take();
                            slot.1 = false;
                            tasks
                        };
                        if let (Some(ui), Some(tasks)) = (weak.upgrade(), tasks) {
                            ACTIVE_STATE.with(|slot| {
                                if let Some(state) = slot.borrow().as_ref() {
                                    queue_changed(&ui, state, tasks);
                                }
                            });
                        }
                    })
                    .is_err()
                    {
                        break;
                    }
                }
                event => {
                    if slint::invoke_from_event_loop(move || {
                        if let Some(ui) = weak.upgrade() {
                            ACTIVE_STATE.with(|slot| {
                                if let Some(state) = slot.borrow().as_ref() {
                                    match event {
                                        QueueEvent::Finished(task) => {
                                            queue_finished(&ui, state, *task)
                                        }
                                        QueueEvent::PersistenceError(error) => {
                                            queue_persistence_failed(&ui, state, &error)
                                        }
                                        QueueEvent::Changed(_) => unreachable!(),
                                    }
                                }
                            });
                        }
                    })
                    .is_err()
                    {
                        break;
                    }
                }
            }
        }
    });
}

fn queue_persistence_failed(ui: &AppWindow, state: &State, error: &str) {
    let services = state.borrow().services.clone();
    let unreadable = services.queue.recovery_error().is_some();
    ui.set_queue_recovery_needed(unreadable);
    if unreadable {
        ui.set_queue_error("Не удалось открыть сохранённую очередь загрузок.".into());
        chillhub_native::diagnostics::log(&services.paths.data_dir, "queue-recovery", error);
        toast(
            ui,
            state,
            "Очередь повреждена. Её можно сбросить в разделе «Загрузки».",
        );
    } else {
        ui.set_queue_error(error.into());
        toast(ui, state, &format!("Не удалось сохранить очередь: {error}"));
    }
}

fn queue_finished(ui: &AppWindow, state: &State, task: QueueItem) {
    state.borrow_mut().shortcut_installs.observe_finished(&task);
    let s = state.borrow();
    if task.state == QueueState::Completed {
        chillhub_native::installed_apps::refresh(
            &s.services.paths.games_dir,
            &s.services.paths.data_dir,
        );
        if task.report.metric_event(task.request.scope) == Some("game_install")
            && let Some(game) = s
                .records
                .iter()
                .find(|g| g.game_id == task.request.game_id)
                .cloned()
        {
            let paths = s.services.paths.clone();
            background(
                ui,
                move || chillhub_native::shortcuts::create_after_install(&paths, &game),
                |_, state, result| {
                    if let Err(error) = result {
                        chillhub_native::diagnostics::log(
                            &state.borrow().services.paths.data_dir,
                            "game-shortcut",
                            &error,
                        );
                    }
                },
            );
        }
    }
    if !ui.window().is_visible() && matches!(task.state, QueueState::Completed | QueueState::Failed)
    {
        let title = s
            .records
            .iter()
            .find(|game| game.game_id == task.request.game_id)
            .map(|game| game.title.as_str())
            .unwrap_or(&task.request.game_id);
        let message = if task.state == QueueState::Completed {
            format!("{title}: загрузка завершена. Откройте лаунчер, чтобы играть.")
        } else {
            format!("{title}: загрузку не удалось завершить. Подробности доступны в очереди.")
        };
        desktop::notify("Chill Hub", &message, task.state == QueueState::Failed);
    }
    if task.state == QueueState::Failed
        && !task.report.transient_error
        && let Some(error) = &task.error
    {
        chillhub_native::diagnostics::report_error(
            &s.services.api,
            &s.services.paths.data_dir,
            "download",
            error,
        );
    }
    report_queue_metrics(&task, |metric| {
        chillhub_native::diagnostics::metric(&s.services.api, &s.services.paths.data_dir, metric);
    });
    drop(s);
    if task.state == QueueState::Completed
        && task.request.scope == chillhub_native::transfer::SyncScope::OwnFilesOnly
    {
        security_offers::installed_mods(ui, state, task.request.root);
    }
    shortcut_install::pump(ui, state);
}

fn report_queue_metrics(task: &QueueItem, mut report: impl FnMut(Metric)) {
    let result = match task.state {
        QueueState::Completed => "ok",
        QueueState::Cancelled => "cancel",
        _ => "fail",
    };
    let bytes = task
        .outcome
        .as_ref()
        .map(|o| o.network_bytes)
        .unwrap_or_else(|| {
            task.progress
                .as_ref()
                .filter(|p| p.stage != chillhub_native::transfer::TransferStage::Checking)
                .map_or(0, |p| p.network_bytes)
        });
    if let Some(event) = task.report.metric_event(task.request.scope) {
        report(Metric {
            event: event.into(),
            game_id: task.request.game_id.clone(),
            version: task.request.version.clone(),
            result: result.into(),
            bytes,
            duration_ms: task.report.duration_ms,
            files_downloaded: task.report.files_downloaded,
            files_total: task.report.files_total,
            full_bytes: task.report.full_bytes,
            hash_mismatches: task.report.hash_mismatches,
            ..Default::default()
        });
    }
    if task.state == QueueState::Failed
        && let Some(error_code) = &task.report.error_code
    {
        report(Metric {
            event: "error".into(),
            game_id: task.request.game_id.clone(),
            version: task.request.version.clone(),
            result: "fail".into(),
            error_code: error_code.clone(),
            ..Default::default()
        });
    }
}

fn refresh_queue_rows_for(ui: &AppWindow, state: &AppState, tasks: &[QueueItem]) {
    let rows = queue_ui::rows(tasks, &state.queue_speeds, |id| {
        if let Some((index, game)) = state
            .catalog
            .iter()
            .enumerate()
            .find(|(_, game)| game.id == id)
        {
            return (
                game.title.as_str().into(),
                state.icons.get(index).cloned().unwrap_or_default(),
            );
        }
        let title = state
            .records
            .iter()
            .find(|game| game.game_id == id)
            .map_or(id, |game| game.title.as_str());
        (title.into(), Image::default())
    });
    queue_ui::apply_rows(ui, rows);
}

pub(crate) fn refresh_queue_rows(ui: &AppWindow, state: &State) {
    let state = state.borrow();
    refresh_queue_rows_for(ui, &state, &state.tasks);
}

fn queue_changed(ui: &AppWindow, state: &State, tasks: Vec<QueueItem>) {
    state
        .borrow_mut()
        .queue_speeds
        .update(&tasks, std::time::Instant::now());
    let changed = {
        let s = state.borrow();
        s.tasks.iter().map(|t| (&t.id, t.state)).collect::<Vec<_>>()
            != tasks.iter().map(|t| (&t.id, t.state)).collect::<Vec<_>>()
    };
    refresh_queue_rows_for(ui, &state.borrow(), &tasks);
    let active_count = tasks
        .iter()
        .filter(|t| {
            !matches!(
                t.state,
                QueueState::Completed | QueueState::Cancelled | QueueState::Failed
            )
        })
        .count();
    ui.set_downloads_active(active_count as i32);
    let active = queue_ui::active_task(&tasks)
        .or_else(|| tasks.iter().find(|task| task.state == QueueState::Waiting));
    let summary = active
        .map(|t| {
            let label = queue_ui::state_label(t);
            if let Some(progress) = queue_ui::stage_progress(t) {
                format!("{label} · {}%", (progress * 100.).floor())
            } else {
                label.into()
            }
        })
        .unwrap_or_else(|| {
            if tasks.iter().any(|t| t.state == QueueState::Failed) {
                "Есть незавершённая загрузка".into()
            } else {
                String::new()
            }
        });
    ui.set_downloads_summary(summary.clone().into());
    ui.set_downloads_progress(active.and_then(queue_ui::stage_progress).unwrap_or(0.));
    desktop::set_status(&if summary.is_empty() {
        "Chill Hub".into()
    } else {
        format!("Chill Hub · {summary}")
    });
    if ui.get_queue_recovery_needed() && state.borrow().services.queue.recovery_error().is_none() {
        ui.set_queue_recovery_needed(false);
        ui.set_queue_error(Default::default());
    }
    state.borrow_mut().tasks = tasks;
    shortcut_install::pump(ui, state);
    sync_library_rows(ui, state);
    if changed {
        refresh_library_states(ui, state);
        refresh_choice(ui, state);
        refresh_disk(ui, state);
    }
}

pub(super) fn library_task<'a>(tasks: &'a [QueueItem], game_id: &str) -> Option<&'a QueueItem> {
    let belongs = |task: &&QueueItem| task.request.game_id == game_id;
    tasks
        .iter()
        .filter(belongs)
        .filter(|task| !queue_ui::can_remove(task.state))
        .min_by_key(|task| match task.state {
            QueueState::Running | QueueState::Pausing | QueueState::Cancelling => 0,
            QueueState::NeedsConfirmation => 1,
            QueueState::Waiting => 2,
            _ => 3,
        })
        .or_else(|| {
            tasks
                .iter()
                .rev()
                .find(belongs)
                .filter(|task| task.state == QueueState::Failed)
        })
}

fn library_queue_kind(task: &QueueItem) -> &'static str {
    match task.state {
        QueueState::Failed => "error",
        QueueState::NeedsConfirmation => "confirmation",
        QueueState::Paused | QueueState::Pausing => "paused",
        QueueState::Waiting => "queued",
        QueueState::Running => match task.progress.as_ref().map(|p| p.stage) {
            Some(
                chillhub_native::transfer::TransferStage::Checking
                | chillhub_native::transfer::TransferStage::Verifying,
            ) => "checking",
            _ if task.report.operation
                == Some(chillhub_native::queue::TransferOperation::Verify) =>
            {
                "recovery"
            }
            _ => "active",
        },
        _ => "active",
    }
}

fn library_queue_label(task: &QueueItem) -> &'static str {
    match task.state {
        QueueState::Failed => "Ошибка загрузки",
        QueueState::NeedsConfirmation => "Нужен выбор",
        QueueState::Pausing => "Ставим на паузу",
        QueueState::Running => match task.progress.as_ref().map(|progress| progress.stage) {
            Some(
                chillhub_native::transfer::TransferStage::Checking
                | chillhub_native::transfer::TransferStage::Verifying,
            ) => "Проверяем",
            Some(chillhub_native::transfer::TransferStage::Activating) => "Устанавливаем",
            _ if library_queue_kind(task) == "recovery" => "Восстанавливаем",
            _ => queue_ui::state_label(task),
        },
        _ => queue_ui::state_label(task),
    }
}

pub(super) fn library_status(task: &QueueItem) -> (&'static str, &'static str) {
    (library_queue_label(task), library_queue_kind(task))
}

pub(super) fn handle_action(ui: &AppWindow, state: &State, id: &str, value: &str) {
    let id = id.to_string();
    let queue = state.borrow().services.queue.clone();
    if value == "reset" {
        if !ui.get_operation_busy() && queue.recovery_error().is_some() {
            confirm(
                ui,
                state,
                Confirmation::ResetQueue,
                "Сбросить очередь загрузок?",
                "Лаунчер создаст пустую очередь и сохранит резервную копию старого списка. Игры и уже скачанные файлы останутся на диске.".into(),
                "Сбросить очередь",
            );
        }
        return;
    }
    if value == "security" {
        let task = state
            .borrow()
            .tasks
            .iter()
            .find(|t| {
                t.id == id
                    && t.state == QueueState::Failed
                    && t.report.error_code.as_deref() == Some("sync_security_blocked")
            })
            .cloned();
        if let Some(task) = task {
            confirm(
                ui,
                state,
                Confirmation::Security(vec![task.request.root.clone()]),
                "Файл заблокирован защитой",
                format!(
                    "{}\n\nПомощник покажет состояние защиты и доступные действия для папки:\n{}\n\nНастройки не меняются без отдельного подтверждения.",
                    task.error.unwrap_or_default(),
                    task.request.root.display()
                ),
                "Открыть помощник",
            );
        }
        return;
    }
    if value == "confirm" {
        let task = state.borrow().tasks.iter().find(|t| t.id == id).cloned();
        if let Some(task) = task {
            let more = task.cleanup_files.len().saturating_sub(25);
            let mut list = task
                .cleanup_files
                .iter()
                .take(25)
                .map(|file| file.path.as_str())
                .collect::<Vec<_>>()
                .join("\n");
            if more > 0 {
                list.push_str(&format!("\n…и ещё {more}"));
            }
            confirm(
                ui,
                state,
                Confirmation::Cleanup(id, task.cleanup_files),
                "Удалить лишние файлы?",
                format!(
                    "В папке {} найдены файлы, которых нет в выбранной версии:\n\n{list}\n\nОни будут удалены. При необходимости сначала скопируйте свои сохранения и моды.",
                    task.request.root.display()
                ),
                "Удалить и продолжить",
            );
        }
        return;
    }
    let value = value.to_string();
    operation(ui, move || {
        match value.as_str() {
            "pause" => queue.pause(&id),
            "resume" => queue.resume(&id),
            "cancel" => queue.cancel(&id),
            "remove" => queue.remove(&id),
            "move-up" | "move-down" => {
                match queue_ui::move_target(&queue.snapshot(), &id, value == "move-up") {
                    Some(before) => queue.move_before(&id, before.as_deref()),
                    None => Ok(()),
                }
            }
            _ => Ok(()),
        }
        .map(|_| "Очередь изменена".into())
        .map_err(|e| e.to_string())
    });
}

#[cfg(test)]
#[path = "queue_tests.rs"]
mod tests;
