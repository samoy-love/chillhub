use std::os::windows::io::FromRawHandle;

#[test]
fn mouse_input_accepts_only_exact_chillhub_messages() {
    assert_eq!(
        parse_mouse_message(&json!({
            "event": "client-message",
            "args": ["chillhub", "left-click"]
        })),
        Some(MouseMessage::LeftClick)
    );
    assert_eq!(
        parse_mouse_message(&json!({
            "event": "client-message",
            "args": ["chillhub", "double-click"]
        })),
        Some(MouseMessage::DoubleClick)
    );
    for message in [
        json!({"event":"property-change","args":["chillhub","left-click"]}),
        json!({"event":"client-message","args":["other","left-click"]}),
        json!({"event":"client-message","args":["chillhub","left-click","extra"]}),
        json!({"event":"client-message","args":["chillhub","unknown"]}),
        json!({"event":"client-message","args":["chillhub",1]}),
    ] {
        assert_eq!(parse_mouse_message(&message), None);
    }
}

#[test]
fn single_click_waits_and_double_click_cancels_pause() {
    let start = Instant::now();
    let mut clicks = ClickCoalescer::default();
    assert_eq!(clicks.push(MouseMessage::LeftClick, start), None);
    assert_eq!(
        clicks.take_expired(start + Duration::from_millis(249)),
        None
    );
    assert_eq!(
        clicks.take_expired(start + Duration::from_millis(250)),
        Some(VideoAction::TogglePause)
    );

    assert_eq!(
        clicks.push(MouseMessage::LeftClick, start + Duration::from_secs(1)),
        None
    );
    assert_eq!(
        clicks.push(
            MouseMessage::DoubleClick,
            start + Duration::from_millis(1100)
        ),
        Some(VideoAction::ToggleFullscreen)
    );
    assert_eq!(
        clicks.push(MouseMessage::LeftClick, start + Duration::from_millis(1150)),
        None
    );
    assert_eq!(
        clicks.take_expired(start + Duration::from_millis(1400)),
        None
    );
    assert_eq!(
        clicks.push(MouseMessage::LeftClick, start + Duration::from_millis(1401)),
        None
    );
    assert_eq!(
        clicks.take_expired(start + Duration::from_millis(1651)),
        Some(VideoAction::TogglePause)
    );
}

#[test]
fn scoped_input_config_contains_only_the_two_video_actions() {
    assert_eq!(
        INPUT_CONF,
        "MBTN_LEFT script-message chillhub left-click\nMBTN_LEFT_DBL script-message chillhub double-click\n"
    );
    assert!(!INPUT_CONF.contains("include"));
}

#[test]
fn take_actions_drains_the_ui_queue() {
    let dir = tempfile::tempdir().unwrap();
    let host = Host::new();
    let mut player = fixture(&host, dir.path());
    player
        .actions
        .lock()
        .unwrap()
        .extend([VideoAction::TogglePause, VideoAction::ToggleFullscreen]);
    assert_eq!(
        player.take_actions(),
        vec![VideoAction::TogglePause, VideoAction::ToggleFullscreen]
    );
    assert!(player.take_actions().is_empty());
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn CreateNamedPipeW(
        name: *const u16,
        access: u32,
        mode: u32,
        instances: u32,
        output_size: u32,
        input_size: u32,
        timeout: u32,
        security: *mut c_void,
    ) -> *mut c_void;
    fn ConnectNamedPipe(pipe: *mut c_void, overlapped: *mut c_void) -> i32;
    fn GetCurrentProcess() -> *mut c_void;
}

