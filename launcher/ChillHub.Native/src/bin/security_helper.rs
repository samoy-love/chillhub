#![cfg_attr(target_os = "windows", windows_subsystem = "windows")]
use chillhub_native::{
    helper_runtime::{self, HelperKind, HelperLog},
    security::{self, SecurityOperation},
    ui::AuxiliaryWindow,
};
use slint::ComponentHandle;
use std::{
    cell::Cell,
    path::PathBuf,
    rc::Rc,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
};

struct Form {
    initial: security::SecurityRequest,
    owned: Option<security::SecurityRequest>,
    removing_owned: Cell<bool>,
    previous_provider: Cell<i32>,
    provider_chosen: Arc<AtomicBool>,
}

impl Form {
    fn new(initial: security::SecurityRequest, owned: Option<security::SecurityRequest>) -> Self {
        Self {
            initial,
            owned,
            removing_owned: Cell::new(false),
            previous_provider: Cell::new(0),
            provider_chosen: Arc::new(AtomicBool::new(false)),
        }
    }

    fn request(&self) -> &security::SecurityRequest {
        self.owned
            .as_ref()
            .filter(|_| self.removing_owned.get())
            .unwrap_or(&self.initial)
    }
}

fn present_request(
    ui: &AuxiliaryWindow,
    request: &security::SecurityRequest,
    elevated: bool,
    provider: usize,
) {
    let guide = &security::guides()[provider.min(security::guides().len() - 1)];
    let folders = request
        .paths
        .iter()
        .map(|p| p.display().to_string())
        .collect::<Vec<_>>();
    let adding = request.operation == SecurityOperation::Add;
    let manual = adding && provider != 0;
    let explanation = if manual {
        guide.instructions.as_str()
    } else if adding {
        "Антивирус перестанет проверять эти папки. Добавляйте только файлы, которым доверяете."
    } else {
        "Удалим только исключения Chill Hub. Остальные записи не изменятся."
    };
    ui.set_paths(if manual {
        "".into()
    } else {
        folders.join("\n").into()
    });
    ui.set_folders(if adding && !elevated && guide.id != "mcafee" {
        slint::ModelRc::new(slint::VecModel::from(
            folders
                .into_iter()
                .map(slint::SharedString::from)
                .collect::<Vec<_>>(),
        ))
    } else {
        Default::default()
    });
    ui.set_selected_folder(0);
    ui.set_copy_status("".into());
    ui.set_error(false);
    ui.set_details(explanation.into());
    ui.set_status(
        if !manual && !elevated {
            "Windows запросит разрешение администратора."
        } else {
            ""
        }
        .into(),
    );
    let primary = if manual {
        if guide.url.is_empty() {
            ""
        } else {
            "Открыть инструкцию"
        }
    } else if adding {
        "Добавить исключения"
    } else {
        "Удалить исключения"
    };
    ui.set_primary_text(primary.into());
    ui.set_primary_enabled(!primary.is_empty() && (manual || !request.paths.is_empty()));
    ui.set_secondary_text(
        if manual {
            "Закрыть"
        } else if adding && !elevated && !guide.url.is_empty() {
            "Инструкция"
        } else {
            ""
        }
        .into(),
    );
    ui.set_secondary_enabled(true);
}

fn present_form(ui: &AuxiliaryWindow, form: &Form, elevated: bool, provider: usize) {
    let adding = form.request().operation == SecurityOperation::Add;
    ui.set_provider_visible(!elevated && adding);
    ui.set_selected_provider(provider as i32);
    ui.set_owned_removal_available(!elevated && adding && form.owned.is_some());
    present_request(ui, form.request(), elevated, provider);
    if form.removing_owned.get() {
        ui.set_secondary_text("Назад".into());
    }
}

fn selected_folder(request: &security::SecurityRequest, index: i32) -> Option<PathBuf> {
    request.paths.get(usize::try_from(index).ok()?).cloned()
}

