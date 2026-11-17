use super::*;
use std::time::{Duration, Instant};

#[test]
fn update_failure_unlocks_window_and_points_to_actual_log() {
    i_slint_backend_testing::init_integration_test_with_system_time();
    invalid_invocation_completes_with_visible_error();
    validated_work_reports_apply_progress_and_recovery_failure();
    worker_persists_result_after_its_window_disappears();
    let temp = tempfile::tempdir().unwrap();
    let log = HelperLog::in_directory(temp.path(), HelperKind::Updater).unwrap();
    let ui = AuxiliaryWindow::new().unwrap();
    ui.set_busy(true);
    ui.set_can_close(false);
    let result = Err("fixture: invalid update job".into());
    let caption = helper_runtime::record_result(&Ok(log.clone()), &result);
    finish(&ui, result, &caption);
    assert!(!ui.get_busy());
    assert!(ui.get_can_close());
    assert!(ui.get_details().contains("invalid update job"));
    assert!(ui.get_status().contains(log.path().to_str().unwrap()));
    assert!(
        std::fs::read_to_string(log.path())
            .unwrap()
            .contains("invalid update job")
    );
    let result = Err("fixture error".into());
    let caption = helper_runtime::record_result(&Err("fixture log unavailable".into()), &result);
    finish(&ui, result, &caption);
    assert!(ui.get_status().contains("Журнал недоступен"));
}

fn invalid_invocation_completes_with_visible_error() {
    let temp = tempfile::tempdir().unwrap();
    let log = HelperLog::in_directory(temp.path(), HelperKind::Updater).unwrap();
    let ui = AuxiliaryWindow::new().unwrap();
    start(
        &ui,
        Ok(log.clone()),
        || update::job_from_args(&["--invalid".into()]),
        |_, _| panic!("invalid jobs cannot reach the worker"),
    );
    assert!(ui.get_busy());
    assert!(!ui.get_can_close());
    wait_for_worker(&ui, false);
    assert!(!ui.get_busy());
    assert!(ui.get_error());
    assert!(ui.get_can_close());
    assert!(ui.get_details().contains("запускается из лаунчера"));
    assert!(ui.get_status().contains(log.path().to_str().unwrap()));
}

fn fixture_job(root: &std::path::Path, mode: update::UpdateMode) -> update::UpdateJob {
    let paths = update::UpdatePaths {
        install: root.join("install"),
        config: root.join("config"),
        games: root.join("games"),
        staging: root.join("staging"),
    };
    update::UpdateJob {
        parent_pid: 1,
        parent_executable: paths.install.join(update::LAUNCHER_NAME),
        restart_args: vec!["--game=fixture".into()],
        token: uuid::Uuid::new_v4().to_string(),
        mode,
        staged: update::StagedUpdate {
            session: paths.staging.join("session"),
            paths,
            current_version: "1.5.0".into(),
            version: "1.5.1".into(),
            files: vec![],
            delete_paths: vec![],
            empty_dirs: vec![],
        },
    }
}

fn validated_work_reports_apply_progress_and_recovery_failure() {
    let temp = tempfile::tempdir().unwrap();
    for mode in [update::UpdateMode::Apply, update::UpdateMode::Recovery] {
        let log = HelperLog::in_directory(temp.path(), HelperKind::Updater).unwrap();
        let ui = AuxiliaryWindow::new().unwrap();
        let job = fixture_job(temp.path(), mode);
        let recovery = mode == update::UpdateMode::Recovery;
        let install_path = job.staged.paths.install.display().to_string();
        start(
            &ui,
            Ok(log.clone()),
            move || Ok(job),
            move |job, progress| {
                assert_eq!(job.mode, mode);
                assert_eq!(job.restart_args, ["--game=fixture"]);
                progress("Копируем файлы", 0.1);
                progress("Копируем файлы", 0.6);
                progress("Проверяем файлы", 0.9);
                if recovery {
                    Err("резервная копия повреждена".into())
                } else {
                    Ok(())
                }
            },
        );
        wait_for_worker(&ui, !recovery);
        assert_eq!(ui.get_error(), recovery);
        assert!(ui.get_can_close());
        assert!(!ui.get_progress_visible());
        assert_eq!(ui.get_progress(), 0.9);
        let contents = std::fs::read_to_string(log.path()).unwrap();
        assert_eq!(contents.matches("Копируем файлы").count(), 1);
        assert_eq!(contents.matches("Проверяем файлы").count(), 1);
        if recovery {
            assert!(ui.get_subtitle().contains("предыдущей версии"));
            assert!(ui.get_details().contains("резервная копия повреждена"));
            assert!(contents.contains("Восстанавливаем версию 1.5.0"));
            assert!(contents.contains("\"event\":\"error\""));
        } else {
            assert_eq!(ui.get_paths(), install_path.as_str());
            assert!(ui.get_details().contains("1.5.0 → 1.5.1"));
            assert!(ui.get_status().contains("Открываем лаунчер"));
            assert!(contents.contains("\"event\":\"complete\""));
        }
    }
}

fn worker_persists_result_after_its_window_disappears() {
    let temp = tempfile::tempdir().unwrap();
    let log = HelperLog::in_directory(temp.path(), HelperKind::Updater).unwrap();
    let ui = AuxiliaryWindow::new().unwrap();
    let (send, proceed) = std::sync::mpsc::channel();
    start(
        &ui,
        Ok(log.clone()),
        move || {
            proceed.recv_timeout(Duration::from_secs(5)).unwrap();
            Err("окно уже закрыто".into())
        },
        |_, _| panic!("no job to execute"),
    );
    drop(ui);
    send.send(()).unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        let contents = std::fs::read_to_string(log.path()).unwrap();
        if contents.split_inclusive('\n').any(|line| {
            line.ends_with('\n')
                && serde_json::from_str::<serde_json::Value>(line).is_ok_and(|entry| {
                    entry["event"] == "error" && entry["message"] == "окно уже закрыто"
                })
        }) {
            break;
        }
        assert!(
            Instant::now() < deadline,
            "missing outcome from detached worker"
        );
        std::thread::sleep(Duration::from_millis(10));
    }
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
