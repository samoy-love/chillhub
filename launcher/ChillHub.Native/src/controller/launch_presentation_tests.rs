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
            LaunchAction::ResumeInstall,
            "Продолжить установку",
            "Установка прервана",
            "interrupted",
            true,
        ),
        (
            LaunchAction::UpdateGame,
            "Обновить",
            "Есть обновление",
            "update",
            true,
        ),
        (
            LaunchAction::RepairGame,
            "Восстановить",
            "Нужно восстановить",
            "repair",
            true,
        ),
        (
            LaunchAction::InstallMods,
            "Установить моды",
            "Нужны моды",
            "idle",
            true,
        ),
        (
            LaunchAction::UpdateMods,
            "Обновить моды",
            "Есть обновление",
            "update",
            true,
        ),
        (
            LaunchAction::RepairMods,
            "Восстановить моды",
            "Восстановить моды",
            "repair",
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
        ("Игра запущена".to_owned(), "running".to_owned())
    );
    copy.running = false;
    let queued = present(&option, &copy, &maintenance, false);
    assert_eq!(queued.play_label, "В очереди");
    assert_eq!(queued.status, "Управляйте задачей в разделе «Загрузки»");
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

#[test]
fn library_falls_back_to_the_most_ready_available_copy() {
    let preferred = LaunchTarget::SteamModded;
    for (copies, expected) in [
        (
            [LaunchAction::InstallMods, LaunchAction::RepairMods],
            "Восстановить моды",
        ),
        (
            [LaunchAction::RepairMods, LaunchAction::UpdateMods],
            "Есть обновление",
        ),
        (
            [LaunchAction::UpdateGame, LaunchAction::Play],
            "Установлена",
        ),
        (
            [LaunchAction::InstallGame, LaunchAction::InstallMods],
            "Нужны моды",
        ),
    ] {
        let [first, second] = copies;
        let options = [
            LaunchOption {
                target: preferred,
                ..fixture_option(LaunchAction::Unavailable)
            },
            LaunchOption {
                target: LaunchTarget::LocalModded,
                ..fixture_option(first)
            },
            LaunchOption {
                target: LaunchTarget::LocalVanilla,
                ..fixture_option(second)
            },
        ];
        assert_eq!(library_copies_label(&options, preferred, false).0, expected);
    }
}

#[test]
fn library_describes_installed_copies_without_changing_the_launch_choice() {
    let make = |target, action| LaunchOption {
        target,
        ..fixture_option(action)
    };
    let options = [
        make(LaunchTarget::SteamModded, LaunchAction::Unavailable),
        make(LaunchTarget::SteamVanilla, LaunchAction::Unavailable),
        make(LaunchTarget::LocalModded, LaunchAction::Play),
        make(LaunchTarget::LocalVanilla, LaunchAction::Play),
    ];
    assert_eq!(
        library_copies_label(&options, LaunchTarget::SteamModded, false),
        ("Установлена".into(), "ready".into())
    );
    assert_eq!(options[0].action, LaunchAction::Unavailable);

    let not_installed = [
        make(LaunchTarget::SteamModded, LaunchAction::Unavailable),
        make(LaunchTarget::LocalModded, LaunchAction::InstallGame),
    ];
    assert_eq!(
        library_copies_label(&not_installed, LaunchTarget::SteamModded, false),
        ("Не установлена".into(), "idle".into())
    );
    let interrupted = [
        make(LaunchTarget::LocalVanilla, LaunchAction::InstallGame),
        make(LaunchTarget::LocalModded, LaunchAction::ResumeInstall),
        make(LaunchTarget::SteamVanilla, LaunchAction::Unavailable),
    ];
    assert_eq!(
        library_copies_label(&interrupted, LaunchTarget::SteamVanilla, false),
        ("Установка прервана".into(), "interrupted".into())
    );
    let interrupted_and_installed = [
        make(LaunchTarget::LocalModded, LaunchAction::ResumeInstall),
        make(LaunchTarget::SteamVanilla, LaunchAction::Play),
    ];
    assert_eq!(
        library_copies_label(&interrupted_and_installed, LaunchTarget::LocalModded, false),
        ("Установлена".into(), "ready".into())
    );
    let steam_only = [make(LaunchTarget::SteamVanilla, LaunchAction::Play)];
    assert_eq!(
        library_copies_label(&steam_only, LaunchTarget::LocalVanilla, false),
        ("Установлена".into(), "ready".into())
    );
    let selected_needs_repair = [
        make(LaunchTarget::LocalModded, LaunchAction::RepairGame),
        make(LaunchTarget::SteamVanilla, LaunchAction::Play),
    ];
    assert_eq!(
        library_copies_label(&selected_needs_repair, LaunchTarget::LocalModded, false),
        ("Нужно восстановить".into(), "repair".into())
    );
    assert_eq!(
        library_copies_label(&selected_needs_repair, LaunchTarget::LocalModded, true),
        ("Игра запущена".into(), "running".into())
    );
    assert_eq!(
        library_copies_label(&[], LaunchTarget::LocalVanilla, false),
        ("Копия недоступна".into(), "idle".into())
    );
}
