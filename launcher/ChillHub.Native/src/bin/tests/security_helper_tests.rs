use super::*;
use std::{
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
    },
    time::{Duration, Instant},
};

#[test]
fn security_window_handles_confirmation_and_results() {
    // Slint's integration platform is process-wide. Keep both UI scenarios
    // on the same initialized event-loop thread.
    i_slint_backend_testing::init_integration_test_with_system_time();
    request_explains_the_selected_provider_and_elevation();
    only_authenticated_handoff_resumes_the_confirmed_action();
    guide_and_confirmed_actions_use_distinct_effects();
    cancelled_elevation_keeps_the_request_available_for_retry();
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
    let result = Err("fixture: operation failed".into());
    let caption = completion_caption(&Ok(log.clone()), &result);
    finish(&ui, result, true, &caption);
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
    assert!(!temp.path().join("security-exclusions.json").exists());
}

fn guide_and_confirmed_actions_use_distinct_effects() {
    let temp = tempfile::tempdir().unwrap();
    let log = HelperLog::in_directory(temp.path(), HelperKind::Security).unwrap();
    let ui = AuxiliaryWindow::new().unwrap();
    let request = security::SecurityRequest {
        paths: vec![temp.path().join("one"), temp.path().join("two")],
        operation: SecurityOperation::Remove,
    };
    let expected = request.paths.clone();
    let calls = Arc::new(AtomicUsize::new(0));
    let worker_calls = calls.clone();
    present_request(&ui, &request, true, 0);
    bind_primary(
        &ui,
        request,
        true,
        Ok(log.clone()),
        move |request| {
            worker_calls.fetch_add(1, Ordering::SeqCst);
            assert_eq!(request.paths, expected);
            assert!(request.operation == SecurityOperation::Remove);
            Ok(vec![
                security::ExclusionResult {
                    path: request.paths[0].display().to_string(),
                    status: "removed".into(),
                    detail: "Удалено".into(),
                    added: false,
                },
                security::ExclusionResult {
                    path: request.paths[1].display().to_string(),
                    status: "unchanged".into(),
                    detail: "Записи нет".into(),
                    added: false,
                },
            ])
        },
        |provider| {
            assert_eq!(provider, 1);
            Err("Инструкция сейчас недоступна".into())
        },
    );
    resume_confirmed_handoff(&ui, false);
    ui.set_selected_provider(-1);
    ui.invoke_primary();
    ui.set_selected_provider(1);
    ui.invoke_primary();
    assert_eq!(ui.get_status(), "Инструкция сейчас недоступна");
    assert!(!ui.get_busy());
    assert_eq!(calls.load(Ordering::SeqCst), 0);
    ui.set_selected_provider(0);
    resume_confirmed_handoff(&ui, true);
    assert!(ui.get_busy());
    assert!(!ui.get_can_close());
    ui.invoke_primary();
    wait_for_worker(&ui, false);
    assert_eq!(calls.load(Ordering::SeqCst), 1);
    assert!(!ui.get_error());
    assert!(ui.get_can_close());
    assert_eq!(ui.get_primary_text(), "Закрыть");
    assert!(ui.get_details().contains("Удалено"));
    assert!(ui.get_details().contains("Записи нет"));
    assert!(ui.get_paths().is_empty());
    assert!(ui.get_status().contains(log.path().to_str().unwrap()));
    let contents = std::fs::read_to_string(log.path()).unwrap();
    for event in ["confirmed", "removed", "unchanged", "complete"] {
        assert!(contents.contains(&format!("\"event\":\"{event}\"")));
    }
}

fn cancelled_elevation_keeps_the_request_available_for_retry() {
    let temp = tempfile::tempdir().unwrap();
    let log = HelperLog::in_directory(temp.path(), HelperKind::Security).unwrap();
    let ui = AuxiliaryWindow::new().unwrap();
    let request = security::SecurityRequest {
        paths: vec![temp.path().join("game")],
        operation: SecurityOperation::Add,
    };
    let calls = Arc::new(AtomicUsize::new(0));
    let worker_calls = calls.clone();
    present_request(&ui, &request, false, 0);
    bind_primary(
        &ui,
        request,
        false,
        Ok(log.clone()),
        move |_| {
            if worker_calls.fetch_add(1, Ordering::SeqCst) == 0 {
                Err("Подтверждение отменено".into())
            } else {
                Ok(vec![])
            }
        },
        |_| -> Result<(), String> { panic!("the Defender action must not open a guide") },
    );
    ui.invoke_primary();
    wait_for_worker(&ui, false);
    assert!(ui.get_error());
    assert!(ui.get_can_close());
    assert!(ui.get_status().contains("Подтверждение отменено"));
    assert_eq!(ui.get_primary_text(), "Добавить исключения");
    assert!(!ui.get_paths().is_empty());
    ui.invoke_primary();
    wait_for_worker(&ui, true);
    assert_eq!(calls.load(Ordering::SeqCst), 2);
    assert!(!ui.get_error());
    let contents = std::fs::read_to_string(log.path()).unwrap();
    assert_eq!(contents.matches("elevation-requested").count(), 2);
    assert!(contents.contains("\"event\":\"complete\""));
    assert!(!temp.path().join("security-exclusions.json").exists());
}

fn wait_for_worker(ui: &AuxiliaryWindow, closes_on_success: bool) {
    use std::{cell::Cell, rc::Rc};

    let weak = ui.as_weak();
    let deadline = Instant::now() + Duration::from_secs(5);
    let timed_out = Rc::new(Cell::new(false));
    let result = timed_out.clone();
    let quit_posted = Cell::new(false);
    let observer = slint::Timer::default();
    observer.start(
        slint::TimerMode::Repeated,
        Duration::from_millis(10),
        move || {
            if quit_posted.get() {
                return;
            }
            let finished = !closes_on_success && weak.upgrade().is_some_and(|ui| !ui.get_busy());
            if finished || Instant::now() >= deadline {
                result.set(!finished);
                quit_posted.set(true);
                slint::quit_event_loop().unwrap();
            }
        },
    );
    // Component::run hides the last window after quitting, which can leave
    // another quit event queued for the next scenario on the same backend.
    slint::run_event_loop_until_quit().unwrap();
    observer.stop();
    assert!(
        !timed_out.get(),
        "the helper did not complete before the deadline"
    );
    assert!(
        !ui.get_busy(),
        "the event loop exited before the helper updated its window"
    );
}
