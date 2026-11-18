use super::*;
use chillhub_native::{config::ConfigStore, launch::LaunchAction, paths::AppPaths};

#[test]
fn tray_keeps_ready_launch_in_background_but_surfaces_install_or_stale_action() {
    assert!(!tray_requires_window(true, Some(&LaunchAction::Play)));
    assert!(tray_requires_window(false, Some(&LaunchAction::Play)));
    for action in [
        LaunchAction::InstallGame,
        LaunchAction::UpdateGame,
        LaunchAction::InstallMods,
        LaunchAction::UpdateMods,
        LaunchAction::RepairMods,
        LaunchAction::Unavailable,
    ] {
        assert!(tray_requires_window(true, Some(&action)));
    }
    assert!(tray_requires_window(true, None));
}

fn consume_requests(data: &Path, now: SystemTime) -> Vec<Vec<String>> {
    let Some(mut prepared) = prepare_requests(data, now, None) else {
        return Vec::new();
    };
    let commands = std::mem::take(&mut prepared.commands);
    prepared.remove().unwrap();
    commands
}
#[test]
fn tray_launch_never_changes_hidden_game_files() {
    assert!(can_launch_from_tray(
        true,
        true,
        false,
        false,
        Some(&LaunchAction::Play)
    ));
    for action in [
        LaunchAction::InstallGame,
        LaunchAction::UpdateGame,
        LaunchAction::InstallMods,
        LaunchAction::UpdateMods,
        LaunchAction::RepairMods,
    ] {
        assert!(can_launch_from_tray(
            true,
            true,
            false,
            false,
            Some(&action)
        ));
    }
    assert!(!can_launch_from_tray(
        true,
        true,
        false,
        false,
        Some(&LaunchAction::Unavailable)
    ));
    assert!(!can_launch_from_tray(
        false,
        true,
        false,
        false,
        Some(&LaunchAction::Play)
    ));
    assert!(!can_launch_from_tray(
        true,
        true,
        true,
        false,
        Some(&LaunchAction::Play)
    ));
    assert!(!can_launch_from_tray(
        true,
        true,
        false,
        true,
        Some(&LaunchAction::Play)
    ));
    assert!(!can_launch_from_tray(
        true,
        false,
        false,
        false,
        Some(&LaunchAction::Play)
    ));
}
#[test]
fn tray_labels_preserve_unicode_and_escape_mnemonics() {
    assert_eq!(play_menu_label("  ", "Играть"), "Игра не выбрана");
    assert_eq!(play_menu_label("A&B", "Играть"), "Играть: A&&B");
    assert_eq!(play_menu_label("PEAK", "Установить"), "Установить: PEAK");
    let mut text = [9u16; 5];
    fill_wide(&mut text, "аб🎮в");
    assert_eq!(&text[..4], &"аб🎮".encode_utf16().collect::<Vec<_>>());
    assert_eq!(text[4], 0);
    let mut short = [9u16; 4];
    fill_wide(&mut short, "аб🎮");
    assert_eq!(&short, &[1072, 1073, 0, 0]);
    assert_eq!(play_menu_label("\0\n", "Играть"), "Игра не выбрана");
    let title = format!("{}\0\0 🎮", "Я".repeat(90));
    assert_eq!(
        play_menu_label(&title, "Играть"),
        format!("Играть: {}…", "Я".repeat(90))
    );
}

#[test]
fn tray_commands_reject_disabled_and_missing_games() {
    let mut selection = MenuSelection {
        title: "Fixture".into(),
        game_id: "fixture".into(),
        action: "Установить".into(),
        enabled: false,
    };
    assert!(selection.clone().command(2).is_none());
    assert!(matches!(
        selection.clone().command(1),
        Some(TrayEvent::Show)
    ));
    assert!(matches!(
        selection.clone().command(3),
        Some(TrayEvent::Exit)
    ));
    assert!(selection.clone().command(0).is_none());
    selection.enabled = true;
    assert!(matches!(
        selection.clone().command(2),
        Some(TrayEvent::Play(game, action)) if game == "fixture" && action == "Установить"
    ));
    selection.game_id.clear();
    assert!(selection.command(2).is_none());
}

