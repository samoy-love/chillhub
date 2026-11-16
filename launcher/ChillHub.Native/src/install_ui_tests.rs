use super::*;
#[test]
fn install_estimate_replaces_stale_disk_value_and_clears_it_when_unknown() {
    slint::platform::set_platform(Box::new(i_slint_backend_testing::TestingBackend::new(
        i_slint_backend_testing::TestingBackendOptions {
            mock_time: true,
            threading: false,
            renderer_name: Some("software".into()),
        },
    )))
    .expect("headless test platform only");
    let ui = AppWindow::new().unwrap();
    ui.set_disk_label("Свободно 52.8 ГБ".into());
    ui.set_install_hint("Свободно 52.8 ГБ".into());
    apply_estimate(
        &ui,
        &chillhub_native::service::DownloadEstimate {
            bytes_to_prepare: 1024,
            required_free_bytes: 2048,
            available_bytes: Some(20 * 1024 * 1024 * 1024),
        },
    );
    assert_eq!(ui.get_disk_label(), "Свободно 20.0 ГБ");
    assert!(ui.get_install_hint().contains("свободно 20.0 ГБ"));
    assert!(!ui.get_disk_label().contains("52.8"));
    assert!(!ui.get_install_hint().contains("52.8"));

    apply_estimate(
        &ui,
        &chillhub_native::service::DownloadEstimate {
            bytes_to_prepare: 0,
            required_free_bytes: 0,
            available_bytes: None,
        },
    );
    assert_eq!(ui.get_disk_label(), "Не удалось определить свободное место");
    assert!(!ui.get_install_hint().contains("52.8"));
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
    let s = describe(&chillhub_native::service::DownloadEstimate {
        bytes_to_prepare: 1024,
        required_free_bytes: 2048,
        available_bytes: Some(1024),
    });
    assert!(s.contains("Недостаточно места"));
    assert!(!s.contains("скачать"));
}
