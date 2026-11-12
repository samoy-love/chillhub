//! Native Windows video surface. Construct, resize and drop on the Slint UI thread.
//! Rectangles use physical pixels in the parent HWND's client area.

use serde_json::{Value, json};
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
        let mut command = Command::new(&self.mpv_exe);
        command.args([
            "--no-config",
            "--load-scripts=no",
            "--ytdl=no",
            "--terminal=no",
            "--input-default-bindings=no",
            "--input-vo-keyboard=no",
            "--input-cursor=no",
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
            .arg(format!("--log-file={}", log_path.display()))
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(stderr)
            .creation_flags(0x0800_0000); // CREATE_NO_WINDOW suppresses the console, not the embedded renderer.
        // Keep the host hidden while mpv initializes its native child and swapchain.
        // The playback timer reveals it once the IPC reports video progress.
        let child = command
            .spawn()
            .map_err(|error| format!("Не удалось запустить видео: {error}"))?;
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
        let state = Arc::clone(&self.state);
        let stop = Arc::clone(&self.stop);
        self.worker = Some(thread::spawn(move || {
            run_worker(
                pipe_name,
                process_handle,
                receiver,
                state,
                stop,
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

fn run_worker(
    pipe_name: String,
    process: isize,
    commands: mpsc::Receiver<Value>,
    state: Arc<Mutex<VideoStatus>>,
    stop: Arc<AtomicBool>,
    mut diagnostics: File,
    address: String,
) {
    let result = communicate(
        &pipe_name,
        process,
        &commands,
        &state,
        &stop,
        &mut diagnostics,
        &address,
    );
    if let Err(error) = result
        && !stop.load(Ordering::Acquire)
    {
        let mut status = state.lock().unwrap_or_else(|error| error.into_inner());
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
    state: &Arc<Mutex<VideoStatus>>,
    stop: &AtomicBool,
    diagnostics: &mut File,
    address: &str,
) -> Result<(), String> {
    let start = Instant::now();
    let mut pipe = loop {
        if stop.load(Ordering::Acquire) {
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
    let mut last_poll = Instant::now() - Duration::from_secs(1);
    let mut last_log = Instant::now() - Duration::from_secs(1);
    while !stop.load(Ordering::Acquire) {
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
                let mut status = state.lock().unwrap_or_else(|error| error.into_inner());
                apply_message(&mut status, &value);
            }
        }
        if pending.len() > 1024 * 1024 {
            return Err("Слишком длинный ответ видеоплеера".into());
        }
        if last_log.elapsed() >= Duration::from_secs(1) {
            let status = state.lock().unwrap_or_else(|error| error.into_inner());
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
mod tests {
    use std::os::windows::io::FromRawHandle;

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
        let stop = Arc::new(AtomicBool::new(false));
        let worker_state = Arc::clone(&state);
        let worker_stop = Arc::clone(&stop);
        let (sender, receiver) = mpsc::channel();
        let mut diagnostics = File::create(dir.path().join("video.jsonl")).unwrap();
        let address = "https://example.org/ролик.mp4?quality=1080";
        let worker = thread::spawn(move || {
            communicate(
                &name,
                unsafe { GetCurrentProcess() } as isize,
                &receiver,
                &worker_state,
                &worker_stop,
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
}