#[test]
fn tray_updates_deduplicate_tips_and_keep_only_accepted_notifications() {
    let shell = ShellBridge::new();
    shell.set_status("  5%  ", |_| panic!("no tray window"));
    assert!(!shell.notify("title", "text", true, |_| panic!("no tray window")));
    assert_eq!(shell.take_update().unwrap().0, "Chill Hub — 5%");
    shell.hwnd.store(17, Ordering::Release);
    shell.set_status("5%", |_| panic!("status did not change"));
    for status in ["6%", "  "] {
        let mut posted = false;
        shell.set_status(status, |hwnd| {
            assert_eq!(hwnd, 17);
            posted = true;
            true
        });
        assert!(posted);
    }
    for index in 0..10 {
        assert!(shell.notify(
            &format!("{index}{}", "я".repeat(130)),
            &"🎮".repeat(520),
            index == 9,
            |hwnd| hwnd == 17
        ));
    }
    assert!(!shell.notify("retry", "post failed", true, |_| false));
    let (tip, notifications) = shell.take_update().unwrap();
    assert_eq!(tip, "Chill Hub");
    assert_eq!(notifications.len(), 8);
    for (index, notification) in (2..=9).zip(notifications) {
        assert!(notification.title.starts_with(&index.to_string()));
        assert_eq!(notification.title.chars().count(), 120);
        assert_eq!(notification.text, "🎮".repeat(512));
        assert_eq!(notification.error, index == 9);
    }
    assert!(shell.take_update().unwrap().1.is_empty());
}
#[test]
fn tray_hint_persistence_preserves_preferences() {
    let temp = tempfile::tempdir().unwrap();
    let store = ConfigStore::new(AppPaths::isolated(temp.path()));
    store
        .update(|c| {
            c.speed_limit_mbps = 17;
            c.minimize_to_tray = true;
        })
        .unwrap();
    remember_tray_hint(&store).unwrap();
    let config = store.load().unwrap();
    assert!(config.tray_hint_shown);
    assert!(config.minimize_to_tray);
    assert_eq!(config.speed_limit_mbps, 17);
}
#[test]
fn closing_instance_hands_mutex_to_new_process() {
    let name = wide(&format!(
        "Local\\ChillHub.Test.Handoff.{}",
        uuid::Uuid::new_v4()
    ));
    let (held, ready) = mpsc::channel();
    let (release, released) = mpsc::channel();
    let holder = thread::spawn(move || unsafe {
        let mutex = CreateMutexW(ptr::null(), 1, name.as_ptr());
        assert!(!mutex.is_null());
        held.send(mutex as usize).unwrap();
        released.recv().unwrap();
        ReleaseMutex(mutex);
    });
    let mutex = ready.recv().unwrap() as HANDLE;
    let acknowledged = unsafe { CreateEventW(ptr::null(), 0, 0, ptr::null()) };
    assert!(!acknowledged.is_null());
    unsafe {
        SetEvent(acknowledged);
    }
    assert!(!await_activation_or_ownership(mutex, acknowledged, 100).unwrap());
    release.send(()).unwrap();
    assert!(await_activation_or_ownership(mutex, acknowledged, 5000).unwrap());
    unsafe {
        ReleaseMutex(mutex);
        CloseHandle(mutex);
        CloseHandle(acknowledged);
    }
    holder.join().unwrap();
}
#[test]
fn isolated_profile_aliases_share_instance_key_and_secondary_activation() {
    use std::os::windows::ffi::{OsStrExt, OsStringExt};
    let temp = tempfile::tempdir().unwrap();
    let directory = temp.path().join("Long isolated launcher profile");
    std::fs::create_dir_all(&directory).unwrap();
    let canonical = std::fs::canonicalize(&directory).unwrap();
    let input: Vec<u16> = directory.as_os_str().encode_wide().chain([0]).collect();
    let mut output = vec![0u16; 32768];
    let length = unsafe {
        windows_sys::Win32::Storage::FileSystem::GetShortPathNameW(
            input.as_ptr(),
            output.as_mut_ptr(),
            output.len() as u32,
        )
    } as usize;
    assert!(length > 0 && length < output.len());
    let short = PathBuf::from(std::ffi::OsString::from_wide(&output[..length]));
    assert_eq!(
        instance_suffix(&short, true).unwrap(),
        instance_suffix(&canonical, true).unwrap()
    );
    assert_eq!(
        instance_suffix(&short.join("missing/profile"), true).unwrap(),
        instance_suffix(&canonical.join("missing/profile"), true).unwrap()
    );
    assert_ne!(
        instance_suffix(&canonical.join("missing/other"), true).unwrap(),
        instance_suffix(&canonical.join("missing/profile"), true).unwrap()
    );
    assert!(!directory.join("missing").exists());
    assert!(instance_suffix(Path::new("relative-profile"), true).is_err());
    assert_eq!(instance_suffix(&directory, false).unwrap(), "");

    let (held, ready) = mpsc::channel();
    let (release, released) = mpsc::channel();
    let holder = thread::spawn(move || {
        let primary = Instance::acquire_for_profile(&short, &[], true).unwrap();
        assert!(primary.primary);
        held.send(()).unwrap();
        assert_eq!(
            unsafe { WaitForSingleObject(primary.events.requested.as_raw_handle(), 5000) },
            WAIT_OBJECT_0
        );
        unsafe { SetEvent(primary.events.acknowledged.as_raw_handle()) };
        released.recv().unwrap();
        drop(primary);
    });
    ready.recv().unwrap();
    let secondary = Instance::acquire_for_profile(&canonical, &[], true).unwrap();
    assert!(!secondary.primary);
    drop(secondary);
    release.send(()).unwrap();
    holder.join().unwrap();
}
#[test]
fn takeover_does_not_wait_for_old_io_and_drains_preserved_shortcuts_at_startup() {
    let temp = tempfile::tempdir().unwrap();
    let data = temp.path().join("profile");
    let mut primary = Instance::acquire_for_profile(&data, &[], true).unwrap();
    assert!(primary.primary);
    let earlier = vec!["--game".into(), "peak".into()];
    let preserved = publish_request(&data, &earlier).unwrap();
    let (dispatched, receive) = mpsc::channel();
    let (unblock, blocked) = mpsc::channel();
    primary
        .listen_with(move |control, requests, accepted| {
            if dispatched.send((control.clone(), requests)).is_err() {
                return false;
            }
            // Hold the old scan and its pinned file while the new UI starts.
            blocked.recv_timeout(Duration::from_secs(5)).is_ok() && accepted.send(false).is_ok()
        })
        .unwrap();
    let events = primary.events.clone();
    let (old_control, old_commands) = receive.recv_timeout(Duration::from_secs(5)).unwrap();
    assert!(old_control.is_active());
    assert_eq!(old_commands.as_slice(), std::slice::from_ref(&earlier));
    assert!(preserved.is_file());

    let (ready, promoted) = mpsc::channel();
    let (planned, delivered) = mpsc::channel();
    let (close, keep_alive) = mpsc::channel();
    let secondary_data = data.clone();
    let secondary = thread::spawn(move || {
        let args = vec!["--game".into(), "other".into()];
        let mut instance = Instance::acquire_for_profile(&secondary_data, &args, true).unwrap();
        assert!(
            instance.primary,
            "shutdown releases launcher ownership before the old scan completes"
        );
        assert!(instance.promoted_request.is_some());
        instance
            .listen_with(move |_, requests, accepted| {
                planned.send(requests).is_ok() && accepted.send(true).is_ok()
            })
            .unwrap();
        ready.send(()).unwrap();
        keep_alive.recv_timeout(Duration::from_secs(5)).unwrap();
    });

    // Model a coalesced request event already consumed by the old listener.
    assert_eq!(
        unsafe { WaitForSingleObject(events.requested.as_raw_handle(), 5000) },
        WAIT_OBJECT_0
    );
    drop(primary);
    assert!(!old_control.is_active());
    assert!(!old_control.publish(&events, true, true));
    promoted.recv_timeout(Duration::from_secs(5)).unwrap();
    assert!(
        delivered.try_recv().is_err(),
        "the old scan still pins the file"
    );
    unblock.send(()).unwrap();
    assert_eq!(
        delivered.recv_timeout(Duration::from_secs(5)).unwrap(),
        [earlier],
        "the startup scan must deliver the retained request once without delivering its own CLI arguments",
    );
    assert_eq!(
        unsafe { WaitForSingleObject(events.acknowledged.as_raw_handle(), 0) },
        WAIT_TIMEOUT,
        "the startup scan must not acknowledge an unrelated launch"
    );
    // An empty requested scan runs only after startup's disk cleanup.
    unsafe { SetEvent(events.requested.as_raw_handle()) };
    assert!(
        delivered
            .recv_timeout(Duration::from_secs(5))
            .unwrap()
            .is_empty()
    );
    assert_eq!(
        unsafe { WaitForSingleObject(events.acknowledged.as_raw_handle(), 5000) },
        WAIT_OBJECT_0
    );
    assert!(
        std::fs::read_dir(data.join("requests"))
            .unwrap()
            .next()
            .is_none()
    );
    close.send(()).unwrap();
    secondary.join().unwrap();
}

