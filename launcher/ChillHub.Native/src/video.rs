//! Native Windows video surface. Construct, resize and drop on the Slint UI thread.
//! Rectangles use physical pixels in the parent HWND's client area.

use serde_json::{Value, json};
use std::collections::VecDeque;
use std::ffi::{OsStr, c_void};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Write};
use std::marker::PhantomData;
use std::os::windows::ffi::OsStrExt;
use std::os::windows::io::AsRawHandle;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::rc::Rc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

#[link(name = "user32")]
unsafe extern "system" {
    fn CreateWindowExW(
        ex_style: u32,
        class: *const u16,
        title: *const u16,
        style: u32,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
        parent: isize,
        menu: isize,
        instance: isize,
        parameter: *mut c_void,
    ) -> isize;
    fn SetWindowPos(
        window: isize,
        after: isize,
        x: i32,
        y: i32,
        width: i32,
        height: i32,
        flags: u32,
    ) -> i32;
    fn ShowWindow(window: isize, command: i32) -> i32;
    fn SetWindowRgn(window: isize, region: isize, redraw: i32) -> i32;
    fn DestroyWindow(window: isize) -> i32;
    fn IsWindow(window: isize) -> i32;
    #[cfg(test)]
    fn IsWindowEnabled(window: isize) -> i32;
    fn EnableWindow(window: isize, enable: i32) -> i32;
    fn EnumChildWindows(
        parent: isize,
        callback: unsafe extern "system" fn(isize, isize) -> i32,
        parameter: isize,
    ) -> i32;
    fn GetWindowThreadProcessId(window: isize, process_id: *mut u32) -> u32;
}

#[link(name = "gdi32")]
unsafe extern "system" {
    fn CreateRoundRectRgn(
        left: i32,
        top: i32,
        right: i32,
        bottom: i32,
        ellipse_width: i32,
        ellipse_height: i32,
    ) -> isize;
    fn DeleteObject(object: isize) -> i32;
}

