#![cfg_attr(target_os = "windows", windows_subsystem = "windows")]
use chillhub_native::{
    helper_runtime::{self, HelperKind, HelperLog},
    ui::AuxiliaryWindow,
    update,
};
use slint::ComponentHandle;

fn main() -> Result<(), String> {
    if std::env::args().nth(1).as_deref() == Some("--setup-ui") {
        return chillhub_native::setup::run(std::env::args().skip(2).collect());
    }
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

fn finish(ui: &AuxiliaryWindow, result: Result<(), String>, caption: &str) {
    ui.set_busy(false);
    ui.set_can_close(true);
    ui.set_error(result.is_err());
    ui.set_progress_visible(false);
    match result {
        Ok(()) => {
            ui.set_status("Готово. Открываем лаунчер.".into());
            let _ = slint::quit_event_loop();
        }
        Err(error) => {
            ui.set_heading("Обновление не завершено".into());
            ui.set_paths("".into());
            ui.set_details(error.into());
            ui.set_status(format!("Можно закрыть это окно.\n{caption}").into());
        }
    }
}

fn run(log: Result<HelperLog, String>) -> Result<(), String> {
    helper_runtime::select_renderer()?;
    let ui = AuxiliaryWindow::new().map_err(|e| e.to_string())?;
    helper_runtime::style_window(ui.window());
    let args: Vec<_> = std::env::args().skip(1).collect();
    start(
        &ui,
        log,
        move || update::job_from_args(&args),
        update::execute_job,
    );
    ui.run().map_err(|e| e.to_string())
}

fn start(
    ui: &AuxiliaryWindow,
    log: Result<HelperLog, String>,
    load_job: impl FnOnce() -> Result<update::UpdateJob, String> + Send + 'static,
    execute: impl FnOnce(&update::UpdateJob, &(dyn Fn(&str, f32) + Sync)) -> Result<(), String>
    + Send
    + 'static,
) {
    ui.set_window_title("Обновление · Chill Hub".into());
    ui.set_heading("Обновление Chill Hub".into());
    ui.set_subtitle("Обновляем лаунчер. Игры и настройки сохранятся.".into());
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
    let weak = ui.as_weak();
    let worker_log = log.clone();
    let spawn = std::thread::Builder::new().name("chillhub-update".into()).spawn(move || {
        let result = helper_runtime::caught(|| {
            let job = load_job()?;
            let recovery = job.mode == update::UpdateMode::Recovery;
            let details = if recovery {
                format!("Восстанавливаем версию {} из резервной копии. Настройки и игры сохранятся.", job.staged.current_version)
            } else {
                format!("Версия {} → {}. Настройки и игры не входят в пакет обновления.", job.staged.current_version, job.staged.version)
            };
            if let Ok(log) = &worker_log { let _ = log.record("job", &details); }
            let install_path = job.staged.paths.install.display().to_string();
            let _ = weak.upgrade_in_event_loop(move |ui| {
                ui.set_details(details.into());
                ui.set_paths(install_path.into());
                if recovery { ui.set_heading("Восстановление Chill Hub".into()); ui.set_subtitle("Возвращаем файлы предыдущей версии и перезапускаем лаунчер.".into()); }
            });
            let last = std::sync::Mutex::new(String::new());
            execute(&job, &|status, progress| {
                if let Ok(mut previous) = last.lock() && *previous != status {
                    if let Ok(log) = &worker_log { let _ = log.record("progress", status); }
                    *previous = status.to_owned();
                }
                let status = status.to_owned();
                let _ = weak.upgrade_in_event_loop(move |ui| { ui.set_status(status.into()); ui.set_progress(progress); });
            })
        });
        // Persist the outcome even when the window disappears before the
        // event loop can deliver the final UI update.
        let caption = helper_runtime::record_result(&worker_log, &result);
        let _ = weak.upgrade_in_event_loop(move |ui| finish(&ui, result, &caption));
    });
    if let Err(error) = spawn {
        let result = Err(format!("Не удалось начать обновление: {error}"));
        let caption = helper_runtime::record_result(&log, &result);
        finish(ui, result, &caption);
    }
}

#[cfg(test)]
#[path = "tests/updater_tests.rs"]
mod tests;
