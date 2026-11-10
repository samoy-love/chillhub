#![cfg_attr(target_os = "windows", windows_subsystem = "windows")]
use chillhub_native::{
    helper_runtime::{self, HelperKind, HelperLog},
    security::{self, SecurityOperation},
    ui::AuxiliaryWindow,
};
use slint::ComponentHandle;

fn present_request(
    ui: &AuxiliaryWindow,
    request: &security::SecurityRequest,
    elevated: bool,
    provider: usize,
) {
    let guide = &security::guides()[provider.min(security::guides().len() - 1)];
    let paths = request
        .paths
        .iter()
        .map(|p| p.display().to_string())
        .collect::<Vec<_>>()
        .join("\n");
    let warning = if request.operation == SecurityOperation::Add {
        security::WARNING
    } else {
        "Удаляются только исключения, добавленные Chill Hub. Другие записи не изменятся."
    };
    ui.set_details(format!("{warning}\n\nПапки:\n{paths}\n\n{}\n\n{}", guide.instructions, if provider==0 { if elevated {"Проверьте пути и подтвердите действие. При отказе изменений не будет."} else {"Автоматическая настройка относится только к Microsoft Defender. Windows запросит разрешение; затем вы подтвердите конкретные папки."} } else {"Chill Hub не меняет настройки этого антивируса и не может подтвердить ручную настройку. После завершения повторите загрузку."}).into());
    ui.set_secondary_text("Открыть инструкцию".into());
    ui.set_secondary_enabled(!guide.url.is_empty());
    ui.set_primary_enabled(provider == 0);
}

fn main() -> Result<(), String> {
    let log = HelperLog::open(HelperKind::Security);
    if let Ok(log) = &log {
        let _ = log.record("start", "Запущен модуль настройки защиты");
    }
    let result = helper_runtime::caught(|| run(log.clone()));
    if result.is_err() {
        helper_runtime::record_result(&log, &result);
    }
    result
}

fn finish(
    ui: &AuxiliaryWindow,
    result: Result<String, String>,
    elevated: bool,
    log: &Result<HelperLog, String>,
) {
    let caption =
        helper_runtime::record_result(log, &result.as_ref().map(|_| ()).map_err(Clone::clone));
    ui.set_busy(false);
    ui.set_can_close(true);
    if !elevated {
        match result {
            Ok(_) => {
                let _ = slint::quit_event_loop();
            }
            Err(error) => ui.set_status(format!("{error}\n{caption}").into()),
        }
        return;
    }
    ui.set_primary_text("Закрыть".into());
    ui.set_primary_enabled(true);
    ui.set_details(
        match result {
            Ok(text) => text,
            Err(error) => format!(
                "Не удалось завершить настройку.\n\n{error}\n\nМожно проверить состояние вручную."
            ),
        }
        .into(),
    );
    ui.set_status(format!("Проверьте результат для каждой папки.\n{caption}").into());
    ui.on_primary(|| {
        let _ = slint::quit_event_loop();
    });
}

