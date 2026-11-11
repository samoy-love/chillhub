//! Opt-in benchmark transport. The application owns its timer and all UI actions.
//! Call `poll` from COMMAND_POLL_INTERVAL only when `from_args` returns `Some`.
use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};
use std::collections::HashSet;
use std::fs;
use std::io::{self, Write};
use std::path::PathBuf;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// Matches the isolated WebView2 benchmark host; 750 ms switches span three ticks.
pub const COMMAND_POLL_INTERVAL: Duration = Duration::from_millis(250);

#[derive(Clone, Copy, Debug, Deserialize, Serialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum Phase {
    Idle,
    Switch,
    Trailer,
    #[serde(rename = "afterclose")]
    AfterClose,
    Minimized,
    Warmup,
    Close,
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Command {
    pub id: String,
    pub phase: Phase,
    #[serde(default = "default_seconds")]
    pub seconds: f64,
    #[serde(default = "default_count")]
    pub count: usize,
    #[serde(default = "default_interval")]
    pub interval_ms: u64,
    #[serde(default = "default_games")]
    pub game_ids: Vec<String>,
}
fn default_seconds() -> f64 {
    20.0
}
fn default_count() -> usize {
    30
}
fn default_interval() -> u64 {
    750
}
fn default_games() -> Vec<String> {
    [
        "lethal-company",
        "how-to-fish",
        "repo",
        "over-the-hill",
        "bombanana",
        "farfarwest",
        "drive-beyond-horizons",
        "machine-party",
        "peak",
    ]
    .into_iter()
    .map(str::to_owned)
    .collect()
}

#[derive(Clone, Debug, Default, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowInfo {
    pub width_dip: f64,
    pub height_dip: f64,
    pub pixel_width: u32,
    pub pixel_height: u32,
    pub client_pixel_width: u32,
    pub client_pixel_height: u32,
    pub dpi: u32,
    pub visible: bool,
    pub minimized: bool,
    pub foreground: bool,
}

impl WindowInfo {
    /// Observes a real HWND without changing activation, size or visibility.
    pub fn from_hwnd(hwnd: isize) -> Self {
        #[cfg(windows)]
        {
            #[repr(C)]
            #[derive(Default)]
            struct Rect {
                left: i32,
                top: i32,
                right: i32,
                bottom: i32,
            }
            #[link(name = "user32")]
            unsafe extern "system" {
                fn GetWindowRect(hwnd: isize, rect: *mut Rect) -> i32;
                fn GetClientRect(hwnd: isize, rect: *mut Rect) -> i32;
                fn GetDpiForWindow(hwnd: isize) -> u32;
                fn IsWindowVisible(hwnd: isize) -> i32;
                fn IsIconic(hwnd: isize) -> i32;
                fn GetForegroundWindow() -> isize;
                fn GetAncestor(hwnd: isize, flags: u32) -> isize;
            }
            let mut outer = Rect::default();
            let mut client = Rect::default();
            // SAFETY: caller supplies the live application's HWND. Output structs are writable.
            unsafe {
                GetWindowRect(hwnd, &mut outer);
                GetClientRect(hwnd, &mut client);
                let dpi = GetDpiForWindow(hwnd).max(1);
                let width = (outer.right - outer.left).max(0) as u32;
                let height = (outer.bottom - outer.top).max(0) as u32;
                Self {
                    width_dip: width as f64 * 96.0 / dpi as f64,
                    height_dip: height as f64 * 96.0 / dpi as f64,
                    pixel_width: width,
                    pixel_height: height,
                    client_pixel_width: client.right.max(0) as u32,
                    client_pixel_height: client.bottom.max(0) as u32,
                    dpi,
                    visible: IsWindowVisible(hwnd) != 0,
                    minimized: IsIconic(hwnd) != 0,
                    foreground: GetAncestor(GetForegroundWindow(), 2) == GetAncestor(hwnd, 2),
                }
            }
        }
        #[cfg(not(windows))]
        {
            let _ = hwnd;
            Self::default()
        }
    }
}

