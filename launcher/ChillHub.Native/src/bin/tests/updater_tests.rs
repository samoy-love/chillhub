use super::*;
#[test]
fn update_failure_unlocks_window_and_points_to_actual_log() {
    i_slint_backend_testing::init_integration_test_with_system_time();
    invalid_invocation_completes_with_visible_error();
    let temp = tempfile::tempdir().unwrap();
    let log = HelperLog::in_directory(temp.path(), HelperKind::Updater).unwrap();
    let ui = AuxiliaryWindow::new().unwrap();
    ui.set_busy(true);
    ui.set_can_close(false);
    finish(
        &ui,
        Err("fixture: invalid update job".into()),
        &Ok(log.clone()),
    );
    assert!(!ui.get_busy());
    assert!(ui.get_can_close());
    assert!(ui.get_details().contains("invalid update job"));
    assert!(ui.get_status().contains(log.path().to_str().unwrap()));
    assert!(
        std::fs::read_to_string(log.path())
            .unwrap()
            .contains("invalid update job")
    );
    finish(
        &ui,
        Err("fixture error".into()),
        &Err("fixture log unavailable".into()),
    );
    assert!(ui.get_status().contains("Журнал недоступен"));
    finish(&ui, Ok(()), &Ok(log));
    assert!(!ui.get_error());
    assert!(!ui.get_progress_visible());
    assert!(ui.get_status().contains("Открываем лаунчер"));
}

fn invalid_invocation_completes_with_visible_error() {
    use std::time::{Duration, Instant};
    let temp = tempfile::tempdir().unwrap();
    let log = HelperLog::in_directory(temp.path(), HelperKind::Updater).unwrap();
    let ui = AuxiliaryWindow::new().unwrap();
    start(&ui, vec!["--invalid".into()], Ok(log.clone()));
    assert!(ui.get_busy());
    assert!(!ui.get_can_close());
    let weak = ui.as_weak();
    let timeout = Instant::now() + Duration::from_secs(5);
    let observer = slint::Timer::default();
    observer.start(
        slint::TimerMode::Repeated,
        Duration::from_millis(10),
        move || {
            if weak.upgrade().is_none_or(|ui| !ui.get_busy()) || Instant::now() >= timeout {
                let _ = slint::quit_event_loop();
            }
        },
    );
    ui.run().unwrap();
    assert!(!ui.get_busy());
    assert!(ui.get_error());
    assert!(ui.get_can_close());
    assert!(ui.get_details().contains("запускается из лаунчера"));
    assert!(ui.get_status().contains(log.path().to_str().unwrap()));
}
