use super::{ACTIVE_STATE, AppWindow, PANIC_CONTEXT, State, toast};
use slint::ComponentHandle;
use std::sync::atomic::{AtomicU64, Ordering};

#[derive(Clone, Copy, Debug)]
pub(crate) enum Activity {
    Quiet,
    Choice,
    Catalog,
    Operation,
    Poll,
    UpdateCheck,
}

static JOB_EPOCHS: [AtomicU64; Activity::UpdateCheck as usize + 1] =
    [const { AtomicU64::new(0) }; Activity::UpdateCheck as usize + 1];

pub(crate) fn background<T: Send + 'static>(
    ui: &AppWindow,
    work: impl FnOnce() -> T + Send + 'static,
    done: impl FnOnce(&AppWindow, &State, T) + Send + 'static,
) {
    background_for(ui, Activity::Quiet, work, done);
}

pub(crate) fn background_for<T: Send + 'static>(
    ui: &AppWindow,
    activity: Activity,
    work: impl FnOnce() -> T + Send + 'static,
    done: impl FnOnce(&AppWindow, &State, T) + Send + 'static,
) {
    let weak = ui.as_weak();
    let latest = &JOB_EPOCHS[activity as usize];
    let epoch = latest.fetch_add(1, Ordering::SeqCst) + 1;
    let diagnostic_api = PANIC_CONTEXT.get().map(|(api, _)| api.clone());
    std::thread::spawn(move || {
        let started = std::time::Instant::now();
        let report_timing = !matches!(activity, Activity::Quiet | Activity::Poll);
        if report_timing && let Some(api) = &diagnostic_api {
            api.event(
                "worker.start",
                serde_json::json!({"activity": format!("{activity:?}"), "id": epoch}),
            );
        }
        let result =
            std::panic::catch_unwind(std::panic::AssertUnwindSafe(work)).map_err(|error| {
                error
                    .downcast_ref::<String>()
                    .cloned()
                    .or_else(|| error.downcast_ref::<&str>().map(|s| s.to_string()))
                    .unwrap_or_else(|| "Неизвестная ошибка фоновой задачи".into())
            });
        if (report_timing || result.is_err())
            && let Some(api) = diagnostic_api
        {
            api.event(
                "worker.finish",
                serde_json::json!({"activity": format!("{activity:?}"), "id": epoch,
                    "elapsedMs": started.elapsed().as_millis(), "panicked": result.is_err()}),
            );
        }
        let _ = weak.upgrade_in_event_loop(move |ui| {
            ACTIVE_STATE.with(|slot| {
                let state = slot.borrow();
                let Some(state) = state.as_ref() else { return };
                match result {
                    Ok(value) => done(&ui, state, value),
                    Err(error) => failed(
                        &ui,
                        state,
                        activity,
                        latest.load(Ordering::SeqCst) == epoch,
                        &error,
                    ),
                }
            });
        });
    });
}

fn failed(ui: &AppWindow, state: &State, activity: Activity, current: bool, error: &str) {
    let data_dir = state.borrow().services.paths.data_dir.clone();
    chillhub_native::diagnostics::log(&data_dir, "background-error", error);
    if current {
        match activity {
            Activity::Choice => {
                ui.set_busy(false);
                ui.set_can_play(false);
            }
            Activity::Catalog => {
                ui.set_loading(false);
                ui.set_load_error(
                    "Не удалось загрузить библиотеку. Попробуйте обновить её.".into(),
                );
                if let Some(request) = super::take_catalog_shortcut(ui, state) {
                    super::offer_shortcut(ui, state, request);
                }
            }
            Activity::Operation => ui.set_operation_busy(false),
            Activity::Poll => state.borrow_mut().poll_inflight = false,
            Activity::UpdateCheck => state.borrow_mut().update_inflight = false,
            Activity::Quiet => {}
        }
    }
    toast(
        ui,
        state,
        "Действие не завершилось из-за внутренней ошибки. Подробности сохранены в журнале.",
    );
}
