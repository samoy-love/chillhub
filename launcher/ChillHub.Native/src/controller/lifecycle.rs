use super::{
    background::{Activity, background, background_for},
    catalog::{refresh_catalog_quietly, reload},
    choice::refresh_choice,
    downloads,
    library::{refresh_disk, refresh_library_states},
    play::watch_game,
    settings::{self, AfterSave},
    updates,
};
use crate::{ACTIVE_STATE, AppWindow, State, orphan_ui, security_offers, toast};
use slint::{ComponentHandle, Timer, TimerMode};
use std::{cell::RefCell, time::Duration};

pub(crate) fn queue_report_notice(
    ui: &AppWindow,
    state: &State,
    notice: chillhub_native::diagnostic_notifications::ReportNotice,
) {
    let text = notice.message();
    if ui.get_toast().is_empty() {
        toast(ui, state, &text);
    } else {
        let mut s = state.borrow_mut();
        if s.report_notices.len() < 8 && !s.report_notices.contains(&text) {
            s.report_notices.push_back(text);
        }
    }
}

pub(crate) fn initialize(ui: &AppWindow, state: &State) {
    crate::karaoke::bind(ui);
    settings::load(ui, state);
    let weak = ui.as_weak();
    chillhub_native::diagnostic_notifications::subscribe(
        &state.borrow().services.paths.data_dir,
        move |notice| {
            let weak = weak.clone();
            let _ = slint::invoke_from_event_loop(move || {
                if let Some(ui) = weak.upgrade() {
                    ACTIVE_STATE.with(|slot| {
                        if let Some(state) = slot.borrow().as_ref() {
                            queue_report_notice(&ui, state, notice);
                        }
                    });
                }
            });
        },
    );
    let services = state.borrow().services.clone();
    chillhub_native::installed_apps::refresh(&services.paths.games_dir, &services.paths.data_dir);
    background(
        ui,
        move || {
            let loaders = services.launch_service().recover_loader_sessions();
            (loaders, services.playtime.reconcile())
        },
        |ui, state, (loaders, playtime)| {
            if let Err(error) = loaders {
                toast(
                    ui,
                    state,
                    &format!("Не удалось восстановить состояние модов: {error}"),
                );
            }
            match playtime {
                Ok(sessions) => {
                    for s in sessions {
                        watch_game(ui, s);
                    }
                }
                Err(e) => toast(
                    ui,
                    state,
                    &format!("Не удалось восстановить время игры: {e}"),
                ),
            }
        },
    );
    downloads::subscribe(ui, state);
    let feedback = state.borrow().services.feedback.clone();
    background(
        ui,
        move || feedback.flush(),
        |ui, state, result| {
            if let Err(error) = result {
                toast(
                    ui,
                    state,
                    &format!("Не удалось прочитать сохранённые сообщения: {error}"),
                );
            }
        },
    );
    let weak = ui.as_weak();
    state.borrow().maintenance_timer.start(
        TimerMode::Repeated,
        Duration::from_secs(60),
        move || {
            if let Some(ui) = weak.upgrade() {
                ACTIVE_STATE.with(|slot| {
                    if let Some(state) = slot.borrow().as_ref() {
                        poll_services(&ui, state);
                        refresh_catalog_quietly(&ui, state);
                    }
                });
            }
        },
    );
    reload(ui, state);
    poll_services(ui, state);
    updates::check_background(ui, state, true);
    let weak = ui.as_weak();
    state
        .borrow()
        .update_timer
        .start(TimerMode::Repeated, Duration::from_secs(600), move || {
            if let Some(ui) = weak.upgrade() {
                ACTIVE_STATE.with(|slot| {
                    if let Some(state) = slot.borrow().as_ref() {
                        updates::check_background(&ui, state, false);
                    }
                });
            }
        });
    let services = state.borrow().services.clone();
    chillhub_native::diagnostics::log(
        &services.paths.data_dir,
        "startup",
        env!("CARGO_PKG_VERSION"),
    );
    chillhub_native::diagnostics::metric(
        &services.api,
        &services.paths.data_dir,
        chillhub_native::diagnostics::Metric {
            event: "launcher_start".into(),
            ..Default::default()
        },
    );
}

