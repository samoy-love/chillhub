#![cfg_attr(target_os = "windows", windows_subsystem = "windows")]
use chillhub_native::{
    helper_runtime::{self, HelperKind, HelperLog},
    ui::AuxiliaryWindow,
    update,
};
use slint::ComponentHandle;
use std::path::PathBuf;

fn main() -> Result<(), String> {
    let log = HelperLog::open(HelperKind::Updater);
    if let Ok(log) = &log {
        let _ = log.record("start", "Запущен модуль обновления");
    }
    let result = helper_runtime::caught(|| run(log.clone()));
    if result.is_err() {
        helper_runtime::record_result(&log, &result);
    }
    result
}

fn finish(ui: &AuxiliaryWindow, result: Result<(), String>, log: &Result<HelperLog, String>) {
    let caption = helper_runtime::record_result(log, &result);
    ui.set_busy(false);
    ui.set_can_close(true);
    match result {
        Ok(()) => {
            ui.set_status("Готово. Открываем лаунчер.".into());
            let _ = slint::quit_event_loop();
        }
        Err(error) => {
            ui.set_heading("Обновление не завершено".into());
            ui.set_details(error.into());
            ui.set_status(format!("Можно закрыть это окно.\n{caption}").into());
        }
    }
}

fn run(log: Result<HelperLog, String>) -> Result<(), String> {
    helper_runtime::select_renderer()?;
    let ui = AuxiliaryWindow::new().map_err(|e| e.to_string())?;
    ui.set_window_title("Обновление · Chill Hub".into());
    ui.set_heading("Обновление Chill Hub".into());
    ui.set_subtitle("Проверяем файлы, сохраняем предыдущую версию и перезапускаем лаунчер.".into());
    ui.set_details("Подготавливаем обновление…".into());
    ui.set_primary_text("Закрыть".into());
    ui.set_secondary_text("".into());
    ui.set_busy(true);
    ui.set_can_close(false);
    ui.set_progress_visible(true);
    let weak = ui.as_weak();
    ui.window().on_close_requested(move || {
        if weak.upgrade().is_some_and(|ui| ui.get_busy()) {
            slint::CloseRequestResponse::KeepWindowShown
        } else {
            slint::CloseRequestResponse::HideWindow
        }
    });
    ui.on_primary(|| {
        let _ = slint::quit_event_loop();
    });
    ui.on_close_requested(|| {
        let _ = slint::quit_event_loop();
    });
    let args: Vec<_> = std::env::args().skip(1).collect();
    let weak = ui.as_weak();
    let worker_log = log.clone();
    let spawn = std::thread::Builder::new().name("chillhub-update".into()).spawn(move || {
        let result = helper_runtime::caught(|| {
            if args.len() != 4 || args[0] != "--job" || args[2] != "--sha256" {
                return Err("Обновление запускается из лаунчера. Откройте Chill Hub и проверьте обновления.".to_owned());
            }
            let job = update::read_job(&PathBuf::from(&args[1]), &args[3])?;
            let recovery = job.mode == update::UpdateMode::Recovery;
            let details = if recovery {
                format!("Восстанавливаем версию {} из резервной копии.\n\nУстановка: {}\n\nНастройки и игры сохранятся.", job.staged.current_version, job.staged.paths.install.display())
            } else {
                format!("Версия {} → {}\n\nУстановка: {}\n\nНастройки и игры не входят в пакет обновления.", job.staged.current_version, job.staged.version, job.staged.paths.install.display())
            };
            if let Ok(log) = &worker_log { let _ = log.record("job", &details); }
            let _ = weak.upgrade_in_event_loop(move |ui| {
                ui.set_details(details.into());
                if recovery { ui.set_heading("Восстановление Chill Hub".into()); ui.set_subtitle("Возвращаем файлы предыдущей версии и перезапускаем лаунчер.".into()); }
            });
            let last = std::sync::Mutex::new(String::new());
            update::execute_job(&job, &|status, progress| {
                if let Ok(mut previous) = last.lock() && *previous != status {
                    if let Ok(log) = &worker_log { let _ = log.record("progress", status); }
                    *previous = status.to_owned();
                }
                let status = status.to_owned();
                let _ = weak.upgrade_in_event_loop(move |ui| { ui.set_status(status.into()); ui.set_progress(progress); });
            })
        });
        let fallback = result.clone();
        let completion_log = worker_log.clone();
        if weak.upgrade_in_event_loop(move |ui| finish(&ui, result, &completion_log)).is_err() {
            helper_runtime::record_result(&worker_log, &fallback);
        }
    });
    if let Err(error) = spawn {
        finish(
            &ui,
            Err(format!("Не удалось начать обновление: {error}")),
            &log,
        );
    }
    ui.run().map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn update_failure_unlocks_window_and_points_to_actual_log() {
        i_slint_backend_testing::init_integration_test_with_system_time();
        let temp = tempfile::tempdir().unwrap();
        let log = HelperLog::in_directory(temp.path(), HelperKind::Updater).unwrap();
        let ui = AuxiliaryWindow::new().unwrap();
        ui.set_busy(true);
        ui.set_can_close(false);
        finish(
            &ui,
            Err("fixture: invalid update job".into()),
            &Ok(log.clone()),
        );
        assert!(!ui.get_busy());
        assert!(ui.get_can_close());
        assert!(ui.get_details().contains("invalid update job"));
        assert!(ui.get_status().contains(log.path().to_str().unwrap()));
        assert!(
            std::fs::read_to_string(log.path())
                .unwrap()
                .contains("invalid update job")
        );
        finish(
            &ui,
            Err("fixture error".into()),
            &Err("fixture log unavailable".into()),
        );
        assert!(ui.get_status().contains("Журнал недоступен"));
    }
}
