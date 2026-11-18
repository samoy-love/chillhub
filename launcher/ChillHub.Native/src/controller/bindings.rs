use super::{
    actions::dispatch,
    background::{Activity, background_for},
    confirmation::{Confirmation, confirm},
    downloads,
    settings::{AfterSave, save as save_settings, save_live as save_live_settings},
    ui_action::UiAction,
};
use crate::{AppWindow, State, close_panel, orphan_ui, toast};
use chillhub_native::{
    feedback::{FeedbackDraft, SendResult},
    model::GameRecord,
    service,
};
use slint::ComponentHandle;
use std::rc::Rc;

pub(crate) fn bind(ui: &AppWindow, state: &State) {
    macro_rules! handler {
        ($name:ident, |$ui:ident, $state:ident $(, $arg:ident)*| $body:block) => {{
            let weak = ui.as_weak();
            let weak_state = Rc::downgrade(state);
            ui.$name(move |$($arg),*| {
                if let (Some($ui), Some($state)) = (weak.upgrade(), weak_state.upgrade()) {
                    $body
                }
            });
        }};
    }
    handler!(on_action_requested, |ui, state, value| {
        if let Some(requested) = UiAction::parse(value.as_str()) {
            dispatch(&ui, &state, requested);
        } else {
            toast(&ui, &state, "Это действие сейчас недоступно");
        }
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
        downloads::handle_action(&ui, &state, id.as_str(), value.as_str());
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
        save_settings(&ui, &state, AfterSave::Close);
    });
    handler!(on_settings_leave, |ui, state, next| {
        save_settings(&ui, &state, AfterSave::from_panel(next.as_str()));
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