#[test]
fn activation_wait_distinguishes_timeout_invalid_handles_and_abandoned_owner() {
    assert!(await_activation_or_ownership(ptr::null_mut(), ptr::null_mut(), 0).is_err());
    let mutex = unsafe { CreateMutexW(ptr::null(), 0, ptr::null()) } as usize;
    let ack = unsafe { CreateEventW(ptr::null(), 0, 0, ptr::null()) };
    assert_ne!(mutex, 0);
    assert!(!ack.is_null());
    let (held, ready) = mpsc::channel();
    let (release, released) = mpsc::channel();
    let holder = thread::spawn(move || {
        assert_eq!(
            unsafe { WaitForSingleObject(mutex as HANDLE, 0) },
            WAIT_OBJECT_0
        );
        held.send(()).unwrap();
        released.recv().unwrap();
        // Windows abandons the mutex when a thread exits without releasing it.
    });
    ready.recv_timeout(Duration::from_secs(5)).unwrap();
    assert!(!await_activation_or_ownership(mutex as HANDLE, ack, 0).unwrap());
    release.send(()).unwrap();
    holder.join().unwrap();
    assert!(await_activation_or_ownership(mutex as HANDLE, ack, 0).unwrap());
    unsafe {
        ReleaseMutex(mutex as HANDLE);
        CloseHandle(mutex as HANDLE);
        CloseHandle(ack);
    }
}
#[test]
fn requests_expire_and_are_consumed_once() {
    let temp = tempfile::tempdir().unwrap();
    let args = vec!["--game".into(), "peak".into()];
    assert!(ShortcutRequest::parse_args(&args).unwrap().is_some());
    publish_request(temp.path(), &args).unwrap();
    let directory = temp.path().join("requests");
    let stale = directory.join("old.json");
    std::fs::write(&stale, serde_json::to_vec(&args).unwrap()).unwrap();
    let now = SystemTime::now();
    std::fs::File::options()
        .write(true)
        .open(&stale)
        .unwrap()
        .set_times(std::fs::FileTimes::new().set_modified(now - Duration::from_secs(121)))
        .unwrap();
    std::fs::write(
        directory.join("oversize.json"),
        vec![b' '; REQUEST_LIMIT as usize + 1],
    )
    .unwrap();
    std::fs::write(directory.join("bad.json"), b"not json").unwrap();
    assert_eq!(consume_requests(temp.path(), now), vec![args]);
    assert!(!stale.exists());
    assert!(consume_requests(temp.path(), now).is_empty());
    assert!(fresh_request(now + Duration::from_secs(120), now));
    assert!(!fresh_request(now + Duration::from_secs(121), now));
}

