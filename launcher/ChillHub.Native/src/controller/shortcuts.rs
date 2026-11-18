use super::{
    background::{Activity, background_for, operation},
    catalog::reload,
    choice::selected,
    confirmation::{Confirmation, confirm},
    play::watch_game,
};
use crate::{ACTIVE_STATE, AppWindow, State, preview_directory, toast};
use chillhub_native::{
    launch::{self, LaunchAction, ShortcutRequest},
    service,
};
use slint::{ComponentHandle, Timer};
use std::{rc::Rc, time::Duration};

pub(crate) fn external_request(ui: &AppWindow, args: Vec<String>) {
    ACTIVE_STATE.with(|slot| {
        if let Some(state) = slot.borrow().as_ref()
            && let Ok(Some(request)) = ShortcutRequest::parse_args(&args)
        {
            let mut s = state.borrow_mut();
            if s.pending_shortcuts.len() + usize::from(s.shortcut.is_some()) >= 32 {
                drop(s);
                toast(
                    ui,
                    state,
                    "Слишком много запросов запуска. Откройте ярлык ещё раз позже.",
                );
                return;
            }
            let was_empty = s.pending_shortcuts.is_empty();
            s.pending_shortcuts.push_back(request);
            drop(s);
            if was_empty {
                pump_shortcuts(ui, state);
            }
        }
    });
}

pub(super) fn take_catalog_shortcut(ui: &AppWindow, state: &State) -> Option<ShortcutRequest> {
    let mut s = state.borrow_mut();
    let request = s.shortcut.take()?;
    if ui.get_operation_busy() || !ui.get_panel_kind().is_empty() {
        let was_empty = s.pending_shortcuts.is_empty();
        s.pending_shortcuts.push_front(request);
        drop(s);
        if was_empty {
            schedule_shortcuts(ui, state);
        }
        return None;
    }
    Some(request)
}

fn pump_shortcuts(ui: &AppWindow, state: &State) {
    let mut s = state.borrow_mut();
    let ready = ui.window().is_visible()
        && !ui.window().is_minimized()
        && !ui.get_loading()
        && !ui.get_busy()
        && !ui.get_operation_busy()
        && ui.get_panel_kind().is_empty()
        && s.shortcut.is_none()
        && s.launching.is_empty();
    if ready {
        s.shortcut = s.pending_shortcuts.pop_front();
    }
    let started = ready && s.shortcut.is_some();
    let queued = !s.pending_shortcuts.is_empty();
    drop(s);
    if started {
        reload(ui, state);
    }
    if queued {
        schedule_shortcuts(ui, state);
    }
}

fn schedule_shortcuts(ui: &AppWindow, state: &State) {
    let weak = ui.as_weak();
    let state = Rc::downgrade(state);
    Timer::single_shot(Duration::from_millis(250), move || {
        if let (Some(ui), Some(state)) = (weak.upgrade(), state.upgrade()) {
            pump_shortcuts(&ui, &state);
        }
    });
}

pub(super) fn offer_shortcut(ui: &AppWindow, state: &State, request: ShortcutRequest) {
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

pub(super) fn create(ui: &AppWindow, state: &State) {
    let services = state.borrow().services.clone();
    if let Some((game, target)) = selected(state) {
        operation(ui, move || {
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
            let args = std::env::args().collect::<Vec<_>>();
            let preview = preview_directory(&args)?;
            let output = service::desktop_path()?.join(launch::game_shortcut_filename(
                &game.title,
                target,
                preview.is_some(),
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
                preview,
            )?;
            Ok("Ярлык создан на рабочем столе".into())
        });
    }
}

pub(super) fn launch_fallback(ui: &AppWindow, state: &State, request: ShortcutRequest) {
    let services = state.borrow().services.clone();
    ui.set_operation_busy(true);
    background_for(
        ui,
        Activity::Operation,
        move || {
            services.maintenance_check(&LaunchAction::Play)?;
            services.launch_service().launch_shortcut(&request)
        },
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