fn poll_services(ui: &AppWindow, state: &State) {
    security_offers::pump(ui, state);
    orphan_ui::pump(ui, state);
    if state.borrow().poll_inflight || !ui.window().is_visible() || ui.window().is_minimized() {
        return;
    }
    state.borrow_mut().poll_inflight = true;
    let services = state.borrow().services.clone();
    background_for(
        ui,
        Activity::Poll,
        move || {
            let maintenance = services.api.maintenance();
            let _ = services.feedback.flush();
            chillhub_native::diagnostics::flush_error_reports(
                &services.api,
                &services.paths.data_dir,
            );
            maintenance
        },
        |ui, state, result| {
            state.borrow_mut().poll_inflight = false;
            if let Ok(m) = result {
                ui.set_maintenance_note(m.banner().into());
                let previous = state.borrow().maintenance.clone();
                let changed = (
                    m.enabled,
                    m.blocks.install,
                    m.blocks.update,
                    m.blocks.launch,
                ) != (
                    previous.enabled,
                    previous.blocks.install,
                    previous.blocks.update,
                    previous.blocks.launch,
                );
                state.borrow_mut().maintenance = m;
                if changed && !ui.get_busy() && !ui.get_operation_busy() {
                    refresh_choice(ui, state);
                }
            }
        },
    );
}

pub(crate) fn install_window_refresh(ui: &AppWindow) {
    use slint::winit_030::{EventResult, WinitWindowAccessor, winit::event::WindowEvent};
    let weak = ui.as_weak();
    let budget = RefCell::new(crate::frame_pacing::FrameBudget::default());
    let redraw_timer = Timer::default();
    ui.window().on_winit_window_event(move |window, event| {
        if matches!(
            event,
            WindowEvent::Resized(_)
                | WindowEvent::ScaleFactorChanged { .. }
                | WindowEvent::MouseInput { .. }
                | WindowEvent::MouseWheel { .. }
                | WindowEvent::KeyboardInput { .. }
                | WindowEvent::Focused(true)
        ) {
            budget.borrow_mut().invalidate_immediately();
        }
        if matches!(event, WindowEvent::RedrawRequested) {
            if let Some(delay) = budget.borrow_mut().request(std::time::Instant::now()) {
                if !redraw_timer.running() {
                    let weak = weak.clone();
                    redraw_timer.start(TimerMode::SingleShot, delay, move || {
                        if let Some(ui) = weak.upgrade() {
                            ui.window()
                                .with_winit_window(|native| native.request_redraw());
                        }
                    });
                }
                return EventResult::PreventDefault;
            }
            redraw_timer.stop();
        }
        #[cfg(feature = "benchmark")]
        crate::perf_probe::observe_window_event(window, event);
        #[cfg(not(feature = "benchmark"))]
        let _ = window;
        if let WindowEvent::Focused(focused) = event {
            let weak = weak.clone();
            let focused = *focused;
            let _ = slint::invoke_from_event_loop(move || {
                if let Some(ui) = weak.upgrade() {
                    ui.set_karaoke_active(focused);
                }
            });
        }
        if matches!(event, WindowEvent::Focused(true)) {
            let weak = weak.clone();
            let _ = slint::invoke_from_event_loop(move || {
                if let Some(ui) = weak.upgrade() {
                    ACTIVE_STATE.with(|slot| {
                        if let Some(state) = slot.borrow().as_ref() {
                            refresh_focused(&ui, state, std::time::Instant::now());
                        }
                    });
                }
            });
        }
        EventResult::Propagate
    });
}

pub(crate) fn refresh_focused(ui: &AppWindow, state: &State, now: std::time::Instant) {
    if state
        .borrow()
        .last_activation
        .is_some_and(|last| now.duration_since(last) < Duration::from_secs(10))
    {
        return;
    }
    state.borrow_mut().last_activation = Some(now);
    poll_services(ui, state);
    refresh_catalog_quietly(ui, state);
    refresh_disk(ui, state);
    if !ui.get_busy() && !ui.get_operation_busy() {
        refresh_choice(ui, state);
        refresh_library_states(ui, state);
    }
}

pub(crate) fn request_exit(ui: &AppWindow, state: &State) {
    if ui.get_operation_busy() {
        return;
    }
    if ui.get_panel_kind() == "settings" {
        settings::save(ui, state, AfterSave::Quit);
    } else {
        save_window(ui, state);
        let _ = slint::quit_event_loop();
    }
}

pub(crate) fn apply_window_geometry(
    config: &mut chillhub_native::config::AppConfig,
    width: f64,
    height: f64,
    maximized: bool,
    viewer_fullscreen: bool,
) {
    if viewer_fullscreen {
        return;
    }
    if !maximized {
        config.window_width = width;
        config.window_height = height;
    }
    config.window_maximized = maximized;
}

pub(crate) fn save_window(ui: &AppWindow, state: &State) {
    use slint::winit_030::WinitWindowAccessor;
    let (store, viewer_fullscreen) = {
        let state = state.borrow();
        (
            state.services.config.clone(),
            state.viewer_restore.is_some(),
        )
    };
    if viewer_fullscreen {
        return;
    }
    ui.window().with_winit_window(|w| {
        let size = w.outer_size().to_logical::<f64>(w.scale_factor());
        let maximized = w.is_maximized();
        let _ = store.update(|c| {
            apply_window_geometry(c, size.width, size.height, maximized, viewer_fullscreen)
        });
    });
}