#[test]
fn shortcut_queue_rejects_unsafe_and_invalid_entries() {
    let temp = tempfile::tempdir().unwrap();
    let data = temp.path().join("profile");
    let oversize = vec!["--game".into(), "a".repeat(REQUEST_LIMIT as usize)];
    assert!(
        publish_request(&data, &oversize)
            .unwrap_err()
            .contains("слишком большой")
    );
    assert!(!data.exists());
    assert!(consume_requests(&data, SystemTime::now()).is_empty());
    let directory = data.join("requests");
    std::fs::create_dir_all(&directory).unwrap();
    let ordinary = ["readme.txt", "partial.tmp"];
    let malformed = [
        ("empty.json", b"[]".as_slice()),
        ("object.json", br#"{"game":"peak"}"#.as_slice()),
        ("traversal.json", br#"["--game","../peak"]"#.as_slice()),
        (
            "unknown-target.json",
            br#"["chillhub://game/peak?target=bogus"]"#.as_slice(),
        ),
    ];
    for name in ordinary {
        std::fs::write(directory.join(name), b"keep").unwrap();
    }
    for (name, bytes) in malformed {
        std::fs::write(directory.join(name), bytes).unwrap();
    }
    std::fs::create_dir(directory.join("folder.json")).unwrap();
    assert!(consume_requests(&data, SystemTime::now()).is_empty());
    assert!(directory.join("folder.json").is_dir());
    for name in ordinary {
        assert_eq!(std::fs::read(directory.join(name)).unwrap(), b"keep");
    }
    for (name, _) in malformed {
        assert!(!directory.join(name).exists());
    }
}

#[test]
fn unacknowledged_shortcut_is_preserved_and_cannot_be_swapped_while_prepared() {
    let temp = tempfile::tempdir().unwrap();
    let args = vec!["--game".into(), "peak".into()];
    let request = publish_request(temp.path(), &args).unwrap();
    let replacement = request.with_file_name("replacement.json");
    let prepared = prepare_requests(temp.path(), SystemTime::now(), None).unwrap();
    assert_eq!(prepared.commands.as_slice(), std::slice::from_ref(&args));
    assert!(std::fs::rename(&request, &replacement).is_err());
    assert!(std::fs::remove_file(&request).is_err());
    drop(prepared);
    assert!(!replacement.exists());
    assert!(request.is_file());
    assert_eq!(consume_requests(temp.path(), SystemTime::now()), [args]);
    assert!(!request.exists());
}

#[test]
fn shortcut_batch_orders_by_arrival_then_filename() {
    let temp = tempfile::tempdir().unwrap();
    let directory = temp.path().join("requests");
    std::fs::create_dir(&directory).unwrap();
    let now = SystemTime::now();
    for (filename, game, age) in [
        ("z-old.json", "old", 90),
        ("b-tie.json", "tie-second", 60),
        ("c-new.json", "new", 30),
        ("a-tie.json", "tie-first", 60),
    ] {
        let path = directory.join(filename);
        std::fs::write(&path, serde_json::to_vec(&["--game", game]).unwrap()).unwrap();
        File::options()
            .write(true)
            .open(path)
            .unwrap()
            .set_times(std::fs::FileTimes::new().set_modified(now - Duration::from_secs(age)))
            .unwrap();
    }
    let commands = consume_requests(temp.path(), now);
    let ids = commands
        .iter()
        .map(|args| args[1].as_str())
        .collect::<Vec<_>>();
    assert_eq!(ids, ["old", "tie-first", "tie-second", "new"]);
    assert!(std::fs::read_dir(directory).unwrap().next().is_none());
}

fn seed_capped_shortcut_batch(data: &Path, now: SystemTime) -> PathBuf {
    let directory = data.join("requests");
    std::fs::create_dir(&directory).unwrap();
    for index in 0..33 {
        let game = format!("game-{index:02}");
        let path = directory.join(format!("{index:02}.json"));
        std::fs::write(&path, serde_json::to_vec(&["--game", &game]).unwrap()).unwrap();
        File::options()
            .write(true)
            .open(path)
            .unwrap()
            .set_times(std::fs::FileTimes::new().set_modified(now))
            .unwrap();
    }
    directory
}

#[test]
fn capped_shortcut_batch_indicates_when_to_wake_for_remaining_requests() {
    let temp = tempfile::tempdir().unwrap();
    let now = SystemTime::now();
    let directory = seed_capped_shortcut_batch(temp.path(), now);
    let first = prepare_requests(temp.path(), now, None).unwrap();
    assert!(first.has_more);
    assert_eq!(first.commands.len(), 32);
    assert_eq!(first.commands.last().unwrap()[1], "game-31");
    assert!(finish_shortcut_batch(first, temp.path()));
    let last = prepare_requests(temp.path(), now, None).unwrap();
    assert!(!last.has_more);
    assert_eq!(last.commands.len(), 1);
    assert_eq!(last.commands[0], ["--game", "game-32"]);
    assert!(!finish_shortcut_batch(last, temp.path()));
    assert!(std::fs::read_dir(directory).unwrap().next().is_none());
}

#[test]
fn shortcut_listener_rearms_itself_when_the_startup_batch_reaches_its_limit() {
    let temp = tempfile::tempdir().unwrap();
    let directory = seed_capped_shortcut_batch(temp.path(), SystemTime::now());
    let mut primary = Instance::acquire_for_profile(temp.path(), &[], true).unwrap();
    assert!(primary.primary);
    let (dispatch, batches) = mpsc::channel();
    primary
        .listen_with(move |_, requests, accepted| {
            dispatch.send(requests).is_ok() && accepted.send(true).is_ok()
        })
        .unwrap();
    let first = batches.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(first.len(), 32);
    assert_eq!(first[0], ["--game", "game-00"]);
    assert_eq!(first[31], ["--game", "game-31"]);
    let remainder = batches.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(remainder, [["--game", "game-32"]]);
    assert_eq!(
        unsafe { WaitForSingleObject(primary.events.acknowledged.as_raw_handle(), 5000) },
        WAIT_OBJECT_0
    );
    assert!(std::fs::read_dir(directory).unwrap().next().is_none());
}

pub(crate) fn activation_shows_a_hidden_window_before_acknowledging_its_request() {
    let temp = tempfile::tempdir().unwrap();
    let mut primary = Instance::acquire_for_profile(temp.path(), &[], true).unwrap();
    assert!(primary.primary);
    let ui = AppWindow::new().unwrap();
    assert!(!ui.window().is_visible());
    primary.listen(ui.as_weak()).unwrap();
    let request = publish_request(temp.path(), &["--game".into(), "peak".into()]).unwrap();
    unsafe { SetEvent(primary.events.requested.as_raw_handle()) };

    let acknowledged = std::rc::Rc::new(std::cell::Cell::new(None));
    let received = acknowledged.clone();
    let event = primary.events.clone();
    let deadline = std::time::Instant::now() + Duration::from_secs(5);
    let timer = slint::Timer::default();
    timer.start(
        slint::TimerMode::Repeated,
        Duration::from_millis(10),
        move || {
            if received.get().is_some() {
                return;
            }
            let signaled = unsafe { WaitForSingleObject(event.acknowledged.as_raw_handle(), 0) }
                == WAIT_OBJECT_0;
            if signaled || std::time::Instant::now() >= deadline {
                received.set(Some(signaled));
                let _ = slint::quit_event_loop();
            }
        },
    );
    slint::run_event_loop_until_quit().unwrap();
    timer.stop();
    assert!(
        acknowledged.get() == Some(true),
        "the desktop listener did not acknowledge activation"
    );
    assert!(ui.window().is_visible());
    assert!(!request.exists());
    ui.hide().unwrap();
}

#[test]
fn failing_to_show_window_preserves_shortcuts_for_the_next_activation() {
    let temp = tempfile::tempdir().unwrap();
    let args = vec!["--game".into(), "peak".into()];
    let request = publish_request(temp.path(), &args).unwrap();
    let now = SystemTime::now();
    let unaccepted = prepare_requests(temp.path(), now, None).unwrap();
    let (accept, receipt) = mpsc::channel();
    deliver_activation(|| false, || panic!("the window failed to open"), accept);
    assert!(!receipt.try_recv().unwrap());
    drop(unaccepted);
    assert!(request.exists());

    let accepted = prepare_requests(temp.path(), now, None).unwrap();
    let mut delivered = Vec::new();
    let (accept, receipt) = mpsc::channel();
    deliver_activation(
        || true,
        || delivered.extend(accepted.commands.iter().cloned()),
        accept,
    );
    assert!(receipt.try_recv().unwrap());
    assert_eq!(delivered, [args]);
    accepted.remove().unwrap();
    assert!(!request.exists());
}

#[test]
fn cleanup_denied_for_a_read_only_handle_does_not_rearm_the_batch() {
    let temp = tempfile::tempdir().unwrap();
    let now = SystemTime::now();
    let directory = seed_capped_shortcut_batch(temp.path(), now);
    let mut prepared = prepare_requests(temp.path(), now, None).unwrap();
    assert!(prepared.has_more);
    assert_eq!(prepared.commands[0][1], "game-00");
    let retained = directory.join("00.json");
    // Model a denied delete on an accepted file with a real Windows handle
    // opened without DELETE access, independent of filesystem permissions.
    prepared.files[0] = File::open(&retained).unwrap();
    assert!(!finish_shortcut_batch(prepared, temp.path()));
    assert!(retained.is_file());
    assert!(directory.join("32.json").is_file());
    assert_eq!(std::fs::read_dir(directory).unwrap().count(), 2);
}