#[test]
fn connected_player_receives_immediate_load_failure() {
    let dir = tempfile::tempdir().unwrap();
    let name = format!(
        r"\\.\pipe\chillhub-video-test-{}-{}",
        std::process::id(),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    );
    let wide: Vec<u16> = OsStr::new(&name).encode_wide().chain(Some(0)).collect();
    // Nonblocking server keeps a broken client bounded by the test deadline.
    let handle = unsafe {
        CreateNamedPipeW(
            wide.as_ptr(),
            3,
            1,
            1,
            65536,
            65536,
            0,
            std::ptr::null_mut(),
        )
    };
    assert_ne!(handle as isize, -1);
    let mut server = unsafe { File::from_raw_handle(handle) };
    let state = Arc::new(Mutex::new(VideoStatus::default()));
    let actions = Arc::new(Mutex::new(VecDeque::new()));
    let stop = Arc::new(AtomicBool::new(false));
    let worker_state = Arc::clone(&state);
    let worker_actions = Arc::clone(&actions);
    let worker_stop = Arc::clone(&stop);
    let (sender, receiver) = mpsc::channel();
    let mut diagnostics = File::create(dir.path().join("video.jsonl")).unwrap();
    let address = "https://example.org/ролик.mp4?quality=1080";
    let worker = thread::spawn(move || {
        communicate(
            &name,
            unsafe { GetCurrentProcess() } as isize,
            &receiver,
            &WorkerShared {
                state: worker_state,
                actions: worker_actions,
                stop: worker_stop,
            },
            &mut diagnostics,
            address,
        )
    });
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        if unsafe { ConnectNamedPipe(server.as_raw_handle(), std::ptr::null_mut()) } != 0
            || std::io::Error::last_os_error().raw_os_error() == Some(535)
        {
            break;
        }
        assert!(Instant::now() < deadline, "player did not connect");
        thread::sleep(Duration::from_millis(5));
    }
    let mut received = Vec::new();
    while !received.contains(&b'\n') {
        read_available(&mut server, &mut received).unwrap();
        assert!(
            Instant::now() < deadline,
            "player did not load after connecting"
        );
        thread::sleep(Duration::from_millis(5));
    }
    let first = received.split(|b| *b == b'\n').next().unwrap();
    let command: Value = serde_json::from_slice(first).unwrap();
    assert_eq!(command["command"], json!(["loadfile", address, "replace"]));
    writeln!(
        server,
        "{}",
        json!({"event":"end-file","reason":"error","file_error":"HTTP 404"})
    )
    .unwrap();
    while state.lock().unwrap().error.is_none() && Instant::now() < deadline {
        thread::sleep(Duration::from_millis(5));
    }
    stop.store(true, Ordering::Release);
    worker.join().unwrap().unwrap();
    drop(sender);
    let status = state.lock().unwrap();
    assert!(!status.ready);
    assert!(status.error.as_ref().unwrap().contains("HTTP 404"));
}

// Hidden native windows exercise ownership and clipping without a desktop,
// a video decoder, network access, or a running Slint event loop.
struct Host(isize);
impl Host {
    fn new() -> Self {
        let class: Vec<u16> = OsStr::new("STATIC").encode_wide().chain(Some(0)).collect();
        let window = unsafe {
            CreateWindowExW(
                0,
                class.as_ptr(),
                std::ptr::null(),
                0x8000_0000,
                0,
                0,
                640,
                480,
                0,
                0,
                0,
                std::ptr::null_mut(),
            )
        };
        assert_ne!(window, 0);
        Self(window)
    }
}
impl Drop for Host {
    fn drop(&mut self) {
        unsafe {
            DestroyWindow(self.0);
        }
    }
}

fn fixture(host: &Host, dir: &Path) -> VideoPlayer {
    let executable = dir.join("invalid-mpv.exe");
    fs::write(&executable, b"not an executable").unwrap();
    VideoPlayer::new(host.0, executable, dir.join("logs")).unwrap()
}