fn bind_form(
    ui: &AuxiliaryWindow,
    form: Rc<Form>,
    elevated: bool,
    log: Result<HelperLog, String>,
    copy: impl Fn(&[PathBuf]) -> Result<(), String> + Clone + Send + 'static,
) {
    let weak = ui.as_weak();
    let selection_form = form.clone();
    ui.on_provider_changed(move |provider| {
        if let Some(ui) = weak.upgrade()
            && !ui.get_busy()
            && ui.get_provider_visible()
            && let Ok(provider) = usize::try_from(provider)
            && provider < security::guides().len()
        {
            selection_form
                .provider_chosen
                .store(true, Ordering::Relaxed);
            present_form(&ui, &selection_form, elevated, provider);
        }
    });

    let weak = ui.as_weak();
    let removal_form = form.clone();
    ui.on_remove_owned(move || {
        if let Some(ui) = weak.upgrade()
            && ui.get_owned_removal_available()
            && !ui.get_busy()
        {
            removal_form.provider_chosen.store(true, Ordering::Relaxed);
            removal_form
                .previous_provider
                .set(ui.get_selected_provider());
            removal_form.removing_owned.set(true);
            present_form(&ui, &removal_form, false, 0);
        }
    });

    let weak = ui.as_weak();
    let alternate_form = form.clone();
    ui.on_secondary(move || {
        let Some(ui) = weak
            .upgrade()
            .filter(|ui| !ui.get_busy() && ui.get_secondary_enabled())
        else {
            return;
        };
        alternate_form
            .provider_chosen
            .store(true, Ordering::Relaxed);
        if alternate_form.removing_owned.replace(false) {
            let provider = alternate_form.previous_provider.get().max(0) as usize;
            present_form(&ui, &alternate_form, false, provider);
        } else if ui.get_selected_provider() != 0 {
            let _ = slint::quit_event_loop();
        } else if ui.get_provider_visible() {
            let result = security::open_guide(0);
            if let Err(error) = &result {
                let caption = helper_runtime::record_result(&log, &result);
                ui.set_status(format!("{error}\n{caption}").into());
            }
        }
    });

    let weak = ui.as_weak();
    ui.on_copy_folder(move |index| {
        let Some(ui) = weak
            .upgrade()
            .filter(|ui| !ui.get_busy() && ui.get_provider_visible())
        else {
            return;
        };
        let provider = ui.get_selected_provider();
        if provider < 0
            || security::guides()
                .get(provider as usize)
                .is_none_or(|g| g.id == "mcafee")
        {
            return;
        }
        let Some(path) = selected_folder(form.request(), index) else {
            return;
        };
        form.provider_chosen.store(true, Ordering::Relaxed);
        if provider != 0 {
            ui.set_status("".into());
        }
        let copy = copy.clone();
        let weak = weak.clone();
        let result = std::thread::Builder::new()
            .name("chillhub-copy-folder".into())
            .spawn(move || {
                let message = copy(&[path])
                    .map(|_| "Путь скопирован".to_string())
                    .unwrap_or_else(|error| error);
                let _ = weak.upgrade_in_event_loop(move |ui| {
                    if ui.get_provider_visible()
                        && ui.get_selected_provider() == provider
                        && ui.get_selected_folder() == index
                    {
                        ui.set_copy_status(message.into());
                    }
                });
            });
        if let Err(error) = result {
            ui.set_copy_status(format!("Не удалось скопировать путь: {error}").into());
        }
    });
}

fn recommend_provider(ui: &AuxiliaryWindow, form: &Form) {
    let chosen = form.provider_chosen.clone();
    let request = form.initial.clone();
    let weak = ui.as_weak();
    ui.set_status("Определяем антивирус…".into());
    let result = std::thread::Builder::new()
        .name("chillhub-antivirus-discovery".into())
        .spawn(move || {
            let provider = security::recommended_guide();
            let _ = weak.upgrade_in_event_loop(move |ui| {
                apply_recommendation(&ui, &request, provider, chosen.load(Ordering::Relaxed));
            });
        });
    if result.is_err() {
        ui.set_status("".into());
    }
}

