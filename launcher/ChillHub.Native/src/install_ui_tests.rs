use super::*;
#[test]
fn install_estimate_warns_for_current_choice_and_clears_stale_warnings() {
    slint::platform::set_platform(Box::new(i_slint_backend_testing::TestingBackend::new(
        i_slint_backend_testing::TestingBackendOptions {
            mock_time: true,
            threading: false,
            renderer_name: Some("software".into()),
        },
    )))
    .expect("headless test platform only");
    let ui = AppWindow::new().unwrap();
    ui.set_game_title("Пример".into());
    ui.set_status("Установить игру".into());
    ui.set_can_play(true);
    ui.set_disk_label("Свободно 52.8 ГБ".into());
    let low_estimate = DownloadEstimate {
        bytes_to_prepare: 1024,
        required_free_bytes: 2048,
        available_bytes: Some(1024),
    };
    let low = Ok(low_estimate);
    let sufficient = Ok(DownloadEstimate {
        available_bytes: Some(20 * 1024 * 1024 * 1024),
        ..low_estimate
    });
    let unknown = Ok(DownloadEstimate {
        available_bytes: None,
        ..low_estimate
    });

    assert!(apply_current_result(&ui, 1, 1, &low));
    assert!(ui.get_install_space_low());
    assert!(ui.get_show_install_space_warning());
    assert_eq!(ui.get_game_status_text(), "Мало места");
    assert!(ui.get_shown_install_hint().contains("Недостаточно места"));
    assert!(
        ui.get_can_play(),
        "the current preflight decides whether to proceed"
    );
    assert_eq!(ui.get_status(), "Установить игру");

    assert!(apply_current_result(&ui, 1, 1, &sufficient));
    assert_eq!(ui.get_disk_label(), "Свободно 20.0 ГБ");
    assert!(ui.get_shown_install_hint().contains("свободно 20.0 ГБ"));
    assert!(!ui.get_disk_label().contains("52.8"));
    assert!(!ui.get_install_space_low());
    assert_eq!(ui.get_game_status_text(), "Установить игру");

    assert!(apply_current_result(&ui, 1, 1, &low));
    assert!(apply_current_result(&ui, 1, 1, &unknown));
    assert_eq!(ui.get_disk_label(), "Не удалось определить свободное место");
    assert!(!ui.get_show_install_space_warning());
    assert!(!ui.get_shown_install_hint().contains("свободно"));

    assert!(apply_current_result(&ui, 1, 1, &low));
    assert!(apply_current_result(
        &ui,
        1,
        1,
        &Err("fixture offline".into())
    ));
    assert_eq!(
        ui.get_shown_install_hint(),
        "Размер уточнится перед загрузкой"
    );
    assert!(!ui.get_install_space_low());

    assert!(apply_current_result(&ui, 2, 2, &sufficient));
    assert!(!apply_current_result(&ui, 1, 2, &low));
    assert_eq!(ui.get_game_status_text(), "Установить игру");
    assert!(ui.get_shown_install_hint().contains("свободно 20.0 ГБ"));

    for set_blocked in [
        AppWindow::set_busy as fn(&AppWindow, bool),
        AppWindow::set_operation_busy,
        AppWindow::set_loading,
    ] {
        assert!(apply_current_result(&ui, 2, 2, &low));
        set_blocked(&ui, true);
        assert!(!ui.get_show_install_space_warning());
        assert!(ui.get_shown_install_hint().is_empty());
        assert!(apply_current_result(&ui, 2, 2, &low));
        set_blocked(&ui, false);
        assert!(!ui.get_install_space_low());
        assert!(ui.get_shown_install_hint().is_empty());
    }

    assert!(apply_current_result(&ui, 2, 2, &low));
    ui.set_game_title("".into());
    assert!(!ui.get_show_install_space_warning());
    assert!(apply_current_result(&ui, 2, 2, &low));
    assert!(!ui.get_install_space_low());
    assert!(ui.get_install_hint().is_empty());
}
#[test]
fn folder_recovery_uses_native_codes_not_localized_messages() {
    for code in [5, 19, 32, 33, 1314] {
        assert!(can_choose_folder_after(&std::io::Error::from_raw_os_error(
            code
        )));
    }
    assert!(!can_choose_folder_after(
        &std::io::Error::from_raw_os_error(112)
    ));
}
#[test]
fn write_probe_leaves_no_files_or_game_directory() {
    let t = tempfile::tempdir().unwrap();
    preflight(&t.path().join("missing/game")).unwrap();
    assert_eq!(std::fs::read_dir(t.path()).unwrap().count(), 0);
}
#[test]
fn estimate_never_calls_planned_work_network_traffic() {
    let (s, low) = describe(&DownloadEstimate {
        bytes_to_prepare: 1024,
        required_free_bytes: 2048,
        available_bytes: Some(1024),
    });
    assert!(low);
    assert!(s.contains("Недостаточно места"));
    assert!(!s.contains("скачать"));
}
