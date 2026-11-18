//! Deferred offers never open another window or change antivirus settings.
use super::*;
use std::collections::VecDeque;

#[derive(Default)]
pub(super) struct Offers {
    pending: VecDeque<Option<PathBuf>>,
    initial_queued: bool,
    checking: bool,
}

pub(super) fn catalog_ready(ui: &AppWindow, state: &State) {
    {
        let mut s = state.borrow_mut();
        if !s.security_offers.initial_queued {
            s.security_offers.initial_queued = true;
            s.security_offers.pending.push_back(None);
        }
    }
    pump(ui, state);
}

pub(super) fn installed_mods(ui: &AppWindow, state: &State, directory: PathBuf) {
    {
        let mut s = state.borrow_mut();
        let offer = Some(directory);
        if !s.security_offers.pending.contains(&offer) && s.security_offers.pending.len() < 32 {
            s.security_offers.pending.push_back(offer);
        }
    }
    pump(ui, state);
}

fn available(ui: &AppWindow, state: &State) -> bool {
    ui.window().is_visible()
        && !ui.window().is_minimized()
        && ui.get_panel_kind().is_empty()
        && !ui.get_operation_busy()
        && !ui.get_loading()
        && !state.borrow().update_inflight
}

pub(super) fn after_panel_closed(ui: &AppWindow, state: &State) {
    if state.borrow().security_offers.pending.is_empty() {
        return;
    }
    let weak = ui.as_weak();
    let state = Rc::downgrade(state);
    Timer::single_shot(Duration::from_millis(250), move || {
        if let (Some(ui), Some(state)) = (weak.upgrade(), state.upgrade()) {
            pump(&ui, &state);
        }
    });
}

pub(super) fn pump(ui: &AppWindow, state: &State) {
    if !available(ui, state) {
        return;
    }
    let (extra, services) = {
        let mut s = state.borrow_mut();
        if s.security_offers.checking {
            return;
        }
        let Some(extra) = s.security_offers.pending.pop_front() else {
            return;
        };
        s.security_offers.checking = true;
        (extra, s.services.clone())
    };
    let copy = extra.clone();
    let data = services.paths.data_dir.clone();
    controller::background(
        ui,
        move || {
            chillhub_native::security::should_offer_at(
                &data,
                copy.as_deref(),
                &services.paths.games_dir,
            )
        },
        move |ui, state, result| {
            state.borrow_mut().security_offers.checking = false;
            let data = state.borrow().services.paths.data_dir.clone();
            match result {
                Ok(true) if !available(ui, state) => {
                    state.borrow_mut().security_offers.pending.push_front(extra)
                }
                Ok(true) => {
                    let paths = if let Some(path) = &extra {
                        vec![path.clone()]
                    } else {
                        let services = state.borrow().services.clone();
                        let mut paths = vec![services.paths.games_dir.clone()];
                        if let Ok(update) = controller::update_paths(&services) {
                            paths.extend([update.install, update.staging]);
                        }
                        paths
                    };
                    let text = if extra.is_some() {
                        "Моды установлены в отдельную папку Steam. При необходимости помощник поможет настроить защиту для этой игры."
                    } else {
                        "Если защита Windows мешает установке или запуску, помощник покажет доступные настройки. Его можно открыть позже в настройках лаунчера."
                    };
                    controller::confirm(
                        ui,
                        state,
                        controller::Confirmation::Security(paths),
                        "Настройка защиты",
                        format!("{text}\n\nОткрытие помощника не меняет настройки антивируса."),
                        "Открыть помощник",
                    );
                    // Mark only after the actual offer became visible. All writes
                    // use the same explicit profile as the launcher and tests.
                    controller::background(
                        ui,
                        move || {
                            chillhub_native::security::acknowledge_offer_at(&data, extra.as_deref())
                        },
                        |_, state, result| {
                            if let Err(error) = result {
                                chillhub_native::diagnostics::log(
                                    &state.borrow().services.paths.data_dir,
                                    "security-offer",
                                    &error,
                                );
                            }
                        },
                    );
                }
                Ok(false) => pump(ui, state),
                Err(error) => chillhub_native::diagnostics::log(&data, "security-offer", &error),
            }
        },
    );
}
