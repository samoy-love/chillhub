use super::{
    background::{Activity, background_for, operation},
    choice::selected,
};
use crate::{AppWindow, State, toast};
use chillhub_native::security::{self, SecurityOperation};
use std::path::PathBuf;

pub(super) fn check(ui: &AppWindow) {
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

pub(super) fn open_helper(ui: &AppWindow, state: &State) {
    let services = state.borrow().services.clone();
    if let Some((game, target)) = selected(state) {
        match services.option(&game, target) {
            // The helper shows the exact paths and owns confirmation.
            // Merely opening it does not change Windows protection.
            Ok(o) => operation(ui, move || {
                security::launch_helper(&[o.game_dir], SecurityOperation::Add)
                    .map(|_| "Открыт помощник антивируса".into())
            }),
            Err(e) => toast(ui, state, &e),
        }
    } else {
        toast(ui, state, "Сначала выберите игру в библиотеке");
    }
}

pub(super) fn open_windows_security(ui: &AppWindow, state: &State) {
    if let Err(e) = security::open_windows_security() {
        toast(ui, state, &e);
    }
}

pub(super) fn open_for_paths(ui: &AppWindow, paths: Vec<PathBuf>) {
    operation(ui, move || {
        security::launch_helper(&paths, SecurityOperation::Add)
            .map(|_| "Открыт помощник защиты".into())
    });
}
