use super::*;
use chillhub_native::{
    feedback::{FeedbackDraft, FeedbackService, SendResult},
    launch::{self, LaunchAction, LaunchService},
    queue::{QueueEvent, QueueState},
    service,
    transfer::TransferControl,
    update::{self, UpdateCheck, UpdatePaths},
};
use std::sync::atomic::Ordering;

#[derive(Clone)]
pub(super) enum Confirmation {
    Delete(GameRecord),
    DeleteOrphans(Vec<OrphanRow>),
    ChangeGamesFolder,
    Cleanup(String),
    Version(GameRecord, LaunchTarget, String),
    Security(Vec<PathBuf>),
    Recovery(update::StagedUpdate),
    Shortcut(ShortcutRequest),
}

#[derive(Clone, Copy, Debug)]
pub(super) enum Activity {
    Quiet = 0,
    Choice = 1,
    Catalog = 2,
    Operation = 3,
    Poll = 4,
    UpdateCheck = 5,
}
static JOB_EPOCHS: [std::sync::atomic::AtomicU64; 6] =
    [const { std::sync::atomic::AtomicU64::new(0) }; 6];
static ICON_EPOCH: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
static LIBRARY_STATE_EPOCH: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
static LIVE_SETTINGS_EPOCH: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(0);
pub(super) fn background<T: Send + 'static>(
    ui: &AppWindow,
    work: impl FnOnce() -> T + Send + 'static,
    done: impl FnOnce(&AppWindow, &State, T) + Send + 'static,
) {
    background_for(ui, Activity::Quiet, work, done);
}
#[cfg(test)]
pub(super) fn test_background_failure(ui: &AppWindow) {
    ui.set_operation_busy(true);
    background_for(
        ui,
        Activity::Operation,
        || panic!("controlled worker failure"),
        |_, _, _: ()| unreachable!(),
    );
}
#[cfg(test)]
pub(super) fn test_empty_catalog(ui: &AppWindow, state: &State) {
    let generation = state.borrow().catalog_generation;
    apply_catalog(
        ui,
        state,
        generation,
        Ok((
            chillhub_native::net::CatalogSnapshot {
                library: Default::default(),
                offline: false,
                fetched_at: String::new(),
                warning: None,
            },
            vec![],
        )),
    );
}
pub(super) fn background_for<T: Send + 'static>(
    ui: &AppWindow,
    activity: Activity,
    work: impl FnOnce() -> T + Send + 'static,
    done: impl FnOnce(&AppWindow, &State, T) + Send + 'static,
) {
    let weak = ui.as_weak();
    let epoch = JOB_EPOCHS[activity as usize].fetch_add(1, Ordering::SeqCst) + 1;
    let diagnostic_api = PANIC_CONTEXT.get().map(|(api, _)| api.clone());
    std::thread::spawn(move || {
        let started = std::time::Instant::now();
        if !matches!(activity, Activity::Quiet | Activity::Poll)
            && let Some(api) = &diagnostic_api
        {
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
        if (!matches!(activity, Activity::Quiet | Activity::Poll) || result.is_err())
            && let Some(api) = diagnostic_api
        {
            api.event(
                "worker.finish",
                serde_json::json!({"activity": format!("{activity:?}"), "id": epoch,
                    "elapsedMs": started.elapsed().as_millis(), "panicked": result.is_err()}),
            );
        }
        let _ = slint::invoke_from_event_loop(move || {
            if let Some(ui) = weak.upgrade() {
                ACTIVE_STATE.with(|slot|{if let Some(state)=slot.borrow().as_ref(){
            match result{
                Ok(value)=>done(&ui,state,value),
                Err(error)=>{
                    let services=state.borrow().services.clone();
                    chillhub_native::diagnostics::log(&services.paths.data_dir,"background-error",&error);
                    if JOB_EPOCHS[activity as usize].load(Ordering::SeqCst)==epoch{
                        match activity{
                            Activity::Choice=>{ui.set_busy(false);ui.set_can_play(false);},
                            Activity::Catalog=>{ui.set_loading(false);ui.set_load_error("Не удалось загрузить библиотеку. Попробуйте обновить её.".into());},
                            Activity::Operation=>ui.set_operation_busy(false),
                            Activity::Poll=>state.borrow_mut().poll_inflight=false,
                            Activity::UpdateCheck=>state.borrow_mut().update_inflight=false,
                            Activity::Quiet=>{}
                        }
                    }
                    toast(&ui,state,"Действие не завершилось из-за внутренней ошибки. Подробности сохранены в журнале.");
                }
            }
        }});
            }
        });
    });
}
pub(super) fn selected(state: &State) -> Option<(GameRecord, LaunchTarget)> {
    let s = state.borrow();
    let game = s.records.get(s.selected)?.clone();
    let visual = s.catalog.get(s.selected)?;
    let choice = s
        .choices
        .get(&game.game_id)
        .cloned()
        .unwrap_or_else(|| default_choice(visual));
    let (source, mode) = valid_choice(visual, &choice.0, &choice.1);
    Some((
        game,
        match (source.as_str(), mode.as_str()) {
            ("steam", "mods") => LaunchTarget::SteamModded,
            ("steam", _) => LaunchTarget::SteamVanilla,
            (_, "mods") => LaunchTarget::LocalModded,
            _ => LaunchTarget::LocalVanilla,
        },
    ))
}
pub(super) fn refresh_choice(ui: &AppWindow, state: &State) {
    let (game, target) = match selected(state) {
        Some(v) => v,
        None => {
            ui.set_can_play(false);
            return;
        }
    };
    let (visual, services, generation, epoch) = {
        let mut s = state.borrow_mut();
        s.choice_generation = s.choice_epoch.fetch_add(1, Ordering::SeqCst) + 1;
        s.prefs.remember(&game.game_id, target);
        s.prefs.last_game_id = game.game_id.clone();
        (
            s.catalog[s.selected].clone(),
            s.services.clone(),
            s.choice_generation,
            s.choice_epoch.clone(),
        )
    };
    ui.set_source(if target.via_steam() { "steam" } else { "local" }.into());
    ui.set_mode(if target.modded() { "mods" } else { "plain" }.into());
    ui.set_supports_steam(visual.steam_supported);
    ui.set_supports_local(visual.local_supported);
    ui.set_has_mods(visual.has_mods);
    ui.set_busy(true);
    ui.set_can_play(false);
    ui.set_install_hint("".into());
    ui.set_can_verify_game(false);
    ui.set_can_verify_mods(false);
    ui.set_has_game_files(false);
    ui.set_can_create_shortcut(false);
    ui.set_status("Проверяем установленную копию…".into());
    services.api.event(
        "launch.choice",
        serde_json::json!({"game": game.game_id, "target": target}),
    );
    background_for(
        ui,
        Activity::Choice,
        move || {
            let result = services.option(&game, target).map(|option| {
                let running = chillhub_native::playtime::processes_in(&option.game_dir)
                    .unwrap_or_default()
                    .into_iter()
                    .next();
                let version = launch::read_marker(&option.game_dir, ".version");
                let exe = launch::resolve_game_exe(
                    &option.game_dir,
                    &game.exe_relative_path,
                    &game
                        .mods
                        .as_ref()
                        .map(|m| m.exe_names.clone())
                        .unwrap_or_default(),
                )
                .is_ok();
                let files = [
                    !target.via_steam() && service::can_verify_game(&game, &option.game_dir),
                    service::can_verify_mods(&option.game_dir),
                    option.game_dir.is_dir(),
                    exe,
                ];
                let time = services
                    .playtime
                    .totals(&game.game_id)
                    .unwrap_or_default()
                    .total_seconds;
                let queued = services.queue.has_pending_for_root(&option.game_dir);
                (option, running, version, time, files, queued)
            });
            let saved = services.config.update(|c| {
                if epoch.load(Ordering::SeqCst) == generation {
                    c.remember(&game.game_id, target);
                    c.last_game_id = game.game_id.clone();
                }
            });
            (result, saved.err())
        },
        move |ui, state, (result, save_error)| {
            if state.borrow().choice_generation != generation {
                return;
            }
            ui.set_busy(false);
            match result {
                Ok((option, running, version, time, files, queued)) => {
                    let blocked = service::blocked(&state.borrow().maintenance, &option.action);
                    let launching = selected(state).is_some_and(|(game, target)| {
                        state
                            .borrow()
                            .launching
                            .contains(&(game.game_id, target.via_steam()))
                    });
                    ui.set_can_verify_game(files[0] && !queued && running.is_none() && !launching);
                    ui.set_can_verify_mods(files[1] && !queued && running.is_none() && !launching);
                    ui.set_has_game_files(files[2]);
                    ui.set_can_create_shortcut(files[3]);
                    ui.set_can_play(
                        option.available()
                            && running.is_none()
                            && !queued
                            && !blocked
                            && !launching,
                    );
                    ui.set_play_label(
                        if launching {
                            "Запускаем…"
                        } else if running.is_some() {
                            "Игра запущена"
                        } else if queued {
                            "В очереди"
                        } else if blocked {
                            "Технические работы"
                        } else {
                            match option.action {
                                LaunchAction::Play => "Играть",
                                LaunchAction::InstallGame => "Установить",
                                LaunchAction::UpdateGame => "Обновить",
                                LaunchAction::InstallMods => "Установить моды",
                                LaunchAction::UpdateMods => "Обновить моды",
                                LaunchAction::RepairMods => "Восстановить моды",
                                LaunchAction::Unavailable => "Недоступно",
                            }
                        }
                        .into(),
                    );
                    ui.set_status(if launching {
                        "Ожидаем запуска игры".into()
                    } else if running.is_some() {
                        "Игра уже запущена".into()
                    } else if queued {
                        "Откройте загрузки, чтобы продолжить или отменить задачу".into()
                    } else if blocked {
                        "Ожидаем завершения работ на сервере".into()
                    } else if option.note.is_empty() {
                        "Готово к запуску".into()
                    } else {
                        option.note.clone().into()
                    });
                    ui.set_installed_version(if target.via_steam() {
                        "Версией управляет Steam".into()
                    } else if version.is_empty() {
                        "Не установлена".into()
                    } else {
                        format!("Установлена {version}").into()
                    });
                    ui.set_playtime_label(if time == 0 {
                        "Ещё не играли".into()
                    } else if time < 60 {
                        "Сыграно меньше минуты".into()
                    } else {
                        format!("Сыграно {} ч {} мин", time / 3600, (time / 60) % 60).into()
                    });
                    let label = library_label(&option, running.is_some());
                    let id = state
                        .borrow()
                        .records
                        .get(state.borrow().selected)
                        .map(|g| g.game_id.clone());
                    if let Some(id) = id {
                        state.borrow_mut().library_status.insert(id, label);
                    }
                    state.borrow_mut().option = Some(option);
                    sync_library_rows(ui, state);
                    install_ui::hint(ui, state);
                }
                Err(error) => {
                    ui.set_can_play(false);
                    ui.set_status(error.into());
                    state.borrow_mut().option = None;
                }
            }
            if let Some(error) = save_error {
                toast(ui, state, &format!("Не удалось сохранить выбор: {error}"));
            }
        },
    );
}

pub(super) fn queue_report_notice(
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
pub(super) fn initialize(ui: &AppWindow, state: &State) {
    crate::karaoke::bind(ui);
    settings(ui, state);
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
        move || services.playtime.reconcile(),
        |ui, state, result| match result {
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
        },
    );
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
                                            ui.set_queue_error(error.clone().into());
                                            toast(
                                                &ui,
                                                state,
                                                &format!("Не удалось сохранить очередь: {error}"),
                                            );
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
    check_updates(ui, state, true);
    let weak = ui.as_weak();
    state
        .borrow()
        .update_timer
        .start(TimerMode::Repeated, Duration::from_secs(600), move || {
            if let Some(ui) = weak.upgrade() {
                ACTIVE_STATE.with(|slot| {
                    if let Some(state) = slot.borrow().as_ref() {
                        check_updates(&ui, state, false);
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
pub(super) fn install_window_refresh(ui: &AppWindow) {
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
                            let now = std::time::Instant::now();
                            if state.borrow().last_activation.is_some_and(|last| {
                                now.duration_since(last) < Duration::from_secs(10)
                            }) {
                                return;
                            }
                            state.borrow_mut().last_activation = Some(now);
                            poll_services(&ui, state);
                            refresh_catalog_quietly(&ui, state);
                            if !ui.get_busy() && !ui.get_operation_busy() {
                                refresh_choice(&ui, state);
                                refresh_library_states(&ui, state);
                            }
                        }
                    });
                }
            });
        }
        EventResult::Propagate
    });
}
fn refresh_catalog_quietly(ui: &AppWindow, state: &State) {
    if !ui.window().is_visible()
        || ui.window().is_minimized()
        || ui.get_loading()
        || ui.get_operation_busy()
        || !ui.get_panel_kind().is_empty()
    {
        return;
    }
    let mut s = state.borrow_mut();
    if s.catalog_poll_inflight || s.catalog_checked_at.elapsed() < Duration::from_secs(300) {
        return;
    }
    s.catalog_poll_inflight = true;
    s.catalog_checked_at = std::time::Instant::now();
    let services = s.services.clone();
    let seed = s.seed.clone();
    let generation = s.catalog_generation;
    let current = serde_json::to_vec(&s.records).ok();
    drop(s);
    background(
        ui,
        move || {
            services
                .api
                .catalog(&services.paths.data_dir.join("cache"))
                .map(|snapshot| {
                    let changed = serde_json::to_vec(&snapshot.library.items).ok() != current;
                    let games = if changed {
                        catalog::from_live(&snapshot.library.items, &seed)
                    } else {
                        Vec::new()
                    };
                    (snapshot, games, changed)
                })
        },
        move |ui, state, result| {
            state.borrow_mut().catalog_poll_inflight = false;
            if state.borrow().catalog_generation != generation {
                return;
            }
            match result {
                Ok((snapshot, games, true))
                    if ui.get_panel_kind().is_empty() && !ui.get_operation_busy() =>
                {
                    apply_catalog(ui, state, generation, Ok((snapshot, games)))
                }
                Ok((snapshot, _, _)) => {
                    state.borrow_mut().offline = snapshot.offline;
                    ui.set_connection_note(snapshot.warning.unwrap_or_default().into());
                    refresh_library_states(ui, state);
                }
                Err(e) => ui.set_connection_note(e.to_string().into()),
            }
        },
    );
}
fn staging_root(paths: &chillhub_native::paths::AppPaths) -> PathBuf {
    if std::env::var_os("CHILLHUB_DATA_DIR").is_some() {
        paths.data_dir.with_extension("updates")
    } else {
        PathBuf::from(std::env::var_os("LOCALAPPDATA").unwrap_or_default())
            .join("ChillHubUpdateStaging")
    }
}
fn check_updates(ui: &AppWindow, state: &State, startup: bool) {
    if state.borrow().update_inflight
        || ui.get_operation_busy()
        || std::env::var_os("CHILLHUB_DATA_DIR").is_some()
    {
        return;
    }
    state.borrow_mut().update_inflight = true;
    let services = state.borrow().services.clone();
    background_for(
        ui,
        Activity::UpdateCheck,
        move || {
            let install = std::env::current_exe()
                .ok()
                .and_then(|p| p.parent().map(Path::to_owned))
                .unwrap_or_default();
            let paths = UpdatePaths {
                install,
                config: services.paths.data_dir.clone(),
                games: services.paths.games_dir.clone(),
                staging: staging_root(&services.paths),
            };
            let pending = update::pending_recoveries(&paths);
            let previous = if startup {
                update::previous_outcome(&paths)
            } else {
                None
            };
            let release = update::check_release(
                &services.api.client,
                services.api.base.as_str(),
                env!("CARGO_PKG_VERSION"),
            );
            (pending, previous, release, paths)
        },
        move |ui, state, (pending, previous, release, paths)| {
            state.borrow_mut().update_inflight = false;
            if let Ok(pending) = pending
                && let Some(staged) = pending.into_iter().next()
            {
                confirm(ui,state,Confirmation::Recovery(staged),"Восстановить прерванное обновление?","Предыдущее обновление не завершилось. Помощник вернёт сохранённые файлы лаунчера и откроет его снова. Игры останутся на месте.".into(),"Восстановить и перезапустить");
                return;
            }
            if let Some(previous) = previous {
                toast(ui, state, &previous.message);
                background(
                    ui,
                    move || update::acknowledge_outcome(&paths, &previous),
                    |_, _, _| {},
                );
            }
            if let Ok(UpdateCheck::Available(release)) = release {
                if ui.get_panel_kind() != ""
                    || !state.borrow().services.idle()
                    || !state.borrow().launching.is_empty()
                {
                    ui.set_connection_note(
                        format!("Доступно обновление Chill Hub {}", release.version).into(),
                    );
                    state.borrow_mut().update = Some(release);
                    return;
                }
                state.borrow_mut().update = Some(release);
                open_panel(ui, state, "update");
                action(ui, state, "update-install");
            }
        },
    );
}
fn offer_shortcut(ui: &AppWindow, state: &State, request: ShortcutRequest) {
    let result = {
        let s = state.borrow();
        request.allowed_fallback(&s.services.paths)
    };
    match result {
        Ok(path) => confirm(
            ui,
            state,
            Confirmation::Shortcut(request),
            "Открыть установленную копию?",
            format!(
                "Каталог этой игры недоступен. Можно открыть сохранённую локальную копию без проверки обновлений:\n{}",
                path.display()
            ),
            "Запустить копию",
        ),
        Err(error) => toast(
            ui,
            state,
            &format!("Не удалось открыть игру из ярлыка: {error}"),
        ),
    }
}
fn settings(ui: &AppWindow, state: &State) {
    ui.set_settings_error("".into());
    let s = state.borrow();
    let c = &s.prefs;
    ui.set_settings_path(
        s.services
            .paths
            .games_dir
            .to_string_lossy()
            .into_owned()
            .into(),
    );
    ui.set_settings_threads(c.download_threads);
    ui.set_settings_speed(c.speed_limit_mbps);
    ui.set_settings_api(c.api_base_url.clone().into());
    ui.set_settings_tray(c.minimize_to_tray);
    ui.set_karaoke_enabled(
        c.extra
            .get("KaraokeEnabled")
            .and_then(|v| v.as_bool())
            .unwrap_or(false),
    );
    ui.set_reduced_motion(
        c.extra
            .get("ReducedMotion")
            .and_then(|v| v.as_bool())
            .unwrap_or(false),
    );
    drop(s);
    refresh_disk(ui, state);
}
fn save_live_settings(ui: &AppWindow, state: &State) {
    let threads = ui.get_settings_threads();
    let speed = ui.get_settings_speed();
    let tray = ui.get_settings_tray();
    let reduced = ui.get_reduced_motion();
    let karaoke = ui.get_karaoke_enabled();
    let s = state.borrow();
    if s.prefs.download_threads == threads
        && s.prefs
            .extra
            .get("KaraokeEnabled")
            .and_then(|v| v.as_bool())
            .unwrap_or(false)
            == karaoke
        && s.prefs.speed_limit_mbps == speed
        && s.prefs.minimize_to_tray == tray
        && s.prefs
            .extra
            .get("ReducedMotion")
            .and_then(|v| v.as_bool())
            .unwrap_or(false)
            == reduced
    {
        return;
    }
    let services = s.services.clone();
    drop(s);
    let epoch = LIVE_SETTINGS_EPOCH.fetch_add(1, Ordering::SeqCst) + 1;
    ui.set_settings_save_status("Сохраняем…".into());
    background(
        ui,
        move || {
            services.config.update(|c| {
                if LIVE_SETTINGS_EPOCH.load(Ordering::SeqCst) == epoch {
                    c.download_threads = threads;
                    c.speed_limit_mbps = speed;
                    c.minimize_to_tray = tray;
                    c.extra.insert("ReducedMotion".into(), reduced.into());
                    c.extra.insert("KaraokeEnabled".into(), karaoke.into());
                }
            })
        },
        move |ui, state, result| {
            if LIVE_SETTINGS_EPOCH.load(Ordering::SeqCst) != epoch {
                return;
            }
            match result {
                Ok(saved) => {
                    let mut s = state.borrow_mut();
                    s.prefs.download_threads = saved.download_threads;
                    s.prefs.speed_limit_mbps = saved.speed_limit_mbps;
                    s.prefs.minimize_to_tray = saved.minimize_to_tray;
                    s.prefs.extra.insert("ReducedMotion".into(), reduced.into());
                    s.prefs
                        .extra
                        .insert("KaraokeEnabled".into(), karaoke.into());
                    s.services
                        .queue
                        .set_speed_limit(saved.speed_limit_mbps.max(0) as u64 * 1024 * 1024);
                    ui.set_settings_save_status("Сохранено".into());
                }
                Err(e) => {
                    ui.set_settings_save_status("Не удалось сохранить".into());
                    ui.set_settings_error(e.into());
                }
            }
        },
    );
}
fn save_settings(ui: &AppWindow, state: &State, next: String) {
    if ui.get_operation_busy() {
        return;
    }
    LIVE_SETTINGS_EPOCH.fetch_add(1, Ordering::SeqCst);
    let services = state.borrow().services.clone();
    let path = settings_ui::normalize_games_path(ui.get_settings_path().as_str())
        .to_string_lossy()
        .into_owned();
    let api = ui
        .get_settings_api()
        .trim()
        .trim_end_matches('/')
        .to_string();
    let threads = ui.get_settings_threads();
    let speed = ui.get_settings_speed();
    let tray = ui.get_settings_tray();
    let reduced = ui.get_reduced_motion();
    let current = state.borrow().prefs.clone();
    let karaoke = ui.get_karaoke_enabled();
    let karaoke_changed = current
        .extra
        .get("KaraokeEnabled")
        .and_then(|v| v.as_bool())
        .unwrap_or(false)
        != karaoke;
    let paths_changed = !settings_ui::same_path(&path, &services.paths.games_dir.to_string_lossy());
    let api_changed = api != current.api_base_url.trim_end_matches('/');
    let reload_needed = paths_changed || api_changed;
    let reduced_changed = current
        .extra
        .get("ReducedMotion")
        .and_then(|v| v.as_bool())
        .unwrap_or(false)
        != reduced;
    if !reload_needed
        && current.download_threads == threads
        && current.speed_limit_mbps == speed
        && current.minimize_to_tray == tray
        && !reduced_changed
        && !karaoke_changed
    {
        finish_settings(ui, state, &next);
        return;
    }
    ui.set_settings_error("".into());
    if reload_needed && !state.borrow().launching.is_empty() {
        ui.set_settings_error("Дождитесь запуска игр перед изменением папки или сервера.".into());
        return;
    }
    ui.set_operation_busy(true);
    background_for(
        ui,
        Activity::Operation,
        move || -> Result<AppConfig, String> {
            if reload_needed && !services.idle() {
                return Err("Папку игр и адрес сервера можно изменить после завершения загрузок и игр. Скорость, потоки и поведение окна можно менять сейчас.".into());
            }
            if paths_changed {
                let folder = PathBuf::from(&path);
                let executable = std::env::current_exe().map_err(|e| e.to_string())?;
                chillhub_native::library::validate_games_location(
                    &folder,
                    &services.paths,
                    executable.parent().ok_or("Папка лаунчера не найдена")?,
                )?;
            }
            if api_changed {
                chillhub_native::net::ApiClient::new(&api).map_err(|e| e.to_string())?;
            }
            let saved = services.config.update(|c| {
                if paths_changed {
                    c.games_path = path;
                }
                if api_changed {
                    c.api_base_url = api;
                }
                c.download_threads = threads;
                c.speed_limit_mbps = speed;
                c.minimize_to_tray = tray;
                c.extra
                    .insert("ReducedMotion".into(), serde_json::Value::Bool(reduced));
                c.extra.insert("KaraokeEnabled".into(), karaoke.into());
            })?;
            services
                .queue
                .set_speed_limit(saved.speed_limit_mbps.max(0) as u64 * 1024 * 1024);
            if paths_changed {
                chillhub_native::installed_apps::refresh(
                    Path::new(&saved.games_path),
                    &services.paths.data_dir,
                );
            }
            Ok(saved)
        },
        move |ui, state, result| {
            ui.set_operation_busy(false);
            match result {
                Ok(config) => {
                    let mut s = state.borrow_mut();
                    s.services.paths = config.apply_paths(&s.services.paths);
                    if api_changed {
                        match chillhub_native::net::ApiClient::new(&config.api_base_url) {
                            Ok(api) => {
                                s.services.api =
                                    api.with_diagnostics(s.services.paths.data_dir.clone())
                            }
                            Err(e) => {
                                drop(s);
                                ui.set_settings_error(e.to_string().into());
                                return;
                            }
                        }
                        s.services.feedback = std::sync::Arc::new(FeedbackService::new(
                            s.services.api.clone(),
                            s.services.paths.data_dir.clone(),
                        ));
                    }
                    s.prefs = config;
                    drop(s);
                    settings(ui, state);
                    finish_settings(ui, state, &next);
                    toast(
                        ui,
                        state,
                        if paths_changed {
                            "Настройки сохранены. Игры остались в прежней папке."
                        } else {
                            "Настройки сохранены"
                        },
                    );
                    if reload_needed && next != "__quit" {
                        reload(ui, state);
                    }
                }
                Err(e) => {
                    ui.set_settings_error(e.clone().into());
                    toast(ui, state, &e);
                }
            }
        },
    );
}
fn finish_settings(ui: &AppWindow, state: &State, next: &str) {
    close_panel(ui, state);
    if next == "__quit" {
        save_window(ui, state);
        let _ = slint::quit_event_loop();
    } else if !next.is_empty() {
        open_panel(ui, state, next);
    }
}
pub(super) fn request_exit(ui: &AppWindow, state: &State) {
    if ui.get_operation_busy() {
        return;
    }
    if ui.get_panel_kind() == "settings" {
        save_settings(ui, state, "__quit".into());
    } else {
        save_window(ui, state);
        let _ = slint::quit_event_loop();
    }
}