pub struct Benchmark {
    directory: PathBuf,
    seen: HashSet<String>,
    started: Option<(String, String)>,
}
impl Benchmark {
    /// No directory creation, file reads or timers when the flag is absent.
    pub fn from_args() -> io::Result<Option<Self>> {
        let mut args = std::env::args_os().skip(1);
        while let Some(arg) = args.next() {
            if arg == "--benchmark-dir" {
                let path = args.next().ok_or_else(|| {
                    io::Error::new(
                        io::ErrorKind::InvalidInput,
                        "--benchmark-dir requires a path",
                    )
                })?;
                return Self::new(PathBuf::from(path)).map(Some);
            }
        }
        Ok(None)
    }
    pub fn new(directory: PathBuf) -> io::Result<Self> {
        if !directory.is_absolute() {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Benchmark directory must be absolute",
            ));
        }
        fs::create_dir_all(&directory)?;
        let mut result = Self {
            directory,
            seen: HashSet::new(),
            started: None,
        };
        // Ignore a stale command from an earlier process using the same directory.
        if let Ok(bytes) = fs::read(result.directory.join("command.json"))
            && let Ok(command) = serde_json::from_slice::<Command>(&bytes)
        {
            result.seen.insert(command.id);
        }
        Ok(result)
    }
    pub fn poll(&mut self) -> io::Result<Option<Command>> {
        let bytes = match fs::read(self.directory.join("command.json")) {
            Ok(bytes) => bytes,
            Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(error) => return Err(error),
        };
        // The controller atomically renames a complete UTF-8 JSON file into place.
        let command: Command = serde_json::from_slice(&bytes)
            .map_err(|e| io::Error::new(io::ErrorKind::InvalidData, e))?;
        if !self.seen.insert(command.id.clone()) {
            return Ok(None);
        }
        if command.id.is_empty()
            || command.id.len() > 128
            || !command.seconds.is_finite()
            || !(0.0..=120.0).contains(&command.seconds)
            || !(1..=100).contains(&command.count)
            || !(100..=5000).contains(&command.interval_ms)
            || command.game_ids.is_empty()
            || command
                .game_ids
                .iter()
                .any(|id| !default_games().contains(id))
        {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "Invalid benchmark command",
            ));
        }
        Ok(Some(command))
    }
    pub fn ready(&mut self, window: &WindowInfo) -> io::Result<()> {
        self.write("startup", "startup", "ready", window, Value::Null)
    }
    pub fn preparing(&mut self, command: &Command, window: &WindowInfo) -> io::Result<()> {
        self.started = None;
        self.command_state(command, "preparing", window, Value::Null)
    }
    /// Call after preparation. For trailer, require advancing time and a decoded frame first.
    pub fn running(
        &mut self,
        command: &Command,
        window: &WindowInfo,
        detail: Value,
    ) -> io::Result<()> {
        self.started = Some((command.id.clone(), utc_now()));
        self.command_state(command, "running", window, detail)
    }
    pub fn complete(
        &mut self,
        command: &Command,
        window: &WindowInfo,
        detail: Value,
    ) -> io::Result<()> {
        let mut detail = object(detail);
        detail.insert("endedUtc".into(), Value::String(utc_now()));
        self.command_state(command, "complete", window, Value::Object(detail))
    }
    pub fn error(
        &mut self,
        command: &Command,
        window: &WindowInfo,
        message: impl ToString,
    ) -> io::Result<()> {
        self.command_state(
            command,
            "error",
            window,
            serde_json::json!({"error":message.to_string(),"endedUtc":utc_now()}),
        )
    }
    fn command_state(
        &mut self,
        command: &Command,
        state: &str,
        window: &WindowInfo,
        detail: Value,
    ) -> io::Result<()> {
        let mut detail = object(detail);
        if let Some((id, started)) = &self.started
            && id == &command.id
        {
            detail.insert("startedUtc".into(), Value::String(started.clone()));
        }
        let phase = serde_json::to_value(command.phase).map_err(io::Error::other)?;
        self.write(
            &command.id,
            phase.as_str().unwrap_or("unknown"),
            state,
            window,
            Value::Object(detail),
        )
    }
    fn write(
        &self,
        id: &str,
        phase: &str,
        state: &str,
        window: &WindowInfo,
        detail: Value,
    ) -> io::Result<()> {
        let value = serde_json::json!({"id":id,"state":state,"phase":phase,"utc":utc_now(),
            "pid":std::process::id(),"window":window,"detail":detail,
            "instrumentation":{"commandPollIntervalMs":COMMAND_POLL_INTERVAL.as_millis()}});
        let data = serde_json::to_vec_pretty(&value).map_err(io::Error::other)?;
        let next = self.directory.join("status.next");
        fs::write(&next, &data)?;
        let destination = self.directory.join("status.json");
        // Windows rename does not replace existing destinations. MoveFileExW does.
        #[cfg(windows)]
        {
            use std::os::windows::ffi::OsStrExt;
            #[link(name = "kernel32")]
            unsafe extern "system" {
                fn MoveFileExW(from: *const u16, to: *const u16, flags: u32) -> i32;
            }
            let from: Vec<u16> = next.as_os_str().encode_wide().chain(Some(0)).collect();
            let to: Vec<u16> = destination
                .as_os_str()
                .encode_wide()
                .chain(Some(0))
                .collect();
            // SAFETY: both paths are valid, null-terminated UTF-16 arrays alive for the call.
            if unsafe { MoveFileExW(from.as_ptr(), to.as_ptr(), 1 | 8) } == 0 {
                return Err(io::Error::last_os_error());
            }
        }
        #[cfg(not(windows))]
        fs::rename(next, destination)?;
        let mut events = fs::OpenOptions::new()
            .create(true)
            .append(true)
            .open(self.directory.join("events.jsonl"))?;
        serde_json::to_writer(&mut events, &value).map_err(io::Error::other)?;
        events.write_all(b"\n")
    }
}
fn object(value: Value) -> Map<String, Value> {
    if let Value::Object(map) = value {
        map
    } else {
        Map::new()
    }
}

/// RFC3339 UTC without adding a runtime/dependency to the prototype.
pub fn utc_now() -> String {
    let duration = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    let seconds = duration.as_secs();
    let days = (seconds / 86400) as i64;
    let z = days + 719468;
    let era = z / 146097;
    let doe = z - era * 146097;
    let yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
    let mut year = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let day = doy - (153 * mp + 2) / 5 + 1;
    let month = mp + if mp < 10 { 3 } else { -9 };
    if month <= 2 {
        year += 1;
    }
    format!(
        "{year:04}-{month:02}-{day:02}T{:02}:{:02}:{:02}.{:03}Z",
        seconds / 3600 % 24,
        seconds / 60 % 60,
        seconds % 60,
        duration.subsec_millis()
    )
}
