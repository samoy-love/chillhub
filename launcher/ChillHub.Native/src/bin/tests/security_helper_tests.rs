use super::*;
#[test]
fn security_window_handles_confirmation_and_results() {
    // Slint's integration platform is process-wide. Keep both UI scenarios
    // on the same initialized event-loop thread.
    i_slint_backend_testing::init_integration_test_with_system_time();
    request_explains_the_selected_provider_and_elevation();
    only_authenticated_handoff_resumes_the_confirmed_action();
    security_results_keep_errors_visible_and_allow_close_without_changes();
}

fn request_explains_the_selected_provider_and_elevation() {
    let ui = AuxiliaryWindow::new().unwrap();
    let mut request = security::SecurityRequest {
        paths: vec![std::path::PathBuf::from(r"D:\Games\Fixture")],
        operation: SecurityOperation::Add,
    };
    present_request(&ui, &request, false, 0);
    assert!(ui.get_status().contains("администратора"));
    assert_eq!(ui.get_primary_text(), "Добавить исключения");
    assert!(ui.get_details().contains("доверяете"));
    assert_eq!(ui.get_paths(), r"D:\Games\Fixture");

    request.operation = SecurityOperation::Remove;
    present_request(&ui, &request, true, 0);
    assert!(ui.get_status().contains("Нажмите кнопку"));
    assert_eq!(ui.get_primary_text(), "Удалить исключения");
    assert!(ui.get_details().contains("только исключения Chill Hub"));

    present_request(&ui, &request, true, 1);
    assert!(ui.get_status().contains("по инструкции"));
    assert_eq!(ui.get_primary_text(), "Открыть инструкцию");
    assert!(ui.get_secondary_text().is_empty());
}
fn only_authenticated_handoff_resumes_the_confirmed_action() {
    use std::{cell::Cell, rc::Rc};
    let ui = AuxiliaryWindow::new().unwrap();
    let calls = Rc::new(Cell::new(0));
    let count = calls.clone();
    let weak = ui.as_weak();
    ui.on_primary(move || {
        count.set(count.get() + 1);
        weak.upgrade().unwrap().set_busy(true);
    });
    resume_confirmed_handoff(&ui, false);
    assert_eq!(calls.get(), 0, "opening a helper cannot change protection");
    ui.set_primary_enabled(false);
    resume_confirmed_handoff(&ui, true);
    assert_eq!(calls.get(), 0, "invalid requests stay inert");
    ui.set_primary_enabled(true);
    resume_confirmed_handoff(&ui, true);
    resume_confirmed_handoff(&ui, true);
    assert_eq!(
        calls.get(),
        1,
        "confirmed handoff starts once without a second click"
    );
}
fn security_results_keep_errors_visible_and_allow_close_without_changes() {
    let temp = tempfile::tempdir().unwrap();
    let log = HelperLog::in_directory(temp.path(), HelperKind::Security).unwrap();
    let ui = AuxiliaryWindow::new().unwrap();
    ui.set_busy(true);
    ui.set_can_close(false);
    ui.set_paths("D:\\Games\\Fixture".into());
    ui.set_provider_visible(true);
    finish(
        &ui,
        Err("fixture: operation failed".into()),
        true,
        &Ok(log.clone()),
    );
    assert!(!ui.get_busy());
    assert!(ui.get_can_close());
    assert!(ui.get_primary_enabled());
    assert_eq!(ui.get_primary_text(), "Закрыть");
    assert!(ui.get_paths().is_empty());
    assert!(
        !ui.get_provider_visible(),
        "completed operation cannot select another provider"
    );
    assert!(ui.get_details().contains("operation failed"));
    assert!(
        std::fs::read_to_string(log.path())
            .unwrap()
            .contains("operation failed")
    );
    finish(
        &ui,
        Err("fixture: elevation cancelled".into()),
        false,
        &Ok(log),
    );
    assert!(ui.get_status().contains("elevation cancelled"));
    assert!(!temp.path().join("security-exclusions.json").exists());
}