#[test]
fn embedded_mpv_child_is_enabled_only_for_its_own_process() {
    let host = Host::new();
    let class: Vec<u16> = OsStr::new("STATIC").encode_wide().chain(Some(0)).collect();
    let child = unsafe {
        CreateWindowExW(
            0,
            class.as_ptr(),
            std::ptr::null(),
            0x4800_0004, // WS_CHILD | WS_DISABLED | SS_BLACKRECT
            0,
            0,
            32,
            32,
            host.0,
            0,
            0,
            std::ptr::null_mut(),
        )
    };
    assert_ne!(child, 0);
    assert_eq!(unsafe { IsWindowEnabled(child) }, 0);

    enable_process_children(host.0, std::process::id().wrapping_add(1));
    assert_eq!(unsafe { IsWindowEnabled(child) }, 0);

    enable_process_children(host.0, std::process::id());
    assert_ne!(unsafe { IsWindowEnabled(child) }, 0);
    unsafe { DestroyWindow(child) };
}

#[test]
fn quality_switch_waits_for_load_and_recovers_after_failed_delivery() {
    let dir = tempfile::tempdir().unwrap();
    let host = Host::new();
    let mut player = fixture(&host, dir.path());
    let address = "https://example.org/720.mp4";
    assert!(
        player
            .switch_source(address)
            .unwrap_err()
            .contains("Дождитесь")
    );
    {
        let mut status = player.state.lock().unwrap();
        status.ready = true;
        status.position = 19.25;
        status.paused = true;
        status.volume = 43.0;
    }
    assert!(
        player
            .switch_source(address)
            .unwrap_err()
            .contains("недоступен")
    );
    let (sender, receiver) = mpsc::channel();
    player.commands = Some(sender);
    player.switch_source(address).unwrap();
    assert_eq!(
        receiver.try_recv().unwrap(),
        json!(["loadfile", address, "replace", -1, {"start":"19.250", "pause":"yes"}])
    );
    let pending = player.status();
    assert!(!pending.ready);
    assert_eq!(
        (pending.position, pending.volume, pending.paused),
        (19.25, 43.0, true)
    );
    assert!(player.switch_source(address).is_err());
    assert!(receiver.try_recv().is_err());
    {
        let mut status = player.state.lock().unwrap();
        apply_message(
            &mut status,
            &json!({"event":"end-file", "reason":"error", "file_error":"HTTP 404"}),
        );
        assert!(!status.ready);
        assert!(status.error.as_ref().unwrap().contains("HTTP 404"));
    }
    let recovery = "https://example.org/480.mp4";
    player.switch_source(recovery).unwrap();
    let retry = receiver.try_recv().unwrap();
    assert_eq!(retry[0], "loadfile");
    assert_eq!(retry[1], recovery);
    assert_eq!(retry[4]["pause"], "yes");
    assert!(player.status().error.is_none());
    assert!(!player.status().ready);
    apply_message(
        &mut player.state.lock().unwrap(),
        &json!({"event":"file-loaded"}),
    );
    drop(receiver);
    assert!(
        player
            .switch_source(address)
            .unwrap_err()
            .contains("Потеряна связь")
    );
    // A failed send must not leave the spinner active indefinitely.
    assert!(player.status().ready);
    assert!(player.status().error.is_none());
}

#[test]
fn playback_controls_clamp_values_ignore_nan_and_replay_from_start() {
    let dir = tempfile::tempdir().unwrap();
    let host = Host::new();
    let mut player = fixture(&host, dir.path());
    let (sender, receiver) = mpsc::channel();
    player.commands = Some(sender);
    player.seek(-12.0);
    player.seek(f64::NAN);
    player.seek(f64::INFINITY);
    player.set_volume(150.0);
    player.set_volume(-1.0);
    player.set_volume(f64::NAN);
    player.set_paused(true);
    player.state.lock().unwrap().ended = true;
    player.set_paused(false);
    assert_eq!(
        receiver.try_iter().collect::<Vec<_>>(),
        vec![
            json!(["seek", 0.0, "absolute+exact"]),
            json!(["set_property", "volume", 100.0]),
            json!(["set_property", "volume", 0.0]),
            json!(["set_property", "pause", true]),
            json!(["seek", 0.0, "absolute+exact"]),
            json!(["set_property", "pause", false]),
        ]
    );
    player.close();
    player.set_paused(false);
    assert!(receiver.try_recv().is_err());
    assert!(player.stop.load(Ordering::Acquire));
    assert!(!player.status().ready);
    assert!(player.status().pid.is_none());
}