fn run(log: Result<HelperLog, String>) -> Result<(), String> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    let elevated_session = security::elevated_request_from_args(&args)?;
    let mut parsed = match elevated_session
        .as_ref()
        .map(|session| Ok(Some(session.request())))
        .unwrap_or_else(|| security::request_from_args(&args))
    {
        Ok(None) => return Ok(()),
        Ok(Some(request)) => Ok(request),
        Err(error) => Err(error),
    };
    let _lifetime = if elevated_session.is_none() {
        match security::SecurityLifetime::acquire() {
            Ok(lifetime) => Some(lifetime),
            Err(error) => {
                parsed = Err(error);
                None
            }
        }
    } else {
        None
    };
    helper_runtime::select_renderer()?;
    let ui = AuxiliaryWindow::new().map_err(|e| e.to_string())?;
    helper_runtime::style_window(ui.window());
    ui.set_window_title("Настройка защиты · Chill Hub".into());
    ui.set_heading("Исключения антивируса".into());
    ui.set_subtitle(
        "Изменение касается только выбранных папок и требует вашего подтверждения.".into(),
    );
    ui.set_secondary_text("Настроить вручную".into());
    let elevated = security::is_elevated();
    let weak = ui.as_weak();
    ui.window().on_close_requested(move || {
        if weak.upgrade().is_some_and(|ui| ui.get_busy()) {
            slint::CloseRequestResponse::KeepWindowShown
        } else {
            slint::CloseRequestResponse::HideWindow
        }
    });
    ui.on_close_requested(|| {
        let _ = slint::quit_event_loop();
    });
    let weak = ui.as_weak();
    let manual_log = log.clone();
    ui.on_secondary(move || {
        let provider = weak
            .upgrade()
            .map(|ui| ui.get_selected_provider().max(0) as usize)
            .unwrap_or(0);
        let result = security::open_guide(provider);
        if let Err(error) = &result {
            let caption = helper_runtime::record_result(&manual_log, &result);
            if let Some(ui) = weak.upgrade() {
                ui.set_status(format!("{error}\n{caption}").into());
            }
        }
    });
    let introduction = parsed
        .as_ref()
        .is_ok_and(|r| r.operation == SecurityOperation::Add);
    match parsed {
        Err(error) => {
            let caption = helper_runtime::record_result(&log, &Err(error.clone()));
            ui.set_details(error.into());
            ui.set_status(caption.into());
            ui.set_primary_text("Закрыть".into());
            ui.on_primary(|| {
                let _ = slint::quit_event_loop();
            });
        }
        Ok(request) => {
            let verb = if request.operation == SecurityOperation::Add {
                "Добавить исключения"
            } else {
                "Удалить эти исключения"
            };
            let selected = if elevated || request.operation == SecurityOperation::Remove {
                0
            } else {
                security::recommended_guide()
            };
            ui.set_provider_names(
                std::rc::Rc::new(slint::VecModel::from(
                    security::guides()
                        .iter()
                        .map(|g| slint::SharedString::from(g.name.as_str()))
                        .collect::<Vec<_>>(),
                ))
                .into(),
            );
            ui.set_provider_visible(!elevated);
            ui.set_selected_provider(selected as i32);
            present_request(&ui, &request, elevated, selected);
            let weak = ui.as_weak();
            let provider_request = request.clone();
            ui.on_provider_changed(move |index| {
                if let Some(ui) = weak.upgrade() {
                    present_request(&ui, &provider_request, elevated, index.max(0) as usize);
                }
            });
            let weak = ui.as_weak();
            let copied_paths = request.paths.clone();
            ui.on_copy_paths(move || {
                let paths = copied_paths.clone();
                let weak = weak.clone();
                let _ = std::thread::Builder::new()
                    .name("chillhub-copy-paths".into())
                    .spawn(move || {
                        let message = security::copy_paths(&paths)
                            .map(|_| "Пути скопированы".to_string())
                            .unwrap_or_else(|e| e);
                        let _ = weak.upgrade_in_event_loop(move |ui| ui.set_status(message.into()));
                    });
            });
            ui.set_primary_text(
                if elevated {
                    verb
                } else {
                    "Продолжить…"
                }
                .into(),
            );
            let weak = ui.as_weak();
            ui.on_primary(move || {
                let Some(ui) = weak.upgrade() else {
                    return;
                };
                if ui.get_busy() || ui.get_selected_provider() != 0 {
                    return;
                }
                ui.set_busy(true);
                ui.set_can_close(false);
                ui.set_status(
                    if elevated {
                        "Windows применяет настройку. Дождитесь результата…"
                    } else {
                        "Завершите настройку в окне с подтверждением Windows…"
                    }
                    .into(),
                );
                if let Ok(log) = &log {
                    let _ = log.record(
                        if elevated {
                            "confirmed"
                        } else {
                            "elevation-requested"
                        },
                        verb,
                    );
                }
                let request = request.clone();
                let elevated_session = elevated_session.clone();
                let worker_log = log.clone();
                let worker_ui = weak.clone();
                let spawn = std::thread::Builder::new()
                    .name("chillhub-security".into())
                    .spawn(move || {
                        let result = helper_runtime::caught(|| {
                            if !elevated {
                                return security::elevate_helper(&request).map(|_| String::new());
                            }
                            let results = match elevated_session {
                                Some(session) => session.apply_confirmed()?,
                                None => security::apply_confirmed(&request)?,
                            };
                            if let Ok(log) = &worker_log {
                                for result in &results {
                                    let _ = log.record(
                                        &result.status,
                                        &format!("{}: {}", result.path, result.detail),
                                    );
                                }
                            }
                            Ok(results
                                .into_iter()
                                .map(|r| format!("{}\n{}", r.path, r.detail))
                                .collect::<Vec<_>>()
                                .join("\n\n"))
                        });
                        let fallback = result.as_ref().map(|_| ()).map_err(Clone::clone);
                        let completion_log = worker_log.clone();
                        if worker_ui
                            .upgrade_in_event_loop(move |ui| {
                                finish(&ui, result, elevated, &completion_log)
                            })
                            .is_err()
                        {
                            helper_runtime::record_result(&worker_log, &fallback);
                        }
                    });
                if let Err(error) = spawn {
                    finish(
                        &ui,
                        Err(format!("Не удалось начать настройку: {error}")),
                        elevated,
                        &log,
                    );
                }
            });
        }
    }
    ui.show().map_err(|e| e.to_string())?;
    if !elevated && introduction {
        let _ = security::mark_introduction_shown();
    }
    ui.run().map_err(|e| e.to_string())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn security_results_keep_errors_visible_and_allow_close_without_changes() {
        i_slint_backend_testing::init_integration_test_with_system_time();
        let temp = tempfile::tempdir().unwrap();
        let log = HelperLog::in_directory(temp.path(), HelperKind::Security).unwrap();
        let ui = AuxiliaryWindow::new().unwrap();
        ui.set_busy(true);
        ui.set_can_close(false);
        finish(
            &ui,
            Err("fixture: operation failed".into()),
            true,
            &Ok(log.clone()),
        );
        assert!(!ui.get_busy());
        assert!(ui.get_can_close());
        assert!(ui.get_primary_enabled());
        assert_eq!(ui.get_primary_text(), "Закрыть");
        assert!(ui.get_details().contains("operation failed"));
        assert!(
            std::fs::read_to_string(log.path())
                .unwrap()
                .contains("operation failed")
        );
        finish(
            &ui,
            Err("fixture: elevation cancelled".into()),
            false,
            &Ok(log),
        );
        assert!(ui.get_status().contains("elevation cancelled"));
        assert!(!temp.path().join("security-exclusions.json").exists());
    }
}
