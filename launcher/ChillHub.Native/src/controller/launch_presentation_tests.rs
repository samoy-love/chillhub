use super::*;
use chillhub_native::{config::LaunchTarget, net::MaintenanceBlocks};

fn copy() -> InstalledCopyFacts {
    InstalledCopyFacts {
        can_verify_game: true,
        can_verify_mods: true,
        has_game_files: true,
        has_executable: true,
        running: false,
        queued: false,
        version_marker: String::new(),
        playtime_seconds: 0,
    }
}

fn fixture_option(action: LaunchAction) -> LaunchOption {
    LaunchOption {
        target: LaunchTarget::LocalModded,
        action,
        game_dir: "fixture".into(),
        note: String::new(),
    }
}

#[test]
fn every_action_has_its_primary_button_and_library_badge() {
    let copy = copy();
    let maintenance = Maintenance::default();
    for (action, button, badge, kind, can_play) in [
        (LaunchAction::Play, "Играть", "Установлена", "ready", true),
        (
            LaunchAction::InstallGame,
            "Установить",
            "Не установлена",
            "idle",
            true,
        ),
        (
            LaunchAction::UpdateGame,
            "Обновить",
            "Доступно обновление",
            "update",
            true,
        ),
        (
            LaunchAction::InstallMods,
            "Установить моды",
            "Моды не установлены",
            "idle",
            true,
        ),
        (
            LaunchAction::UpdateMods,
            "Обновить моды",
            "Доступно обновление",
            "update",
            true,
        ),
        (
            LaunchAction::RepairMods,
            "Восстановить моды",
            "Нужно восстановление",
            "update",
            true,
        ),
        (
            LaunchAction::Unavailable,
            "Недоступно",
            "Копия недоступна",
            "idle",
            false,
        ),
    ] {
        let mut option = fixture_option(action);
        option.note = "Состояние выбранной копии".into();
        let view = present(&option, &copy, &maintenance, false);
        assert_eq!(view.play_label, button, "{:?}", option.action);
        assert_eq!(view.status, "Состояние выбранной копии");
        assert_eq!(view.can_play, can_play);
        assert_eq!(
            library_label(&option.action, false),
            (badge.to_owned(), kind.to_owned())
        );
    }
}

#[test]
fn higher_priority_activity_controls_both_primary_button_and_status() {
    let option = fixture_option(LaunchAction::Play);
    let mut copy = copy();
    copy.running = true;
    copy.queued = true;
    let maintenance = Maintenance {
        enabled: true,
        blocks: MaintenanceBlocks {
            install: false,
            update: false,
            launch: true,
        },
        ..Default::default()
    };

    let launching = present(&option, &copy, &maintenance, true);
    assert_eq!(launching.play_label, "Запускаем…");
    assert_eq!(launching.status, "Ожидаем запуска игры");
    let running = present(&option, &copy, &maintenance, false);
    assert_eq!(running.play_label, "Игра запущена");
    assert_eq!(running.status, "Игра уже запущена");
    assert_eq!(
        library_label(&LaunchAction::UpdateMods, true),
        ("Игра запущена".to_owned(), "active".to_owned())
    );
    copy.running = false;
    let queued = present(&option, &copy, &maintenance, false);
    assert_eq!(queued.play_label, "В очереди");
    assert_eq!(
        queued.status,
        "Откройте загрузки, чтобы продолжить или отменить задачу"
    );
    copy.queued = false;
    let servicing = present(&option, &copy, &maintenance, false);
    assert_eq!(servicing.play_label, "Технические работы");
    assert_eq!(servicing.status, "Ожидаем завершения работ на сервере");
    for busy in [launching, running, queued] {
        assert!(!busy.can_play);
        assert!(!busy.can_verify_game);
        assert!(!busy.can_verify_mods);
        assert!(busy.has_game_files);
        assert!(busy.can_create_shortcut);
    }
    assert!(!servicing.can_play);
    assert!(servicing.can_verify_game && servicing.can_verify_mods);

    let install = fixture_option(LaunchAction::InstallGame);
    let allowed = present(&install, &copy, &maintenance, false);
    assert_eq!(allowed.play_label, "Установить");
    assert!(allowed.can_play);
}

#[test]
fn verification_folder_and_shortcut_follow_separate_copy_facts() {
    let mut option = fixture_option(LaunchAction::Unavailable);
    option.note = "Файл запуска игры не найден".into();
    let mut copy = copy();
    copy.can_verify_mods = false;
    copy.has_executable = false;
    let maintenance = Maintenance::default();
    let local = present(&option, &copy, &maintenance, false);
    assert!(!local.can_play);
    assert_eq!(local.status, "Файл запуска игры не найден");
    assert!(local.can_verify_game && local.has_game_files);
    assert!(!local.can_verify_mods && !local.can_create_shortcut);

    option.target = LaunchTarget::SteamModded;
    copy.can_verify_game = false;
    copy.can_verify_mods = true;
    copy.has_executable = true;
    let steam = present(&option, &copy, &maintenance, false);
    assert!(!steam.can_verify_game);
    assert!(steam.can_verify_mods && steam.can_create_shortcut);
}

#[test]
fn version_and_playtime_match_the_selected_source_and_boundaries() {
    let mut option = fixture_option(LaunchAction::Play);
    let maintenance = Maintenance::default();
    let mut copy = copy();
    let empty = present(&option, &copy, &maintenance, false);
    assert_eq!(empty.installed_version, "Не установлена");
    assert_eq!(empty.status, "Готово к запуску");
    copy.version_marker = "1.2.3".into();
    for (seconds, label) in [
        (-1, "Сыграно меньше минуты"),
        (0, "Ещё не играли"),
        (1, "Сыграно меньше минуты"),
        (59, "Сыграно меньше минуты"),
        (60, "Сыграно 0 ч 1 мин"),
        (3599, "Сыграно 0 ч 59 мин"),
        (3660, "Сыграно 1 ч 1 мин"),
    ] {
        copy.playtime_seconds = seconds;
        let local = present(&option, &copy, &maintenance, false);
        assert_eq!(local.installed_version, "Установлена 1.2.3");
        assert_eq!(local.playtime_label, label);
    }

    option.target = LaunchTarget::SteamVanilla;
    let steam = present(&option, &copy, &maintenance, false);
    assert_eq!(steam.installed_version, "Версией управляет Steam");
    assert_eq!(steam.playtime_label, "Сыграно 1 ч 1 мин");
}