#[link(name = "kernel32")]
unsafe extern "system" {
    fn PeekNamedPipe(
        pipe: *mut c_void,
        buffer: *mut c_void,
        size: u32,
        read: *mut u32,
        available: *mut u32,
        remaining: *mut u32,
    ) -> i32;
    fn WaitForSingleObject(handle: *mut c_void, milliseconds: u32) -> u32;
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct VideoRect {
    pub x: i32,
    pub y: i32,
    pub width: i32,
    pub height: i32,
    pub radius: i32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum VideoAction {
    TogglePause,
    ToggleFullscreen,
}

#[derive(Clone, Debug)]
pub struct VideoStatus {
    pub position: f64,
    pub duration: f64,
    pub paused: bool,
    pub volume: f64,
    pub ready: bool,
    pub ended: bool,
    pub error: Option<String>,
    pub hwdec: String,
    pub pid: Option<u32>,
    pub width: u32,
    pub height: u32,
    pub fps: f64,
    pub estimated_frame_number: u64,
    pub dropped_frames: u64,
    pub decoder_dropped_frames: u64,
    pub av_sync_seconds: f64,
    pub audio_codec: String,
    pub playback_started_utc_ms: Option<u64>,
    #[cfg(feature = "benchmark")]
    pub log_path: PathBuf,
    #[cfg(feature = "benchmark")]
    pub diagnostics_path: PathBuf,
}

impl Default for VideoStatus {
    fn default() -> Self {
        Self {
            position: 0.0,
            duration: 0.0,
            paused: false,
            volume: 60.0,
            ready: false,
            ended: false,
            error: None,
            hwdec: String::new(),
            pid: None,
            width: 0,
            height: 0,
            fps: 0.0,
            estimated_frame_number: 0,
            dropped_frames: 0,
            decoder_dropped_frames: 0,
            av_sync_seconds: 0.0,
            audio_codec: String::new(),
            playback_started_utc_ms: None,
            #[cfg(feature = "benchmark")]
            log_path: PathBuf::new(),
            #[cfg(feature = "benchmark")]
            diagnostics_path: PathBuf::new(),
        }
    }
}

impl VideoStatus {
    pub fn is_playing(&self) -> bool {
        self.ready && !self.paused && !self.ended
    }
}

pub struct VideoPlayer {
    window: isize,
    mpv_exe: PathBuf,
    log_dir: PathBuf,
    child: Option<Child>,
    commands: Option<mpsc::Sender<Value>>,
    actions: Arc<Mutex<VecDeque<VideoAction>>>,
    input_conf_path: Option<PathBuf>,
    stop: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
    state: Arc<Mutex<VideoStatus>>,
    rect: VideoRect,
    region_shape: Option<(i32, i32, i32)>,
    visible: bool,
    // HWND lifetime and geometry are owned by the UI thread.
    _ui_thread: PhantomData<Rc<()>>,
}

impl VideoPlayer {
    pub fn new(
        parent_hwnd: isize,
        mpv_exe: impl Into<PathBuf>,
        log_dir: impl Into<PathBuf>,
    ) -> Result<Self, String> {
        if parent_hwnd == 0 || unsafe { IsWindow(parent_hwnd) } == 0 {
            return Err("Не удалось получить окно для видео".into());
        }
        let mpv_exe = mpv_exe.into();
        if !mpv_exe.is_file() {
            return Err(format!("Не найден mpv: {}", mpv_exe.display()));
        }
        let log_dir = log_dir.into();
        fs::create_dir_all(&log_dir).map_err(|error| error.to_string())?;
        let class: Vec<u16> = OsStr::new("STATIC").encode_wide().chain(Some(0)).collect();
        // WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | SS_BLACKRECT. Initially hidden.
        let window = unsafe {
            CreateWindowExW(
                0,
                class.as_ptr(),
                std::ptr::null(),
                0x4600_0004,
                0,
                0,
                1,
                1,
                parent_hwnd,
                0,
                0,
                std::ptr::null_mut(),
            )
        };
        if window == 0 {
            return Err(std::io::Error::last_os_error().to_string());
        }
        Ok(Self {
            window,
            mpv_exe,
            log_dir,
            child: None,
            commands: None,
            actions: Arc::new(Mutex::new(VecDeque::new())),
            input_conf_path: None,
            stop: Arc::new(AtomicBool::new(false)),
            worker: None,
            state: Arc::new(Mutex::new(VideoStatus::default())),
            rect: VideoRect::default(),
            region_shape: None,
            visible: false,
            _ui_thread: PhantomData,
        })
    }

    /// Starts the local file immediately with sound. Pipe connection is asynchronous.
    pub fn open(&mut self, path: &Path, rect: VideoRect) -> Result<(), String> {
        let raw = path.to_string_lossy();
        let remote = url::Url::parse(&raw).ok().filter(|u| {
            matches!(u.scheme(), "https" | "http")
                && u.username().is_empty()
                && u.password().is_none()
        });
        if remote.is_none() && !path.is_file() {
            return Err(format!("Видео не найдено: {}", path.display()));
        }
        if rect.width < 1 || rect.height < 1 {
            return Err("Размер области видео должен быть положительным".into());
        }
        let path = if let Some(url) = remote {
            PathBuf::from(url.as_str())
        } else {
            fs::canonicalize(path).map_err(|error| error.to_string())?
        };
        self.close();
        self.set_rect(rect);
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos();
        let session = format!("{}-{stamp}", std::process::id());
        let pipe_name = format!(r"\\.\pipe\chillhub-slint-video-{session}");
        let log_path = self.log_dir.join(format!("mpv-{session}.log"));
        let diagnostics_path = self.log_dir.join(format!("video-{session}.jsonl"));
        let stderr = File::create(self.log_dir.join(format!("mpv-{session}.stderr.log")))
            .map_err(|error| error.to_string())?;
        let diagnostics = File::create(&diagnostics_path).map_err(|error| error.to_string())?;
        let input_conf_path = self.log_dir.join(format!("mpv-input-{session}.conf"));
        let mut input_conf = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&input_conf_path)
            .map_err(|error| error.to_string())?;
        let write_result = input_conf.write_all(INPUT_CONF.as_bytes());
        drop(input_conf);
        if let Err(error) = write_result {
            let _ = fs::remove_file(&input_conf_path);
            return Err(error.to_string());
        }
        let mut command = Command::new(&self.mpv_exe);
        command.args([
            "--no-config",
            "--load-scripts=no",
            "--ytdl=no",
            "--terminal=no",
            "--input-default-bindings=no",
            "--input-vo-keyboard=no",
            // The embedded video needs pointer events for the scoped mouse bindings.
            "--input-cursor=yes",
            "--input-doubleclick-time=250",
            "--osc=no",
            "--osd-level=0",
            "--border=no",
            "--window-dragging=no",
            "--stop-screensaver=no",
            "--keep-open=yes",
            "--idle=yes",
            "--vo=gpu-next",
            "--gpu-api=d3d11",
            "--hwdec=auto-safe",
            "--pause=no",
            "--mute=no",
            "--volume=60",
            "--video-sync=audio",
            // Bound network readahead; mpv's default can fetch an entire trailer
            // before the viewer chooses a lower quality or closes the gallery.
            "--cache-secs=12",
            "--demuxer-max-bytes=16MiB",
            "--demuxer-max-back-bytes=4MiB",
        ]);
        // mpv interprets a Windows wid as an unsigned 32-bit HWND and creates its own child.
        command
            .arg(format!("--wid={}", self.window as u32))
            .arg(format!("--input-ipc-server={pipe_name}"))
            .arg(format!("--input-conf={}", input_conf_path.display()))
            .arg(format!("--log-file={}", log_path.display()))
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(stderr)
            .creation_flags(0x0800_0000); // CREATE_NO_WINDOW suppresses the console, not the embedded renderer.
        // Keep the host hidden while mpv initializes its native child and swapchain.
        // The playback timer reveals it once the IPC reports video progress.
        let child = match command.spawn() {
            Ok(child) => child,
            Err(error) => {
                let _ = fs::remove_file(&input_conf_path);
                return Err(format!("Не удалось запустить видео: {error}"));
            }
        };
        self.input_conf_path = Some(input_conf_path);
        let process_handle = child.as_raw_handle() as isize;
        let initial = VideoStatus {
            pid: Some(child.id()),
            #[cfg(feature = "benchmark")]
            log_path,
            #[cfg(feature = "benchmark")]
            diagnostics_path,
            ..VideoStatus::default()
        };
        *self.state.lock().unwrap_or_else(|error| error.into_inner()) = initial;
        self.stop = Arc::new(AtomicBool::new(false));
        let (sender, receiver) = mpsc::channel();
        let shared = WorkerShared {
            state: Arc::clone(&self.state),
            actions: Arc::clone(&self.actions),
            stop: Arc::clone(&self.stop),
        };
        self.worker = Some(thread::spawn(move || {
            run_worker(
                pipe_name,
                process_handle,
                receiver,
                shared,
                diagnostics,
                path.to_string_lossy().into_owned(),
            );
        }));
        self.commands = Some(sender);
        self.child = Some(child);
        Ok(())
    }

    pub fn set_rect(&mut self, rect: VideoRect) {
        let radius = rect
            .radius
            .max(0)
            .min(rect.width.min(rect.height).max(0) / 2);
        let shape = (rect.width, rect.height, radius);
        if self.rect == rect && self.region_shape == Some(shape) {
            return;
        }
        let geometry_changed = self.rect != rect;
        self.rect = rect;
        if rect.width < 1 || rect.height < 1 {
            self.visible = false;
            unsafe {
                ShowWindow(self.window, 0);
            }
            return;
        }
        unsafe {
            if geometry_changed {
                SetWindowPos(
                    self.window,
                    0,
                    rect.x,
                    rect.y,
                    rect.width,
                    rect.height,
                    0x0014,
                );
            }
            if self.region_shape != Some(shape) {
                // The Slint corner masks cannot cover a native child HWND.
                // Clip the host itself so the mpv child inherits the same boundary.
                let region = if radius == 0 {
                    0
                } else {
                    CreateRoundRectRgn(
                        0,
                        0,
                        rect.width.saturating_add(1),
                        rect.height.saturating_add(1),
                        radius * 2,
                        radius * 2,
                    )
                };
                if radius == 0 || region != 0 {
                    if SetWindowRgn(self.window, region, i32::from(self.visible)) != 0 {
                        // Windows owns the HRGN after success and releases it when
                        // replaced or when the HWND is destroyed. Never delete it here.
                        self.region_shape = Some(shape);
                    } else if region != 0 {
                        // Failed transfer: the region is still ours to release.
                        DeleteObject(region);
                    }
                }
            }
        }
    }

    /// Called from the existing playback-status timer on the UI thread.
    /// Neither file-loaded nor process creation means the video surface is ready.
    pub fn reveal_if_ready(&mut self) -> bool {
        let (ready, failed) = {
            let status = self.state.lock().unwrap_or_else(|error| error.into_inner());
            (
                status.ready
                    && status.error.is_none()
                    && status.width > 0
                    && status.height > 0
                    && (status.estimated_frame_number > 0 || status.position > 0.0),
                status.error.is_some(),
            )
        };
        if failed {
            if self.visible {
                unsafe { ShowWindow(self.window, 0) };
                self.visible = false;
            }
            return false;
        }
        if self.visible {
            return true;
        }
        if self.child.is_none() || self.rect.width < 1 || self.rect.height < 1 {
            return false;
        }
        if ready {
            if let Some(child) = self.child.as_ref() {
                enable_process_children(self.window, child.id());
            }
            unsafe { ShowWindow(self.window, 8) };
            self.visible = true;
        }
        self.visible
    }

    #[cfg(feature = "benchmark")]
    pub fn is_revealed(&self) -> bool {
        self.visible
    }

    pub fn set_paused(&self, paused: bool) {
        if !paused && self.status().ended {
            self.seek(0.0);
        }
        self.send(json!(["set_property", "pause", paused]));
    }
    pub fn seek(&self, seconds: f64) {
        if seconds.is_finite() {
            self.send(json!(["seek", seconds.max(0.0), "absolute+exact"]));
        }
    }
    /// Keep the player, volume and playback position when changing rendition.
    pub fn switch_source(&self, address: &str) -> Result<(), String> {
        let mut status = self.state.lock().unwrap_or_else(|error| error.into_inner());
        if !status.ready && status.error.is_none() {
            return Err("Дождитесь загрузки видео перед сменой качества".into());
        }
        self.commands
            .as_ref()
            .ok_or_else(|| "Видеоплеер недоступен".to_string())?
            .send(quality_command(address, &status))
            .map_err(|_| "Потеряна связь с видео".to_string())?;
        status.ready = false;
        status.ended = false;
        status.error = None;
        Ok(())
    }
    pub fn set_volume(&self, percent: f64) {
        if percent.is_finite() {
            self.send(json!(["set_property", "volume", percent.clamp(0.0, 100.0)]));
        }
    }
    pub fn status(&self) -> VideoStatus {
        self.state
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .clone()
    }
    /// Returns mouse actions received from mpv for the UI thread to apply.
    pub fn take_actions(&mut self) -> Vec<VideoAction> {
        self.actions
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .drain(..)
            .collect()
    }
    pub fn close(&mut self) {
        self.visible = false;
        unsafe { ShowWindow(self.window, 0) };
        self.stop.store(true, Ordering::Release);
        self.commands = None;
        // Termination uses the owned Child handle, never a name or recycled PID.
        if let Some(mut child) = self.child.take() {
            let _ = child.kill();
            let _ = child.wait();
        }
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
        if let Some(input_conf_path) = self.input_conf_path.take() {
            let _ = fs::remove_file(input_conf_path);
        }
        self.actions
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .clear();
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        state.ready = false;
        state.pid = None;
    }
    fn send(&self, command: Value) {
        if let Some(sender) = &self.commands {
            let _ = sender.send(command);
        }
    }
}

fn quality_command(address: &str, status: &VideoStatus) -> Value {
    json!(["loadfile", address, "replace", -1, {
        "start": format!("{:.3}", if status.ended { 0.0 } else { status.position.max(0.0) }),
        "pause": if status.paused || status.ended { "yes" } else { "no" }
    }])
}

impl Drop for VideoPlayer {
    fn drop(&mut self) {
        self.close();
        unsafe {
            DestroyWindow(self.window);
        }
    }
}

struct EnableProcessChildren {
    process_id: u32,
}

unsafe extern "system" fn enable_process_child(window: isize, parameter: isize) -> i32 {
    let context = unsafe { &*(parameter as *const EnableProcessChildren) };
    let mut process_id = 0;
    unsafe { GetWindowThreadProcessId(window, &mut process_id) };
    if process_id == context.process_id {
        // mpv creates its embedded renderer as a disabled child HWND. That
        // makes Windows send pointer hits to the host instead of the renderer.
        unsafe { EnableWindow(window, 1) };
    }
    1
}

fn enable_process_children(parent: isize, process_id: u32) {
    let context = EnableProcessChildren { process_id };
    unsafe {
        EnumChildWindows(
            parent,
            enable_process_child,
            &context as *const EnableProcessChildren as isize,
        );
    }
}

const PROPERTIES: &[&str] = &[
    "time-pos",
    "duration",
    "pause",
    "volume",
    "eof-reached",
    "hwdec-current",
    "video-params/w",
    "video-params/h",
    "container-fps",
    "estimated-frame-number",
    "frame-drop-count",
    "decoder-frame-drop-count",
    "avsync",
    "audio-codec-name",
];

const INPUT_CONF: &str = "MBTN_LEFT script-message chillhub left-click\nMBTN_LEFT_DBL script-message chillhub double-click\n";
const DOUBLE_CLICK_WINDOW: Duration = Duration::from_millis(250);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum MouseMessage {
    LeftClick,
    DoubleClick,
}

fn parse_mouse_message(message: &Value) -> Option<MouseMessage> {
    if message["event"] != "client-message" {
        return None;
    }
    let args = message["args"].as_array()?;
    if args.len() != 2 || args[0].as_str()? != "chillhub" {
        return None;
    }
    match args[1].as_str()? {
        "left-click" => Some(MouseMessage::LeftClick),
        "double-click" => Some(MouseMessage::DoubleClick),
        _ => None,
    }
}

#[derive(Default)]
struct ClickCoalescer {
    pending_single: Option<Instant>,
    suppress_single_until: Option<Instant>,
}

impl ClickCoalescer {
    fn push(&mut self, message: MouseMessage, now: Instant) -> Option<VideoAction> {
        match message {
            MouseMessage::LeftClick => {
                if self.suppress_single_until.is_some_and(|until| now < until) {
                    return None;
                }
                self.pending_single = Some(now + DOUBLE_CLICK_WINDOW);
                None
            }
            MouseMessage::DoubleClick => {
                self.pending_single = None;
                self.suppress_single_until = Some(now + DOUBLE_CLICK_WINDOW);
                Some(VideoAction::ToggleFullscreen)
            }
        }
    }

    fn take_expired(&mut self, now: Instant) -> Option<VideoAction> {
        if self.pending_single.is_some_and(|deadline| now >= deadline) {
            self.pending_single = None;
            Some(VideoAction::TogglePause)
        } else {
            None
        }
    }
}

struct WorkerShared {
    state: Arc<Mutex<VideoStatus>>,
    actions: Arc<Mutex<VecDeque<VideoAction>>>,
    stop: Arc<AtomicBool>,
}

fn run_worker(
    pipe_name: String,
    process: isize,
    commands: mpsc::Receiver<Value>,
    shared: WorkerShared,
    mut diagnostics: File,
    address: String,
) {
    let result = communicate(
        &pipe_name,
        process,
        &commands,
        &shared,
        &mut diagnostics,
        &address,
    );
    if let Err(error) = result
        && !shared.stop.load(Ordering::Acquire)
    {
        let mut status = shared
            .state
            .lock()
            .unwrap_or_else(|error| error.into_inner());
        status.error = Some(error);
        status.ready = false;
        let _ = writeln!(
            diagnostics,
            "{}",
            json!({"error": status.error, "pid": status.pid})
        );
    }
}

fn communicate(
    pipe_name: &str,
    process: isize,
    commands: &mpsc::Receiver<Value>,
    shared: &WorkerShared,
    diagnostics: &mut File,
    address: &str,
) -> Result<(), String> {
    let start = Instant::now();
    let mut pipe = loop {
        if shared.stop.load(Ordering::Acquire) {
            return Ok(());
        }
        if unsafe { WaitForSingleObject(process as *mut c_void, 0) } == 0 {
            return Err("Видеоплеер завершился при запуске. Подробности в журнале mpv.".into());
        }
        match OpenOptions::new().read(true).write(true).open(pipe_name) {
            Ok(pipe) => break pipe,
            Err(_) if start.elapsed() < Duration::from_secs(8) => {
                thread::sleep(Duration::from_millis(25))
            }
            Err(error) => return Err(format!("Не удалось подключиться к видеоплееру: {error}")),
        }
    };
    // Start only after IPC is connected: an immediate HTTP failure must not
    // occur before we can receive its end-file event.
    write_command(
        &mut pipe,
        json!({"command": ["loadfile", address, "replace"]}),
    )?;
    let mut pending = Vec::new();
    let mut clicks = ClickCoalescer::default();
    let mut last_poll = Instant::now() - Duration::from_secs(1);
    let mut last_log = Instant::now() - Duration::from_secs(1);
    while !shared.stop.load(Ordering::Acquire) {
        if unsafe { WaitForSingleObject(process as *mut c_void, 0) } == 0 {
            return Err("Видеоплеер завершился. Подробности в журнале mpv.".into());
        }
        for command in commands.try_iter() {
            write_command(&mut pipe, json!({"command": command}))?;
        }
        if last_poll.elapsed() >= Duration::from_millis(250) {
            for (id, property) in PROPERTIES.iter().enumerate() {
                write_command(
                    &mut pipe,
                    json!({"command": ["get_property", property], "request_id": id + 1}),
                )?;
            }
            last_poll = Instant::now();
        }
        read_available(&mut pipe, &mut pending)?;
        while let Some(newline) = pending.iter().position(|byte| *byte == b'\n') {
            let line: Vec<u8> = pending.drain(..=newline).collect();
            if let Ok(value) = serde_json::from_slice::<Value>(&line) {
                if let Some(message) = parse_mouse_message(&value)
                    && let Some(action) = clicks.push(message, Instant::now())
                {
                    shared
                        .actions
                        .lock()
                        .unwrap_or_else(|error| error.into_inner())
                        .push_back(action);
                }
                let mut status = shared
                    .state
                    .lock()
                    .unwrap_or_else(|error| error.into_inner());
                apply_message(&mut status, &value);
            }
        }
        if let Some(action) = clicks.take_expired(Instant::now()) {
            shared
                .actions
                .lock()
                .unwrap_or_else(|error| error.into_inner())
                .push_back(action);
        }
        if pending.len() > 1024 * 1024 {
            return Err("Слишком длинный ответ видеоплеера".into());
        }
        if last_log.elapsed() >= Duration::from_secs(1) {
            let status = shared
                .state
                .lock()
                .unwrap_or_else(|error| error.into_inner());
            let _ = writeln!(
                diagnostics,
                "{}",
                json!({
                    "elapsed": start.elapsed().as_secs_f64(), "pid": status.pid,
                    "playbackStartedUtcMs": status.playback_started_utc_ms,
                    "ready": status.ready, "position": status.position, "duration": status.duration,
                    "paused": status.paused, "volume": status.volume, "hwdec": status.hwdec,
                    "width": status.width, "height": status.height, "fps": status.fps,
                    "estimatedFrame": status.estimated_frame_number, "droppedFrames": status.dropped_frames,
                    "decoderDroppedFrames": status.decoder_dropped_frames, "avSyncSeconds": status.av_sync_seconds,
                    "audioCodec": status.audio_codec, "error": status.error
                })
            );
            last_log = Instant::now();
        }
        thread::sleep(Duration::from_millis(25));
    }
    Ok(())
}

fn write_command(pipe: &mut File, value: Value) -> Result<(), String> {
    writeln!(pipe, "{value}").map_err(|error| format!("Ошибка управления видео: {error}"))
}

fn read_available(pipe: &mut File, pending: &mut Vec<u8>) -> Result<(), String> {
    let mut available = 0;
    let success = unsafe {
        PeekNamedPipe(
            pipe.as_raw_handle(),
            std::ptr::null_mut(),
            0,
            std::ptr::null_mut(),
            &mut available,
            std::ptr::null_mut(),
        )
    };
    if success == 0 {
        return Err(format!(
            "Потеряна связь с видео: {}",
            std::io::Error::last_os_error()
        ));
    }
    if available > 0 {
        let mut buffer = vec![0; available.min(64 * 1024) as usize];
        let count = pipe.read(&mut buffer).map_err(|error| error.to_string())?;
        pending.extend_from_slice(&buffer[..count]);
    }
    Ok(())
}

fn apply_message(status: &mut VideoStatus, message: &Value) {
    if message["event"] == "file-loaded" {
        status.ready = true;
        status.error = None;
    }
    if message["event"] == "end-file" && message["reason"] == "error" {
        status.error = Some(format!(
            "Не удалось воспроизвести видео: {}",
            message["file_error"]
        ));
        status.ready = false;
    }
    let Some(id) = message["request_id"]
        .as_u64()
        .and_then(|id| id.checked_sub(1))
    else {
        return;
    };
    let Some(property) = PROPERTIES.get(id as usize) else {
        return;
    };
    if message["error"] != "success" {
        return;
    }
    let value = &message["data"];
    match *property {
        "time-pos" => {
            if let Some(value) = value.as_f64() {
                status.position = value;
                status.ready = true;
                if value > 0.0 && status.playback_started_utc_ms.is_none() {
                    status.playback_started_utc_ms = Some(
                        SystemTime::now()
                            .duration_since(UNIX_EPOCH)
                            .unwrap_or_default()
                            .as_millis() as u64,
                    );
                }
            }
        }
        "duration" => {
            if let Some(value) = value.as_f64() {
                status.duration = value;
            }
        }
        "pause" => {
            if let Some(value) = value.as_bool() {
                status.paused = value;
            }
        }
        "volume" => {
            if let Some(value) = value.as_f64() {
                status.volume = value;
            }
        }
        "eof-reached" => {
            if let Some(value) = value.as_bool() {
                status.ended = value;
            }
        }
        "hwdec-current" => {
            if let Some(value) = value.as_str() {
                status.hwdec = value.into();
            }
        }
        "video-params/w" => status.width = value.as_u64().unwrap_or(0) as u32,
        "video-params/h" => status.height = value.as_u64().unwrap_or(0) as u32,
        "container-fps" => status.fps = value.as_f64().unwrap_or(0.0),
        "estimated-frame-number" => status.estimated_frame_number = value.as_u64().unwrap_or(0),
        "frame-drop-count" => status.dropped_frames = value.as_u64().unwrap_or(0),
        "decoder-frame-drop-count" => status.decoder_dropped_frames = value.as_u64().unwrap_or(0),
        "avsync" => status.av_sync_seconds = value.as_f64().unwrap_or(0.0),
        "audio-codec-name" => {
            if let Some(value) = value.as_str() {
                status.audio_codec = value.into();
            }
        }
        _ => {}
    }
}

#[cfg(test)]
#[path = "video_tests.rs"]
mod tests;
