use super::*;
use chillhub_native::{config::ConfigStore, launch::LaunchAction, paths::AppPaths};
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
            unsafe { WaitForSingleObject(primary.event as HANDLE, 5000) },
            WAIT_OBJECT_0
        );
        unsafe { SetEvent(primary.acknowledged as HANDLE) };
        released.recv().unwrap();
        let event = primary.event as HANDLE;
        let acknowledged = primary.acknowledged as HANDLE;
        drop(primary);
        // No listener owns these handles in this GUI-free fixture.
        unsafe {
            CloseHandle(event);
            CloseHandle(acknowledged);
        }
    });
    ready.recv().unwrap();
    let secondary = Instance::acquire_for_profile(&canonical, &[], true).unwrap();
    assert!(!secondary.primary);
    drop(secondary);
    release.send(()).unwrap();
    holder.join().unwrap();
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
