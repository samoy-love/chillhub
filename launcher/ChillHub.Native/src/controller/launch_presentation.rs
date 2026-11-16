//! Derives the selected copy's display from a filesystem probe and current activity.
//! The controller collects those inputs and applies the resulting presentation to Slint.

use chillhub_native::{
    launch::{LaunchAction, LaunchOption},
    net::Maintenance,
    service,
};

pub(super) struct InstalledCopyFacts {
    pub(super) can_verify_game: bool,
    pub(super) can_verify_mods: bool,
    pub(super) has_game_files: bool,
    pub(super) has_executable: bool,
    pub(super) running: bool,
    pub(super) queued: bool,
    pub(super) version_marker: String,
    pub(super) playtime_seconds: i64,
}

pub(super) struct LaunchPresentation<'a> {
    pub(super) can_verify_game: bool,
    pub(super) can_verify_mods: bool,
    pub(super) has_game_files: bool,
    pub(super) can_create_shortcut: bool,
    pub(super) can_play: bool,
    pub(super) play_label: &'static str,
    pub(super) status: &'a str,
    pub(super) installed_version: String,
    pub(super) playtime_label: String,
}

enum Blocker {
    Launching,
    Running,
    Queued,
    Maintenance,
}

pub(super) fn present<'a>(
    option: &'a LaunchOption,
    copy: &InstalledCopyFacts,
    maintenance: &Maintenance,
    launching: bool,
) -> LaunchPresentation<'a> {
    let blocker = if launching {
        Some(Blocker::Launching)
    } else if copy.running {
        Some(Blocker::Running)
    } else if copy.queued {
        Some(Blocker::Queued)
    } else if service::blocked(maintenance, &option.action) {
        Some(Blocker::Maintenance)
    } else {
        None
    };
    let (play_label, status) = match blocker {
        Some(Blocker::Launching) => ("Запускаем…", "Ожидаем запуска игры"),
        Some(Blocker::Running) => ("Игра запущена", "Игра уже запущена"),
        Some(Blocker::Queued) => (
            "В очереди",
            "Откройте загрузки, чтобы продолжить или отменить задачу",
        ),
        Some(Blocker::Maintenance) => ("Технические работы", "Ожидаем завершения работ на сервере"),
        None => {
            let label = match option.action {
                LaunchAction::Play => "Играть",
                LaunchAction::InstallGame => "Установить",
                LaunchAction::UpdateGame => "Обновить",
                LaunchAction::InstallMods => "Установить моды",
                LaunchAction::UpdateMods => "Обновить моды",
                LaunchAction::RepairMods => "Восстановить моды",
                LaunchAction::Unavailable => "Недоступно",
            };
            let status = if option.note.is_empty() {
                "Готово к запуску"
            } else {
                option.note.as_str()
            };
            (label, status)
        }
    };
    let can_verify = matches!(blocker, None | Some(Blocker::Maintenance));
    let installed_version = if option.target.via_steam() {
        "Версией управляет Steam".into()
    } else if copy.version_marker.is_empty() {
        "Не установлена".into()
    } else {
        format!("Установлена {}", copy.version_marker)
    };
    let playtime_label = match copy.playtime_seconds {
        0 => "Ещё не играли".into(),
        ..=59 => "Сыграно меньше минуты".into(),
        seconds => format!("Сыграно {} ч {} мин", seconds / 3600, (seconds / 60) % 60),
    };
    LaunchPresentation {
        can_verify_game: copy.can_verify_game && can_verify,
        can_verify_mods: copy.can_verify_mods && can_verify,
        has_game_files: copy.has_game_files,
        can_create_shortcut: copy.has_executable,
        can_play: blocker.is_none() && option.available(),
        play_label,
        status,
        installed_version,
        playtime_label,
    }
}

pub(super) fn library_label(action: &LaunchAction, running: bool) -> (String, String) {
    if running {
        return ("Игра запущена".into(), "active".into());
    }
    let (text, kind) = match action {
        LaunchAction::Play => ("Установлена", "ready"),
        LaunchAction::UpdateGame | LaunchAction::UpdateMods => ("Доступно обновление", "update"),
        LaunchAction::RepairMods => ("Нужно восстановление", "update"),
        LaunchAction::InstallGame => ("Не установлена", "idle"),
        LaunchAction::InstallMods => ("Моды не установлены", "idle"),
        LaunchAction::Unavailable => ("Копия недоступна", "idle"),
    };
    (text.into(), kind.into())
}

#[cfg(test)]
#[path = "launch_presentation_tests.rs"]
mod tests;