fn refresh_disk(ui: &AppWindow, state: &State) {
    let path = state.borrow().services.paths.games_dir.clone();
    background(
        ui,
        move || {
            let mut p = path.as_path();
            while !p.exists() {
                let parent = p.parent()?;
                p = parent;
            }
            fs2::available_space(p).ok()
        },
        |ui, _, space| {
            ui.set_disk_label(
                space
                    .map(|n| format!("Свободно {:.1} ГБ", n as f64 / 1024_f64.powi(3)))
                    .unwrap_or_else(|| "Не удалось определить свободное место".into())
                    .into(),
            )
        },
    );
}
fn reload(ui: &AppWindow, state: &State) {
    state.borrow_mut().catalog_checked_at = std::time::Instant::now();
    ui.set_loading(true);
    ui.set_load_error("".into());
    ui.set_can_play(false);
    let (services, seed, generation) = {
        let mut s = state.borrow_mut();
        s.catalog_generation += 1;
        (s.services.clone(), s.seed.clone(), s.catalog_generation)
    };
    let cached_ui = ui.as_weak();
    background_for(
        ui,
        Activity::Catalog,
        move || {
            let cache_dir = services.paths.data_dir.join("cache");
            if let Some(snapshot) = services.api.cached_catalog(&cache_dir) {
                let games = catalog::from_live(&snapshot.library.items, &seed);
                let _ = slint::invoke_from_event_loop(move || {
                    if let Some(ui) = cached_ui.upgrade() {
                        ACTIVE_STATE.with(|slot| {
                            if let Some(state) = slot.borrow().as_ref() {
                                apply_catalog(&ui, state, generation, Ok((snapshot, games)));
                            }
                        });
                    }
                });
            }
            let result = services.api.catalog(&services.paths.data_dir.join("cache"));
            result.map(|snapshot| {
                let games = catalog::from_live(&snapshot.library.items, &seed);
                (snapshot, games)
            })
        },
        move |ui, state, result| apply_catalog(ui, state, generation, result),
    );
}
fn apply_catalog(
    ui: &AppWindow,
    state: &State,
    generation: u64,
    result: Result<
        (chillhub_native::net::CatalogSnapshot, Vec<Game>),
        chillhub_native::net::ApiError,
    >,
) {
    if state.borrow().catalog_generation != generation {
        return;
    }
    ui.set_loading(false);
    match result {
        Err(error) => {
            ui.set_load_error(error.to_string().into());
            ui.set_can_play(false);
            let request = state.borrow_mut().shortcut.take();
            if let Some(request) = request {
                offer_shortcut(ui, state, request);
            }
        }
        Ok((snapshot, games)) => {
            let (index, shortcut) = {
                let mut s = state.borrow_mut();
                s.offline = snapshot.offline;
                s.records = snapshot.library.items;
                s.catalog = games;
                s.icons = vec![Image::default(); s.catalog.len()];
                s.visible.clear();
                s.generation += 1;
                s.selected = usize::MAX;
                let remembered = s.prefs.launch_targets.clone();
                for (id, target) in remembered {
                    if let Some(t) = LaunchTarget::parse(&target) {
                        s.choices.entry(id).or_insert_with(|| {
                            (
                                if t.via_steam() { "steam" } else { "local" }.into(),
                                if t.modded() { "mods" } else { "plain" }.into(),
                            )
                        });
                    }
                }
                let shortcut = s.shortcut.take();
                let preferred = shortcut
                    .as_ref()
                    .map(|s| s.game_id.as_str())
                    .unwrap_or(&s.prefs.last_game_id);
                let index = s
                    .catalog
                    .iter()
                    .position(|g| g.id == preferred)
                    .unwrap_or(0);
                if let Some(ref request) = shortcut
                    && let Some(t) = request.target
                {
                    s.choices.insert(
                        request.game_id.clone(),
                        (
                            if t.via_steam() { "steam" } else { "local" }.into(),
                            if t.modded() { "mods" } else { "plain" }.into(),
                        ),
                    );
                }
                (index, shortcut)
            };
            ui.set_connection_note(snapshot.warning.unwrap_or_default().into());
            refresh_library(ui, state);
            load_icons(ui, state, generation);
            refresh_library_states(ui, state);
            if state.borrow().catalog.is_empty() {
                clear_game(ui, state);
                ui.set_load_error("В библиотеке пока нет опубликованных игр".into());
                ui.set_can_play(false);
            } else {
                select_game(ui, state, index);
            }
            if let Some(request) = shortcut {
                if state
                    .borrow()
                    .records
                    .iter()
                    .any(|g| g.game_id == request.game_id)
                {
                    // Capture the requested game now. A delayed callback could
                    // otherwise launch a different game selected in the meantime.
                    play(ui, state);
                } else {
                    offer_shortcut(ui, state, request);
                }
            } else if ui.get_panel_kind().is_empty()
                && chillhub_native::changelog::should_show(
                    &state.borrow().prefs.last_seen_changelog_version,
                    env!("CARGO_PKG_VERSION"),
                )
            {
                open_panel(ui, state, "changelog");
            }
            security_offers::catalog_ready(ui, state);
            orphan_ui::catalog_ready(ui, state);
        }
    }
}
fn library_label(option: &LaunchOption, running: bool) -> (String, String) {
    if running {
        return ("Игра запущена".into(), "active".into());
    }
    let (text, kind) = match option.action {
        LaunchAction::Play => ("Установлена", "ready"),
        LaunchAction::UpdateGame | LaunchAction::UpdateMods => ("Доступно обновление", "update"),
        LaunchAction::RepairMods => ("Нужно восстановление", "update"),
        LaunchAction::InstallGame => ("Не установлена", "idle"),
        LaunchAction::InstallMods => ("Моды не установлены", "idle"),
        LaunchAction::Unavailable => ("Копия недоступна", "idle"),
    };
    (text.into(), kind.into())
}
fn refresh_library_states(ui: &AppWindow, state: &State) {
    let epoch = LIBRARY_STATE_EPOCH.fetch_add(1, Ordering::SeqCst) + 1;
    let s = state.borrow();
    let services = s.services.clone();
    let records = s.records.clone();
    let prefs = s.prefs.clone();
    let generation = s.catalog_generation;
    drop(s);
    background(
        ui,
        move || {
            records
                .into_iter()
                .map(|g| {
                    if let Err(error) = services.verify_game_status(&g) {
                        chillhub_native::diagnostics::log(
                            &services.paths.data_dir,
                            "library-status",
                            &error,
                        );
                    }
                    let target = prefs.remembered(&g.game_id).unwrap_or(
                        if g.mods.as_ref().is_some_and(|m| !m.steam_app_id.is_empty()) {
                            LaunchTarget::SteamModded
                        } else if g.mods.is_some() {
                            LaunchTarget::LocalModded
                        } else {
                            LaunchTarget::LocalVanilla
                        },
                    );
                    let label = services
                        .option(&g, target)
                        .map(|option| {
                            let running = chillhub_native::playtime::processes_in(&option.game_dir)
                                .is_ok_and(|p| !p.is_empty());
                            library_label(&option, running)
                        })
                        .unwrap_or(("Не удалось проверить".into(), "error".into()));
                    (g.game_id, label)
                })
                .collect::<HashMap<_, _>>()
        },
        move |ui, state, labels| {
            if LIBRARY_STATE_EPOCH.load(Ordering::SeqCst) != epoch
                || state.borrow().catalog_generation != generation
            {
                return;
            }
            state.borrow_mut().library_status = labels;
            sync_library_rows(ui, state);
            if !ui.get_busy() && !ui.get_operation_busy() {
                refresh_choice(ui, state);
            }
        },
    );
}
pub(super) fn sync_library_rows(ui: &AppWindow, state: &State) {
    let model = ui.get_games();
    let s = state.borrow();
    for index in 0..model.row_count() {
        let Some(mut row) = model.row_data(index) else {
            continue;
        };
        let mut label = s
            .library_status
            .get(row.id.as_str())
            .cloned()
            .unwrap_or_default();
        let queue = s
            .tasks
            .iter()
            .rev()
            .find(|t| {
                t.request.game_id == row.id.as_str()
                    && !matches!(
                        t.state,
                        QueueState::Completed | QueueState::Cancelled | QueueState::Failed
                    )
            })
            .or_else(|| {
                s.tasks
                    .iter()
                    .rev()
                    .find(|t| t.request.game_id == row.id.as_str())
                    .filter(|t| t.state == QueueState::Failed)
            });
        if let Some(task) = queue {
            label = (
                queue_ui::state_label(task).into(),
                if task.state == QueueState::Failed {
                    "error"
                } else {
                    "active"
                }
                .into(),
            );
            if task.state == QueueState::Running
                && task.progress.as_ref().is_some_and(|p| p.total_bytes > 0)
            {
                label.0 = format!("{} · {:.0}%", label.0, queue_ui::progress(task) * 100.);
            }
        }
        if row.status.as_str() != label.0 || row.status_kind.as_str() != label.1 {
            row.status = label.0.into();
            row.status_kind = label.1.into();
            model.set_row_data(index, row);
        }
    }
}