#[test]
fn failed_video_hides_stale_frame_and_reveals_recovered_source() {
    let dir = tempfile::tempdir().unwrap();
    let host = Host::new();
    let mut player = fixture(&host, dir.path());
    player.set_rect(VideoRect {
        width: 320,
        height: 180,
        ..VideoRect::default()
    });
    player.child = Some(
        Command::new(std::env::current_exe().unwrap())
            .arg("--list")
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .creation_flags(0x0800_0000)
            .spawn()
            .unwrap(),
    );
    {
        let mut status = player.state.lock().unwrap();
        status.ready = true;
        status.width = 1280;
        status.height = 720;
        status.position = 1.0;
    }
    assert!(player.reveal_if_ready());
    assert!(player.reveal_if_ready());
    player.state.lock().unwrap().error = Some("HTTP 404".into());
    assert!(!player.reveal_if_ready());
    assert!(!player.visible);
    {
        let mut status = player.state.lock().unwrap();
        status.error = None;
        status.ready = false;
    }
    assert!(!player.reveal_if_ready());
    player.state.lock().unwrap().ready = true;
    assert!(player.reveal_if_ready());
    player.close();
    assert!(!player.visible);
}

#[test]
fn native_surface_validates_inputs_and_releases_only_its_child() {
    let dir = tempfile::tempdir().unwrap();
    let host = Host::new();
    assert!(VideoPlayer::new(0, "missing", dir.path()).is_err());
    assert!(VideoPlayer::new(-1, "missing", dir.path()).is_err());
    assert!(VideoPlayer::new(host.0, dir.path().join("missing"), dir.path()).is_err());
    let mut player = fixture(&host, dir.path());
    assert!(VideoPlayer::new(host.0, &player.mpv_exe, &player.mpv_exe).is_err());
    let child = player.window;
    let rect = VideoRect {
        x: 12,
        y: 24,
        width: 320,
        height: 180,
        radius: 200,
    };
    player.set_rect(rect);
    assert_eq!(player.region_shape, Some((320, 180, 90)));
    player.set_rect(rect);
    assert_eq!(player.rect, rect);
    player.set_rect(VideoRect { radius: 0, ..rect });
    assert_eq!(player.region_shape, Some((320, 180, 0)));
    assert!(!player.reveal_if_ready());
    player.set_rect(VideoRect::default());
    assert!(!player.visible);
    assert!(
        player
            .open(&dir.path().join("absent.mp4"), rect)
            .unwrap_err()
            .contains("не найдено")
    );
    let url = Path::new("https://example.org/video.mp4");
    assert!(
        player
            .open(url, VideoRect::default())
            .unwrap_err()
            .contains("положительным")
    );
    // Both remote URLs and canonical local files reach process launch;
    // a corrupt packaged player must fail cleanly and leave no worker.
    let local = dir.path().join("test.mp4");
    fs::write(&local, b"fixture").unwrap();
    for input in [url, local.as_path()] {
        assert!(
            player
                .open(input, rect)
                .unwrap_err()
                .contains("Не удалось запустить")
        );
        assert!(player.child.is_none());
        assert!(player.worker.is_none());
        assert!(!player.reveal_if_ready());
    }
    assert!(
        !fs::read_dir(dir.path().join("logs"))
            .unwrap()
            .flatten()
            .any(|entry| entry
                .file_name()
                .to_string_lossy()
                .starts_with("mpv-input-"))
    );
    drop(player);
    assert_eq!(unsafe { IsWindow(child) }, 0);
    assert_ne!(unsafe { IsWindow(host.0) }, 0);
}