fn apply_recommendation(
    ui: &AuxiliaryWindow,
    request: &security::SecurityRequest,
    provider: usize,
    already_chosen: bool,
) {
    if !already_chosen && ui.get_provider_visible() && !ui.get_busy() {
        ui.set_selected_provider(provider as i32);
        present_request(ui, request, false, provider);
    }
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

fn finish(ui: &AuxiliaryWindow, result: Result<String, String>, elevated: bool, caption: &str) {
    ui.set_busy(false);
    ui.set_can_close(true);
    ui.set_error(result.is_err());
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
    ui.set_secondary_text("".into());
    ui.set_paths("".into());
    ui.set_folders(Default::default());
    ui.set_provider_visible(false);
    ui.set_owned_removal_available(false);
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
    // Only the authenticated pipe handoff follows an explicit click in the
    // ordinary helper. A direct elevated launch still needs its own confirmation.
    let confirmed_handoff = elevated_session.is_some();
    let mut parsed = match elevated_session
        .as_ref()
        .map(|session| Ok(Some(session.request())))
        .unwrap_or_else(|| security::request_from_args(&args))
    {
        Ok(None) => return Ok(()),
        Ok(Some(request)) => Ok(request),
        Err(error) => Err(error),
    };
    let lifetime = if elevated_session.is_none() {
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
    ui.set_secondary_text("".into());
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
    let introduction = parsed
        .as_ref()
        .is_ok_and(|r| r.operation == SecurityOperation::Add);
    match parsed {
        Err(error) => {
            let caption = helper_runtime::record_result(&log, &Err(error.clone()));
            ui.set_details(error.into());
            ui.set_status(caption.into());
            ui.set_primary_text("Закрыть".into());
            ui.set_error(true);
            ui.on_primary(|| {
                let _ = slint::quit_event_loop();
            });
        }
        Ok(request) => {
            let discovery = !elevated && request.operation == SecurityOperation::Add;
            let owned = if discovery {
                match security::request_from_args(&["--remove".into()]) {
                    Ok(request) => request,
                    Err(error) => {
                        if let Ok(log) = &log {
                            let _ = log.record("owned-exclusions", &error);
                        }
                        None
                    }
                }
            } else {
                None
            };
            let form = Rc::new(Form::new(request, owned));
            ui.set_provider_names(
                std::rc::Rc::new(slint::VecModel::from(
                    security::guides()
                        .iter()
                        .map(|g| slint::SharedString::from(g.name.as_str()))
                        .collect::<Vec<_>>(),
                ))
                .into(),
            );
            let initial_provider = if discovery {
                security::guides().len() - 1
            } else {
                0
            };
            present_form(&ui, &form, elevated, initial_provider);
            bind_form(
                &ui,
                form.clone(),
                elevated,
                log.clone(),
                security::copy_paths,
            );
            if discovery {
                recommend_provider(&ui, &form);
            }
            bind_primary(
                &ui,
                move || form.request().clone(),
                elevated,
                log,
                move |request| {
                    if !elevated {
                        security::elevate_helper(request)?;
                        return Ok(Vec::new());
                    }
                    match &elevated_session {
                        Some(session) => session.apply_confirmed(),
                        None => security::apply_confirmed(request),
                    }
                },
                security::open_guide,
            );
        }
    }
    ui.show().map_err(|e| e.to_string())?;
    if !elevated && lifetime.is_some() {
        helper_runtime::mark_security_window(ui.window());
    }
    resume_confirmed_handoff(&ui, confirmed_handoff);
    if !elevated && introduction {
        let _ = security::mark_introduction_shown();
    }
    ui.run().map_err(|e| e.to_string())
}

fn bind_primary(
    ui: &AuxiliaryWindow,
    request_for_action: impl Fn() -> security::SecurityRequest + 'static,
    elevated: bool,
    log: Result<HelperLog, String>,
    execute: impl Fn(&security::SecurityRequest) -> Result<Vec<security::ExclusionResult>, String>
    + Clone
    + Send
    + 'static,
    open_guide: fn(usize) -> Result<(), String>,
) {
    let weak = ui.as_weak();
    ui.on_primary(move || {
        let Some(ui) = weak.upgrade() else {
            return;
        };
        if ui.get_busy() || !ui.get_primary_enabled() || ui.get_primary_text().is_empty() {
            return;
        }
        let provider = ui.get_selected_provider();
        if provider != 0 {
            if let Ok(provider) = usize::try_from(provider)
                && security::guides()
                    .get(provider)
                    .is_some_and(|guide| !guide.url.is_empty())
                && let Err(error) = open_guide(provider)
            {
                ui.set_status(error.into());
            }
            return;
        }
        let request = request_for_action();
        let verb = if request.operation == SecurityOperation::Add {
            "Добавить исключения"
        } else {
            "Удалить эти исключения"
        };
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
        let execute = execute.clone();
        let worker_log = log.clone();
        let worker_ui = weak.clone();
        let spawn = std::thread::Builder::new()
            .name("chillhub-security".into())
            .spawn(move || {
                let result = helper_runtime::caught(|| {
                    let results = execute(&request)?;
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
                let caption = completion_caption(&worker_log, &result);
                let _ = worker_ui.upgrade_in_event_loop(move |ui| {
                    finish(&ui, result, elevated, &caption);
                });
            });
        if let Err(error) = spawn {
            let result = Err(format!("Не удалось начать настройку: {error}"));
            let caption = completion_caption(&log, &result);
            finish(&ui, result, elevated, &caption);
        }
    });
}

fn completion_caption(log: &Result<HelperLog, String>, result: &Result<String, String>) -> String {
    helper_runtime::record_result(log, &result.as_ref().map(|_| ()).map_err(Clone::clone))
}

fn resume_confirmed_handoff(ui: &AuxiliaryWindow, authenticated: bool) {
    if authenticated && ui.get_primary_enabled() && !ui.get_busy() {
        ui.invoke_primary();
    }
}

#[cfg(test)]
#[path = "tests/security_helper_tests.rs"]
mod tests;