fn load_icons(ui: &AppWindow, state: &State, generation: u64) {
    let icon_epoch = ICON_EPOCH.fetch_add(1, Ordering::SeqCst) + 1;
    let s = state.borrow();
    let services = s.services.clone();
    let bundle = s.data_root.clone();
    let games = s.catalog.clone();
    drop(s);
    let weak = ui.as_weak();
    std::thread::spawn(move || {
        let cache = services.paths.data_dir.join("cache/media");
        for remote in [false, true] {
            for (index, game) in games.iter().enumerate() {
                if ICON_EPOCH.load(Ordering::SeqCst) != icon_epoch {
                    return;
                }
                let value = if remote || game.fallback_icon.is_empty() {
                    &game.icon
                } else {
                    &game.fallback_icon
                };
                let bundled = value.starts_with("Assets/") || value.starts_with("art/");
                if remote == bundled {
                    continue;
                }
                let result =
                    source_image_pixels(value, &bundle, &cache, &services.api, 64, 64, None);
                let pixels = match result {
                    Ok(pixels) => pixels,
                    Err(error) => {
                        services.api.event(
                            "image.icon_error",
                            serde_json::json!({
                                "game": game.id, "error": error, "remote": remote,
                                "bundledFallback": !game.fallback_icon.is_empty()
                            }),
                        );
                        continue;
                    }
                };
                let weak = weak.clone();
                let _ = slint::invoke_from_event_loop(move || {
                    if let Some(ui) = weak.upgrade() {
                        ACTIVE_STATE.with(|slot| {
                            if let Some(state) = slot.borrow().as_ref() {
                                let mut s = state.borrow_mut();
                                if s.catalog_generation != generation
                                    || ICON_EPOCH.load(Ordering::SeqCst) != icon_epoch
                                {
                                    return;
                                }
                                let image = from_pixels(pixels);
                                if let Some(icon) = s.icons.get_mut(index) {
                                    *icon = image.clone();
                                }
                                let visible_index = s.visible.iter().position(|&i| i == index);
                                drop(s);
                                if let Some(visible_index) = visible_index {
                                    let model = ui.get_games();
                                    if let Some(mut row) = model.row_data(visible_index) {
                                        row.icon = image;
                                        model.set_row_data(visible_index, row);
                                    }
                                }
                            }
                        });
                    }
                });
            }
        }
        let _ = chillhub_native::media::trim_cache(&cache);
    });
}
fn queue_finished(ui: &AppWindow, state: &State, task: QueueItem) {
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
        chillhub_native::diagnostics::metric(
            &s.services.api,
            &s.services.paths.data_dir,
            chillhub_native::diagnostics::Metric {
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
            },
        );
    }
    if task.state == QueueState::Failed
        && let Some(error_code) = &task.report.error_code
    {
        chillhub_native::diagnostics::metric(
            &s.services.api,
            &s.services.paths.data_dir,
            chillhub_native::diagnostics::Metric {
                event: "error".into(),
                game_id: task.request.game_id.clone(),
                version: task.request.version.clone(),
                result: "fail".into(),
                error_code: error_code.clone(),
                ..Default::default()
            },
        );
    }
    drop(s);
    if task.state == QueueState::Completed
        && task.request.scope == chillhub_native::transfer::SyncScope::OwnFilesOnly
    {
        security_offers::installed_mods(ui, state, task.request.root.clone());
    }
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
    let mut waiting_position = 0;
    let rows = tasks
        .iter()
        .map(|task| {
            let title = state
                .borrow()
                .records
                .iter()
                .find(|g| g.game_id == task.request.game_id)
                .map(|g| g.title.clone())
                .unwrap_or_else(|| task.request.game_id.clone());
            let progress = queue_ui::progress(task);
            let detail = queue_ui::detail(
                task,
                state.borrow().queue_speeds.rate(task),
                state.borrow().queue_speeds.eta(task),
            );
            let state_name = if task.state == QueueState::Waiting {
                waiting_position += 1;
                if waiting_position == 1 {
                    "Следующая в очереди".to_string()
                } else {
                    format!("В очереди · {waiting_position}-я")
                }
            } else {
                queue_ui::state_label(task).to_string()
            };
            QueueRow {
                id: task.id.clone().into(),
                title: format!(
                    "{title} · {}",
                    if task.request.scope == chillhub_native::transfer::SyncScope::OwnFilesOnly {
                        "моды"
                    } else {
                        "игра"
                    }
                )
                .into(),
                detail: detail.into(),
                progress: if task.state == QueueState::Completed {
                    1.
                } else {
                    progress
                },
                state: state_name.into(),
                can_pause: matches!(task.state, QueueState::Running | QueueState::Waiting),
                can_resume: matches!(
                    task.state,
                    QueueState::Paused | QueueState::Failed | QueueState::Cancelled
                ),
                can_cancel: matches!(
                    task.state,
                    QueueState::Waiting
                        | QueueState::Running
                        | QueueState::Paused
                        | QueueState::NeedsConfirmation
                ),
                can_confirm: task.state == QueueState::NeedsConfirmation,
                can_prioritize: queue_ui::can_prioritize(
                    task.state,
                    tasks.first().is_some_and(|first| first.id == task.id),
                ),
                can_remove: queue_ui::can_remove(task.state),
                security_blocked: task.state == QueueState::Failed
                    && task.report.error_code.as_deref() == Some("sync_security_blocked"),
            }
        })
        .collect::<Vec<_>>();
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
    let active = tasks
        .iter()
        .find(|t| t.state == QueueState::Running)
        .or_else(|| {
            tasks.iter().find(|t| {
                matches!(
                    t.state,
                    QueueState::Paused | QueueState::Waiting | QueueState::NeedsConfirmation
                )
            })
        });
    let summary = active
        .map(|t| {
            let label = queue_ui::state_label(t);
            if t.progress.as_ref().is_some_and(|p| p.total_bytes > 0)
                && t.state == QueueState::Running
            {
                format!("{label} · {:.0}%", queue_ui::progress(t) * 100.)
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
    ui.set_downloads_progress(active.map_or(0., queue_ui::progress));
    desktop::set_status(&if summary.is_empty() {
        "Chill Hub".into()
    } else {
        format!("Chill Hub · {summary}")
    });
    ui.set_queue(Rc::new(VecModel::from(rows)).into());
    state.borrow_mut().tasks = tasks;
    sync_library_rows(ui, state);
    if changed {
        refresh_library_states(ui, state);
        refresh_choice(ui, state);
        refresh_disk(ui, state);
    }
}
pub(super) fn play(ui: &AppWindow, state: &State) {
    if ui.get_operation_busy() {
        return;
    }
    let Some((game, target)) = selected(state) else {
        return;
    };
    let services = state.borrow().services.clone();
    let offline = state.borrow().offline;
    let shown_action = state.borrow().option.as_ref().map(|o| o.action.clone());
    let launching = shown_action == Some(LaunchAction::Play);
    let launch_key = (game.game_id.clone(), target.via_steam());
    if state.borrow().launching.contains(&launch_key) {
        return;
    }
    if launching {
        state.borrow_mut().launching.insert(launch_key.clone());
        ui.set_play_label("Запускаем…".into());
        ui.set_status("Ожидаем запуска игры".into());
    } else {
        ui.set_busy(true);
        ui.set_operation_busy(true);
    }
    ui.set_can_play(false);
    background_for(
        ui,
        if launching { Activity::Quiet } else { Activity::Operation },
        move || -> Result<Option<chillhub_native::playtime::SessionHandle>, install_ui::PlayFailure> {
            std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            let option = services.option(&game, target)?;
            if option.action==LaunchAction::Play && shown_action.is_some_and(|shown|shown!=LaunchAction::Play) {
                return Err("Состояние игры изменилось. Проверьте кнопку и нажмите её снова, чтобы запустить игру.".to_string().into());
            }
            if !(offline && option.action == LaunchAction::Play) {
                services.maintenance_check(&option.action)?;
            }
            if !option.available() {
                return Err(option.note.into());
            }
            if option.action == LaunchAction::Play {
                let launched = LaunchService::new(services.paths.clone()).launch(&game, target)?;
                chillhub_native::diagnostics::metric(
                    &services.api,
                    &services.paths.data_dir,
                    chillhub_native::diagnostics::Metric {
                        event: "game_launch".into(),
                        game_id: game.game_id,
                        version: game.latest_version,
                        result: "ok".into(),
                        ..Default::default()
                    },
                );
                Ok(Some(launched))
            } else {
                if !target.via_steam()
                    && let Err(e)=install_ui::preflight(&option.game_dir) {
                        if install_ui::can_choose_folder_after(&e) {return Err(install_ui::PlayFailure::Folder(option.game_dir,e.to_string()));}
                        return Err(format!("Папка установки недоступна: {e}").into());
                    }
                services
                    .enqueue_game(&game, target, None, false, false)
                    .map(|_| None)
                    .map_err(Into::into)
            }
            })).unwrap_or_else(|_| Err(install_ui::PlayFailure::General("Не удалось завершить запуск. Подробности сохранены в журнале.".into())))
        },
        move |ui, state, result| {
            if launching {
                state.borrow_mut().launching.remove(&launch_key);
            } else {
                ui.set_busy(false);
                ui.set_operation_busy(false);
            }
            match result {
                Ok(Some(session)) => {
                    if let Some(warning) = &session.warning {
                        toast(ui, state, warning);
                    } else {
                        toast(ui, state, "Игра запущена");
                    }
                    watch_game(ui, session);
                }
                Ok(None) => open_panel(ui, state, "downloads"),
                Err(install_ui::PlayFailure::General(error)) => report_failure(ui, state, "launch", &error),
                Err(install_ui::PlayFailure::Folder(path,error)) => {
                    chillhub_native::diagnostics::log(&state.borrow().services.paths.data_dir,"install-path",&error);
                    confirm(ui,state,Confirmation::ChangeGamesFolder,"В эту папку нельзя записать файлы",format!("Папка:\n{}\n\nВыберите другую папку для новых установок. Уже установленные игры не будут перемещены. После сохранения настроек нажмите «Установить» снова.",path.display()),"Выбрать другую папку");
                }
            }
            refresh_choice(ui, state);
        },
    );
}
fn watch_game(ui: &AppWindow, session: chillhub_native::playtime::SessionHandle) {
    let services =
        ACTIVE_STATE.with(|slot| slot.borrow().as_ref().map(|s| s.borrow().services.clone()));
    background(
        ui,
        move || {
            let start = chrono::Utc::now();
            while session.process.is_running() {
                std::thread::sleep(Duration::from_secs(1));
            }
            if let Some(services) = services {
                chillhub_native::diagnostics::metric(
                    &services.api,
                    &services.paths.data_dir,
                    chillhub_native::diagnostics::Metric {
                        event: "game_session".into(),
                        game_id: session.game_id,
                        result: "ok".into(),
                        duration_ms: (chrono::Utc::now() - start).num_milliseconds().max(0) as u64,
                        ..Default::default()
                    },
                );
            }
        },
        |ui, state, ()| refresh_choice(ui, state),
    );
}
pub(super) fn prepare_panel(ui: &AppWindow, state: &State, kind: &str) {
    if kind == "update" {
        let release = state.borrow().update.clone();
        ui.set_update_available(release.is_some());
        ui.set_update_target(release.map(|r| r.version).unwrap_or_default().into());
        ui.set_update_error(false);
        ui.set_task_status("".into());
        ui.set_task_progress(0.);
        action(ui, state, "update-check");
    }
    if kind == "mods" {
        ui.set_has_modpack_link(
            selected(state)
                .and_then(|(g, _)| g.mods.as_ref().and_then(settings_ui::modpack_url))
                .is_some(),
        );
    }
    if kind == "settings" {
        settings(ui, state);
    }
    if kind == "changelog"
        && chillhub_native::changelog::should_show(
            &state.borrow().prefs.last_seen_changelog_version,
            env!("CARGO_PKG_VERSION"),
        )
    {
        let store = state.borrow().services.config.clone();
        state.borrow_mut().prefs.last_seen_changelog_version = env!("CARGO_PKG_VERSION").into();
        background(
            ui,
            move || {
                store.update(|c| {
                    if chillhub_native::changelog::should_show(
                        &c.last_seen_changelog_version,
                        env!("CARGO_PKG_VERSION"),
                    ) {
                        c.last_seen_changelog_version = env!("CARGO_PKG_VERSION").into();
                    }
                })
            },
            |ui, state, result| {
                if let Err(error) = result {
                    toast(
                        ui,
                        state,
                        &format!("Не удалось запомнить просмотр обновлений: {error}"),
                    );
                }
            },
        );
    }
    if kind == "orphans" {
        orphan_ui::prepare(ui, state);
    }
    if kind == "manage" {
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
}
pub(super) fn confirm(
    ui: &AppWindow,
    state: &State,
    action: Confirmation,
    title: &str,
    body: String,
    label: &str,
) {
    close_panel(ui, state);
    state.borrow_mut().pending = Some(action);
    ui.set_panel_title(title.into());
    ui.set_panel_subtitle("Проверьте выбранное действие".into());
    ui.set_panel_body(body.into());
    ui.set_confirm_text(label.into());
    ui.set_panel_kind("confirm".into());
}
fn operation(
    ui: &AppWindow,
    _state: &State,
    work: impl FnOnce() -> Result<String, String> + Send + 'static,
) {
    ui.set_operation_busy(true);
    background_for(ui, Activity::Operation, work, |ui, state, result| {
        ui.set_operation_busy(false);
        match result {
            Ok(message) => toast(ui, state, &message),
            Err(error) => report_failure(ui, state, "operation", &error),
        }
        refresh_choice(ui, state);
    });
}
fn report_failure(ui: &AppWindow, state: &State, kind: &str, error: &str) {
    let services = state.borrow().services.clone();
    chillhub_native::diagnostics::log(&services.paths.data_dir, kind, error);
    chillhub_native::diagnostics::report_error(
        &services.api,
        &services.paths.data_dir,
        kind,
        error,
    );
    toast(ui, state, error);
}
fn action(ui: &AppWindow, state: &State, value: &str) {
    if matches!(value, "update-install" | "update-verify") && !state.borrow().launching.is_empty() {
        toast(
            ui,
            state,
            "Дождитесь запуска игр перед обновлением лаунчера.",
        );
        return;
    }
    let services = state.borrow().services.clone();
    match value {
        "choose-games-folder" => {
            if ui.get_operation_busy() || ui.get_panel_kind() != "settings" {
                return;
            }
            let owner = state.borrow().native_hwnd;
            let initial = PathBuf::from(ui.get_settings_path().as_str());
            ui.set_operation_busy(true);
            background_for(
                ui,
                Activity::Operation,
                move || settings_ui::choose_folder(owner, &initial),
                |ui, state, result| {
                    ui.set_operation_busy(false);
                    if ui.get_panel_kind() != "settings" {
                        return;
                    }
                    match result {
                        Ok(Some(path)) => {
                            ui.set_settings_path(path.to_string_lossy().into_owned().into())
                        }
                        Ok(None) => {}
                        Err(e) => {
                            ui.set_settings_error(e.clone().into());
                            toast(ui, state, &e);
                        }
                    }
                },
            );
        }
        "copy-game-path" => {
            let path = ui.get_manage_path();
            if !path.is_empty() {
                match settings_ui::copy_text(state.borrow().native_hwnd, &path) {
                    Ok(()) => toast(ui, state, "Путь скопирован"),
                    Err(e) => toast(ui, state, &e),
                }
            }
        }
        "open-modpack-source" => {
            if let Some(url) = selected(state)
                .and_then(|(g, _)| g.mods.as_ref().and_then(settings_ui::modpack_url))
                && let Err(e) = settings_ui::open_package(&url)
            {
                toast(ui, state, &e);
            }
        }
        "retry-catalog" => reload(ui, state),
        "delete-selected-orphans" => orphan_ui::confirm_selected(ui, state),
        "open-logs" => {
            if let Some(directory) =
                chillhub_native::diagnostics::existing_log_directory(&services.paths.data_dir)
            {
                if let Err(e) = service::open_directory(&directory) {
                    toast(ui, state, &e);
                }
            } else {
                let e = "Журнал недоступен или отключён.";
                toast(ui, state, e);
            }
        }
        "feedback" | "changelog" | "update" | "security" => open_panel(ui, state, value),
        "cancel-action" => {
            state.borrow_mut().pending = None;
            close_panel(ui, state);
        }
        "confirm-action" => {
            let pending = state.borrow_mut().pending.take();
            close_panel(ui, state);
            match pending {
                Some(Confirmation::DeleteOrphans(rows)) => {
                    orphan_ui::delete_selected(ui, state, rows)
                }
                Some(Confirmation::ChangeGamesFolder) => {
                    open_panel(ui, state, "settings");
                    action(ui, state, "choose-games-folder");
                }
                Some(Confirmation::Delete(game)) => operation(ui, state, move || {
                    services.delete_local(&game)?;
                    chillhub_native::installed_apps::refresh(
                        &services.paths.games_dir,
                        &services.paths.data_dir,
                    );
                    Ok("Локальная копия удалена".into())
                }),
                Some(Confirmation::Cleanup(id)) => operation(ui, state, move || {
                    services
                        .queue
                        .confirm_cleanup(&id)
                        .map(|_| "Задача продолжена".into())
                        .map_err(|e| e.to_string())
                }),
                Some(Confirmation::Version(game, target, version)) => {
                    operation(ui, state, move || {
                        services
                            .enqueue_game(&game, target, Some(&version), false, false)
                            .map(|_| "Выбранная версия добавлена в очередь".into())
                    })
                }
                Some(Confirmation::Recovery(staged)) => {
                    ui.set_operation_busy(true);
                    background_for(
                        ui,
                        Activity::Operation,
                        move || update::launch_recovery(&staged, &[]).map(|_| ()),
                        |ui, state, r| {
                            ui.set_operation_busy(false);
                            match r {
                                Ok(()) => {
                                    save_window(ui, state);
                                    let _ = slint::quit_event_loop();
                                }
                                Err(e) => toast(ui, state, &e),
                            }
                        },
                    );
                }
                Some(Confirmation::Shortcut(request)) => {
                    ui.set_operation_busy(true);
                    background_for(
                        ui,
                        Activity::Operation,
                        move || LaunchService::new(services.paths).launch_shortcut(&request),
                        |ui, state, r| {
                            ui.set_operation_busy(false);
                            match r {
                                Ok(session) => {
                                    watch_game(ui, session);
                                    toast(ui, state, "Установленная копия запущена");
                                }
                                Err(e) => toast(ui, state, &e),
                            }
                        },
                    );
                }
                Some(Confirmation::Security(paths)) => operation(ui, state, move || {
                    chillhub_native::security::launch_helper(
                        &paths,
                        chillhub_native::security::SecurityOperation::Add,
                    )
                    .map(|_| "Открыт помощник защиты".into())
                }),
                None => {}
            }
        }
        "verify-game" | "verify-mods" => {
            if let Some((game, target)) = selected(state) {
                let mods = value == "verify-mods";
                if target.via_steam() && !mods {
                    toast(
                        ui,
                        state,
                        "Проверьте файлы игры в Steam: Свойства → Установленные файлы → Проверить целостность. Модпак проверяется отдельно.",
                    );
                    return;
                }
                operation(ui, state, move || {
                    services
                        .enqueue_game(&game, target, None, true, mods)
                        .map(|_| "Проверка добавлена в очередь".into())
                });
            }
        }
        "open-game-folder" => {
            if let Some((game, target)) = selected(state) {
                operation(ui, state, move || {
                    services
                        .option(&game, target)
                        .and_then(|o| service::open_directory(&o.game_dir))
                        .map(|_| "Папка открыта".into())
                });
            }
        }
        "delete-game" => {
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
        "version-install" => {
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
        "create-shortcut" => {
            if let Some((game, target)) = selected(state) {
                operation(ui, state, move || {
                    let option = services.option(&game, target)?;
                    let exe = launch::resolve_game_exe(
                        &option.game_dir,
                        &game.exe_relative_path,
                        &game
                            .mods
                            .as_ref()
                            .map(|m| m.exe_names.clone())
                            .unwrap_or_default(),
                    )
                    .ok();
                    let filename = game
                        .title
                        .chars()
                        .map(|c| if r#"<>:"/\|?*"#.contains(c) { '_' } else { c })
                        .collect::<String>();
                    let output = service::desktop_path()?.join(format!(
                        "{} · {}.lnk",
                        filename.trim_end_matches(['.', ' ']),
                        target.as_str()
                    ));
                    launch::create_game_shortcut(
                        &std::env::current_exe().map_err(|e| e.to_string())?,
                        &output,
                        &ShortcutRequest {
                            game_id: game.game_id,
                            title: game.title,
                            exe_path: exe,
                            target: Some(target),
                        },
                    )?;
                    Ok("Ярлык создан на рабочем столе".into())
                });
            }
        }
        "security-check" | "security-diagnostics" => {
            ui.set_operation_busy(true);
            ui.set_panel_body("Проверяем состояние защиты и последние блокировки…".into());
            background_for(
                ui,
                Activity::Operation,
                chillhub_native::security::inspect,
                |ui, state, r| {
                    ui.set_operation_busy(false);
                    match r {
                        Ok(report) => ui.set_panel_body(report.summary().into()),
                        Err(e) => {
                            ui.set_panel_body(format!("Не удалось проверить защиту: {e}").into());
                            toast(ui, state, &e);
                        }
                    }
                },
            );
        }
        "security-helper" => {
            if let Some((game, target)) = selected(state) {
                match services.option(&game, target) {
                    // The helper shows the exact paths and owns confirmation.
                    // Merely opening it does not change Windows protection.
                    Ok(o) => operation(ui, state, move || {
                        chillhub_native::security::launch_helper(
                            &[o.game_dir],
                            chillhub_native::security::SecurityOperation::Add,
                        )
                        .map(|_| "Открыт помощник антивируса".into())
                    }),
                    Err(e) => toast(ui, state, &e),
                }
            } else {
                toast(ui, state, "Сначала выберите игру в библиотеке");
            }
        }
        "security-open" | "windows-security" => {
            if let Err(e) = chillhub_native::security::open_windows_security() {
                toast(ui, state, &e);
            }
        }
        "update-check" => {
            state.borrow_mut().update = None;
            ui.set_operation_busy(true);
            ui.set_update_available(false);
            ui.set_update_checked(false);
            ui.set_update_error(false);
            ui.set_update_target("".into());
            ui.set_task_status("".into());
            ui.set_task_progress(0.);
            ui.set_panel_body("Ищем новую версию Chill Hub…".into());
            background_for(
                ui,
                Activity::Operation,
                move || {
                    update::check_release(
                        &services.api.client,
                        services.api.base.as_str(),
                        env!("CARGO_PKG_VERSION"),
                    )
                },
                |ui, state, r| {
                    ui.set_operation_busy(false);
                    ui.set_update_checked(true);
                    match r {
                        Ok(UpdateCheck::Current { .. }) => {
                            state.borrow_mut().update = None;
                            ui.set_panel_body("У вас актуальная версия Chill Hub.".into());
                        }
                        Ok(UpdateCheck::Available(release)) => {
                            ui.set_update_available(true);
                            ui.set_update_target(release.version.clone().into());
                            ui.set_panel_body(format!("Доступна версия {}. Игры и настройки сохранятся. После загрузки лаунчер перезапустится.",release.version).into());
                            state.borrow_mut().update = Some(release);
                        }
                        Err(e) => {
                            ui.set_update_error(true);
                            ui.set_panel_body(format!("Не удалось проверить обновления. Проверьте подключение и попробуйте снова.\n\n{e}").into());
                        }
                    }
                },
            );
        }
        "update-verify" => {
            let Some(release) = state.borrow().update.clone() else {
                toast(ui, state, "Сначала проверьте обновления");
                return;
            };
            let paths = match update_paths(&services) {
                Ok(paths) => paths,
                Err(e) => {
                    toast(ui, state, &e);
                    return;
                }
            };
            ui.set_operation_busy(true);
            ui.set_update_error(false);
            ui.set_task_progress(0.);
            ui.set_task_status("Проверяем установленные файлы…".into());
            let weak = ui.as_weak();
            background_for(
                ui,
                Activity::Operation,
                move || {
                    update::verify_installed_release(
                        &release,
                        &paths,
                        &TransferControl::default(),
                        &move |e| update_progress(&weak, e),
                    )
                },
                |ui, state, result| {
                    ui.set_operation_busy(false);
                    match result {
                        Ok(report) if report.mismatches.is_empty() => {
                            ui.set_task_progress(1.);
                            ui.set_task_status(
                                format!("Проверено файлов: {}", report.checked_files).into(),
                            );
                            ui.set_panel_body("Все установленные файлы соответствуют выбранному выпуску. Номер версии восстановлен, ограничение повторных попыток снято.".into());
                        }
                        Ok(report) => {
                            ui.set_task_status(
                                format!(
                                    "Проверено файлов: {} · отличаются: {}",
                                    report.checked_files,
                                    report.mismatches.len()
                                )
                                .into(),
                            );
                            let names = report
                                .mismatches
                                .iter()
                                .take(12)
                                .cloned()
                                .collect::<Vec<_>>()
                                .join("\n");
                            ui.set_panel_body(format!("Некоторые файлы отличаются от выпуска. Файлы лаунчера и игр не изменены. Если повторное обновление заблокировано, скачайте установщик с официального сайта.\n\n{names}").into());
                        }
                        Err(e) => {
                            ui.set_update_error(true);
                            ui.set_task_status(e.clone().into());
                            toast(ui, state, &e);
                        }
                    }
                },
            );
        }
        "update-install" => {
            let Some(release) = state.borrow().update.clone() else {
                toast(ui, state, "Сначала проверьте обновления");
                return;
            };
            if !services.idle() {
                toast(
                    ui,
                    state,
                    "Сначала завершите или приостановите загрузки игр",
                );
                return;
            }
            let exe = match std::env::current_exe() {
                Ok(p) => p,
                Err(e) => {
                    toast(ui, state, &e.to_string());
                    return;
                }
            };
            let stage = staging_root(&services.paths);
            let paths = UpdatePaths {
                install: exe.parent().unwrap().into(),
                config: services.paths.data_dir.clone(),
                games: services.paths.games_dir.clone(),
                staging: stage,
            };
            ui.set_operation_busy(true);
            ui.set_update_error(false);
            ui.set_task_progress(0.);
            ui.set_task_status("Подготавливаем обновление…".into());
            let weak = ui.as_weak();
            background_for(
                ui,
                Activity::Operation,
                move || {
                    let staged = update::stage_update(
                        &release,
                        &paths,
                        &TransferControl::default(),
                        &move |e| {
                            let weak = weak.clone();
                            let _ = slint::invoke_from_event_loop(move || {
                                if let Some(ui) = weak.upgrade() {
                                    ui.set_task_progress(if e.total_bytes > 0 {
                                        e.completed_bytes as f32 / e.total_bytes as f32
                                    } else {
                                        0.
                                    });
                                    ui.set_task_status(e.current_file.into());
                                }
                            });
                        },
                    )?;
                    update::launch_updater(&staged, &[]).map(|_| ())
                },
                |ui, state, result| {
                    ui.set_operation_busy(false);
                    match result {
                        Ok(()) => {
                            save_window(ui, state);
                            state.borrow().services.queue.shutdown();
                            let _ = slint::quit_event_loop();
                        }
                        Err(e) => {
                            ui.set_update_error(true);
                            ui.set_task_status(format!("{e}\nЕсли обновление не удаётся повторить, нажмите «Проверить установку».").into());
                            toast(ui, state, &e);
                        }
                    }
                },
            );
        }
        _ => toast(ui, state, "Это действие сейчас недоступно"),
    }
}
pub(super) fn update_paths(services: &Services) -> Result<UpdatePaths, String> {
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    Ok(UpdatePaths {
        install: exe.parent().ok_or("Папка лаунчера не найдена")?.into(),
        config: services.paths.data_dir.clone(),
        games: services.paths.games_dir.clone(),
        staging: staging_root(&services.paths),
    })
}
fn update_progress(weak: &slint::Weak<AppWindow>, e: chillhub_native::transfer::TransferEvent) {
    let weak = weak.clone();
    let _ = slint::invoke_from_event_loop(move || {
        if let Some(ui) = weak.upgrade() {
            ui.set_task_progress(if e.total_bytes > 0 {
                e.completed_bytes as f32 / e.total_bytes as f32
            } else {
                0.
            });
            ui.set_task_status(
                format!(
                    "Проверено файлов: {} из {}\n{}",
                    e.completed_files, e.total_files, e.current_file
                )
                .into(),
            );
        }
    });
}
pub(super) fn bind(ui: &AppWindow, state: &State) {
    macro_rules! handler {($name:ident,|$u:ident,$s:ident $(,$a:ident)*|$body:block)=>{{let weak=ui.as_weak();let weak_state=Rc::downgrade(state);ui.$name(move|$($a),*|{if let(Some($u),Some($s))=(weak.upgrade(),weak_state.upgrade())$body});}};}
    handler!(on_action_requested, |ui, state, value| {
        action(&ui, &state, value.as_str());
    });
    handler!(on_orphan_action, |ui, state, id, value| {
        if value == "toggle" {
            orphan_ui::toggle(&ui, id.as_str());
            return;
        }
        let paths = state.borrow().services.paths.clone();
        let records = state.borrow().records.clone();
        let id = id.to_string();
        let found = chillhub_native::library::orphaned(&paths, &records)
            .ok()
            .and_then(|rows| rows.into_iter().find(|r| r.id == id));
        if let Some(found) = found {
            if value == "open" {
                if let Err(e) = service::open_directory(&found.path) {
                    toast(&ui, &state, &e);
                }
            } else if value == "delete" {
                confirm(
                    &ui,
                    &state,
                    Confirmation::Delete(GameRecord {
                        game_id: id,
                        title: found.id,
                        ..Default::default()
                    }),
                    "Удалить установленную копию?",
                    format!(
                        "Будет удалена папка:\n{}\n\nСохранения и вручную добавленные файлы внутри неё тоже будут удалены.",
                        found.path.display()
                    ),
                    "Удалить копию",
                );
            }
        }
    });
    handler!(on_setting_changed, |ui, state, key, value| {
        if key == "karaoke" {
            ui.set_karaoke_enabled(value == "true");
            save_live_settings(&ui, &state);
        }
        if key == "reduced-motion" {
            ui.set_reduced_motion(value == "true");
            save_live_settings(&ui, &state);
        }
    });
    handler!(on_version_selected, |ui, _state, value| {
        ui.set_selected_version(value);
    });
    handler!(on_queue_action, |ui, state, id, value| {
        let id = id.to_string();
        let queue = state.borrow().services.queue.clone();
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
                    &ui,
                    &state,
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
                let more = task.cleanup_paths.len().saturating_sub(25);
                let mut list = task
                    .cleanup_paths
                    .iter()
                    .take(25)
                    .cloned()
                    .collect::<Vec<_>>()
                    .join("\n");
                if more > 0 {
                    list.push_str(&format!("\n…и ещё {more}"));
                }
                confirm(
                    &ui,
                    &state,
                    Confirmation::Cleanup(id),
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
        operation(&ui, &state, move || {
            match value.as_str() {
                "pause" => queue.pause(&id),
                "resume" => queue.resume(&id),
                "cancel" => queue.cancel(&id),
                "remove" => queue.remove(&id),
                "first" => {
                    let first = queue.snapshot().first().map(|i| i.id.clone());
                    queue.move_before(&id, first.as_deref())
                }
                _ => Ok(()),
            }
            .map(|_| "Очередь изменена".into())
            .map_err(|e| e.to_string())
        });
    });
    handler!(on_settings_save, |ui,
                                state,
                                path,
                                threads,
                                speed,
                                api,
                                tray,
                                reduced| {
        ui.set_settings_path(path);
        ui.set_settings_threads(threads);
        ui.set_settings_speed(speed);
        ui.set_settings_api(api);
        ui.set_settings_tray(tray);
        ui.set_reduced_motion(reduced);
        save_settings(&ui, &state, "".into());
    });
    handler!(on_settings_leave, |ui, state, next| {
        save_settings(&ui, &state, next.to_string());
    });
    handler!(on_settings_live_changed, |ui, state| {
        save_live_settings(&ui, &state);
    });
    handler!(on_feedback_send, |ui,
                                state,
                                name,
                                contact,
                                kind,
                                message,
                                logs| {
        let service = state.borrow().services.feedback.clone();
        let draft = FeedbackDraft {
            name: name.to_string(),
            contact: contact.to_string(),
            kind: kind.to_string(),
            comment: message.to_string(),
            attach_logs: logs,
            ..Default::default()
        };
        ui.set_operation_busy(true);
        background_for(
            &ui,
            Activity::Operation,
            move || service.submit(draft),
            |ui, state, result| {
                ui.set_operation_busy(false);
                match result {
                    SendResult::Sent => {
                        close_panel(ui, state);
                        toast(ui, state, "Сообщение отправлено. Спасибо!");
                    }
                    SendResult::QueuedPersisted => {
                        close_panel(ui, state);
                        toast(
                            ui,
                            state,
                            "Сообщение сохранено и будет отправлено, когда появится связь",
                        );
                    }
                    SendResult::Rejected(e) | SendResult::LocalSaveFailed(e) => {
                        toast(ui, state, &e)
                    }
                    SendResult::RateLimited(n) => {
                        toast(ui, state, &format!("Повторите отправку через {n} секунд"))
                    }
                }
            },
        );
    });
}
pub(super) fn save_window(ui: &AppWindow, state: &State) {
    use slint::winit_030::WinitWindowAccessor;
    let store = state.borrow().services.config.clone();
    ui.window().with_winit_window(|w| {
        let size = w.outer_size().to_logical::<f64>(w.scale_factor());
        let maximized = w.is_maximized();
        let _ = store.update(|c| {
            if !maximized {
                c.window_width = size.width;
                c.window_height = size.height;
            }
            c.window_maximized = maximized;
        });
    });
}
pub(super) fn external_request(ui: &AppWindow, args: Vec<String>) {
    ACTIVE_STATE.with(|slot| {
        if let Some(state) = slot.borrow().as_ref()
            && let Ok(Some(request)) = ShortcutRequest::parse_args(&args)
        {
            state.borrow_mut().shortcut = Some(request);
            reload(ui, state);
        }
    });
}