#[test]
fn player_properties_update_diagnostics_and_ignore_unrelated_responses() {
    let mut status = VideoStatus::default();
    property(&mut status, "time-pos", json!(1.25));
    let started = status.playback_started_utc_ms;
    assert!(started.is_some());
    property(&mut status, "time-pos", json!(2.5));
    assert_eq!(status.playback_started_utc_ms, started);
    property(&mut status, "duration", json!(90.0));
    property(&mut status, "pause", json!(true));
    property(&mut status, "volume", json!(43.0));
    property(&mut status, "hwdec-current", json!("d3d11va"));
    property(&mut status, "video-params/w", json!(1920));
    property(&mut status, "video-params/h", json!(1080));
    property(&mut status, "container-fps", json!(60.0));
    property(&mut status, "estimated-frame-number", json!(150));
    property(&mut status, "frame-drop-count", json!(2));
    property(&mut status, "decoder-frame-drop-count", json!(3));
    property(&mut status, "avsync", json!(-0.015));
    property(&mut status, "audio-codec-name", json!("aac"));
    assert_eq!(
        (status.position, status.duration, status.volume),
        (2.5, 90.0, 43.0)
    );
    assert_eq!(
        (status.width, status.height, status.fps),
        (1920, 1080, 60.0)
    );
    assert_eq!(
        (
            status.estimated_frame_number,
            status.dropped_frames,
            status.decoder_dropped_frames
        ),
        (150, 2, 3)
    );
    assert_eq!(status.hwdec, "d3d11va");
    assert_eq!(status.audio_codec, "aac");
    assert_eq!(status.av_sync_seconds, -0.015);
    assert!(!status.is_playing());
    for message in [
        json!({}),
        json!({"request_id":0}),
        json!({"request_id":999}),
        json!({"request_id":1,"error":"property unavailable","data":100}),
    ] {
        apply_message(&mut status, &message);
    }
    for name in [
        "time-pos",
        "duration",
        "pause",
        "volume",
        "hwdec-current",
        "audio-codec-name",
    ] {
        property(&mut status, name, Value::Null);
    }
    assert_eq!(status.position, 2.5);
    assert_eq!(status.duration, 90.0);
    assert_eq!(status.volume, 43.0);
    assert!(status.paused);
    assert_eq!(status.hwdec, "d3d11va");
    assert_eq!(status.audio_codec, "aac");
}

#[test]
fn quality_switch_preserves_position_pause_and_uses_literal_url() {
    let mut status = super::VideoStatus {
        position: 37.125,
        paused: true,
        ..Default::default()
    };
    let address = "https://example.org/video.mp4?name=a,b";
    let command = super::quality_command(address, &status);
    assert_eq!(
        command,
        serde_json::json!(["loadfile",address,"replace",-1,{"start":"37.125","pause":"yes"}])
    );
    status.paused = false;
    assert_eq!(super::quality_command(address, &status)[4]["pause"], "no");
    status.ended = true;
    assert_eq!(
        super::quality_command(address, &status)[4],
        serde_json::json!({"start":"0.000","pause":"yes"})
    );
}
use super::*;

fn property(status: &mut VideoStatus, name: &str, value: Value) {
    let id = PROPERTIES.iter().position(|p| *p == name).unwrap() + 1;
    apply_message(
        status,
        &json!({"request_id": id, "error": "success", "data": value}),
    );
}

#[test]
fn finished_video_offers_replay_even_when_mpv_is_not_paused() {
    let mut status = VideoStatus::default();
    apply_message(&mut status, &json!({"event": "file-loaded"}));
    assert!(status.is_playing());
    property(&mut status, "eof-reached", json!(true));
    assert!(!status.paused);
    // The icon and its action must both offer playback on the first click.
    assert!(!status.is_playing());
    property(&mut status, "eof-reached", json!(false));
    assert!(status.is_playing());
    property(&mut status, "pause", json!(true));
    assert!(!status.is_playing());
    property(&mut status, "pause", json!(false));
    assert!(status.is_playing());
}
