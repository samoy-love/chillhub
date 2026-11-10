//! Opt-in diagnostic capture. Keep the returned handle until after the event loop exits.
//! No timer, redraw request, or per-frame file IO is introduced by this module.
//! RenderingNotifier can alter renderer submission behavior: do not enable it in RAM/GPU runs.
use crate::AppWindow;
use serde::Serialize;
use slint::winit_030::{EventResult, WinitWindowAccessor, winit};
use slint::{ComponentHandle, RenderingState};
use std::cell::RefCell;
use std::io::{self, Write};
use std::path::PathBuf;
use std::rc::Rc;
use std::time::{Instant, SystemTime, UNIX_EPOCH};

const MAX_EVENTS: usize = 32_768;

#[derive(Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
struct Event {
    ms: f64,
    state: &'static str,
    frame: u64,
    redraw: u64,
    resize_sequence: u64,
    native_width_px: u32,
    native_height_px: u32,
    slint_width_px: u32,
    slint_height_px: u32,
    scale_factor: f64,
    render_duration_ms: Option<f64>,
    after_render_gap_ms: Option<f64>,
    redraw_gap_ms: Option<f64>,
    since_resize_ms: Option<f64>,
    geometry_matches: bool,
    flag: Option<bool>,
}

struct Capture {
    start: Instant,
    start_unix_ms: u128,
    events: Vec<Event>,
    dropped_events: u64,
    active: bool,
    frame: u64,
    redraw: u64,
    resize_sequence: u64,
    last_resize_ms: Option<f64>,
    before_ms: Option<f64>,
    previous_after_ms: Option<f64>,
    previous_redraw_ms: Option<f64>,
    native_width_px: u32,
    native_height_px: u32,
    scale_factor: f64,
    notifier_available: bool,
    notifier_requested: bool,
    notifier_error: Option<String>,
}

impl Capture {
    fn push(&mut self, state: &'static str, window: &slint::Window, flag: Option<bool>) {
        if !self.active {
            return;
        }
        let ms = self.start.elapsed().as_secs_f64() * 1000.0;
        let size = window.size();
        let mut render_duration_ms = None;
        let mut after_render_gap_ms = None;
        let mut redraw_gap_ms = None;
        if state == "redrawRequested" {
            self.redraw += 1;
            redraw_gap_ms = self
                .previous_redraw_ms
                .replace(ms)
                .map(|redraw| ms - redraw);
        }
        if state == "beforeRendering" {
            self.frame += 1;
            self.before_ms = Some(ms);
        } else if state == "afterRendering" {
            render_duration_ms = self.before_ms.take().map(|before| ms - before);
            after_render_gap_ms = self.previous_after_ms.replace(ms).map(|after| ms - after);
        }
        let event = Event {
            ms,
            state,
            frame: self.frame,
            redraw: self.redraw,
            resize_sequence: self.resize_sequence,
            native_width_px: self.native_width_px,
            native_height_px: self.native_height_px,
            slint_width_px: size.width,
            slint_height_px: size.height,
            scale_factor: self.scale_factor,
            render_duration_ms,
            after_render_gap_ms,
            redraw_gap_ms,
            since_resize_ms: self.last_resize_ms.map(|resize| ms - resize),
            geometry_matches: self.native_width_px.abs_diff(size.width) <= 1
                && self.native_height_px.abs_diff(size.height) <= 1,
            flag,
        };
        if self.events.len() < MAX_EVENTS {
            self.events.push(event);
        } else {
            self.dropped_events += 1;
        }
    }
}

pub struct ProbeHandle {
    capture: Rc<RefCell<Capture>>,
    output: PathBuf,
}

impl ProbeHandle {
    /// Optional explicit checkpoint; normally Drop writes the capture once on clean exit.
    pub fn flush(&self) -> io::Result<()> {
        let capture = self.capture.borrow();
        if let Some(parent) = self.output.parent().filter(|p| !p.as_os_str().is_empty()) {
            std::fs::create_dir_all(parent)?;
        }
        let mut writer = io::BufWriter::new(std::fs::File::create(&self.output)?);
        let document = serde_json::json!({
            "formatVersion": 1,
            "processId": std::process::id(),
            "startUnixMs": capture.start_unix_ms,
            "elapsedMs": capture.start.elapsed().as_secs_f64() * 1000.0,
            "notifierAvailable": capture.notifier_available,
            "notifierRequested": capture.notifier_requested,
            "notifierError": capture.notifier_error,
            "eventLimit": MAX_EVENTS,
            "droppedEvents": capture.dropped_events,
            "events": capture.events,
            "limitations": [
                "AfterRendering runs before buffer presentation. Render durations measure CPU-side rendering/submission, not GPU completion or displayed FPS.",
                "Installing RenderingNotifier can add flush/submit work in the renderer; compare only equivalently instrumented diagnostics and keep it disabled for resource measurements.",
                "Geometry matches compare Slint's cached window-adapter size with Winit's resize/inner-size observations; they do not inspect UI layout or presented pixels. A mismatch in the pre-dispatch Resized event is expected; inspect subsequent RedrawRequested events.",
                "Default capture uses Winit resize/redraw events only and never installs RenderingNotifier. RedrawRequested is the start of a render attempt, not presentation. Set CHILLHUB_PROBE_RENDER_TIMING=1 only for separate instrumented diagnostics.",
                "Idle gaps, focus loss, minimization and pauses between manual drag gestures are not missed frames. Inspect timestamps and resize events before deriving pacing statistics.",
                "This probe owns the single Winit event-filter callback and always propagates events. Do not install another filter over it."
            ]
        });
        serde_json::to_writer(&mut writer, &document)?;
        writer.flush()
    }

    pub fn notifier_available(&self) -> bool {
        self.capture.borrow().notifier_available
    }
}

impl Drop for ProbeHandle {
    fn drop(&mut self) {
        self.capture.borrow_mut().active = false;
        if let Err(error) = self.flush() {
            eprintln!(
                "Frame probe could not write {}: {error}",
                self.output.display()
            );
        }
    }
}

pub fn install(ui: &AppWindow, output: PathBuf) -> Result<ProbeHandle, String> {
    if output.as_os_str().is_empty() {
        return Err("Frame-probe output path is empty".into());
    }
    let window = ui.window();
    let native = window
        .with_winit_window(|native| {
            let size = native.inner_size();
            (size.width, size.height, native.scale_factor())
        })
        .ok_or("Frame probe needs a live Winit window; install after native initialization")?;
    let capture = Rc::new(RefCell::new(Capture {
        start: Instant::now(),
        start_unix_ms: SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_millis(),
        events: Vec::with_capacity(MAX_EVENTS),
        dropped_events: 0,
        active: true,
        frame: 0,
        redraw: 0,
        resize_sequence: 0,
        last_resize_ms: None,
        before_ms: None,
        previous_after_ms: None,
        previous_redraw_ms: None,
        native_width_px: native.0,
        native_height_px: native.1,
        scale_factor: native.2,
        notifier_available: false,
        notifier_requested: std::env::var("CHILLHUB_PROBE_RENDER_TIMING").as_deref() == Ok("1"),
        notifier_error: None,
    }));
    capture.borrow_mut().push("installed", window, None);

    let event_capture = capture.clone();
    window.on_winit_window_event(move |window, event| {
        use winit::event::WindowEvent;
        let mut data = event_capture.borrow_mut();
        if !data.active {
            return EventResult::Propagate;
        }
        match event {
            WindowEvent::Resized(size) => {
                data.native_width_px = size.width;
                data.native_height_px = size.height;
                data.resize_sequence += 1;
                data.last_resize_ms = Some(data.start.elapsed().as_secs_f64() * 1000.0);
                data.push("resized", window, None);
            }
            WindowEvent::ScaleFactorChanged { scale_factor, .. } => {
                data.scale_factor = *scale_factor;
                data.push("scaleChanged", window, None);
            }
            WindowEvent::RedrawRequested => {
                if let Some((size, scale)) =
                    window.with_winit_window(|native| (native.inner_size(), native.scale_factor()))
                {
                    data.native_width_px = size.width;
                    data.native_height_px = size.height;
                    data.scale_factor = scale;
                }
                data.push("redrawRequested", window, None);
            }
            WindowEvent::Focused(value) => data.push("focused", window, Some(*value)),
            WindowEvent::Occluded(value) => data.push("occluded", window, Some(*value)),
            WindowEvent::CloseRequested => data.push("closeRequested", window, None),
            _ => {}
        }
        EventResult::Propagate
    });

    if capture.borrow().notifier_requested {
        let render_capture = capture.clone();
        let weak = ui.as_weak();
        let result = window.set_rendering_notifier(move |state, _graphics| {
            let Some(ui) = weak.upgrade() else {
                return;
            };
            let mut data = render_capture.borrow_mut();
            if !data.active {
                return;
            }
            // Read the OS client size at the render boundary, independently of the last event.
            if let Some((size, scale)) = ui
                .window()
                .with_winit_window(|native| (native.inner_size(), native.scale_factor()))
            {
                data.native_width_px = size.width;
                data.native_height_px = size.height;
                data.scale_factor = scale;
            }
            let state = match state {
                RenderingState::RenderingSetup => "renderingSetup",
                RenderingState::BeforeRendering => "beforeRendering",
                RenderingState::AfterRendering => "afterRendering",
                RenderingState::RenderingTeardown => "renderingTeardown",
                _ => "otherRenderingState",
            };
            data.push(state, ui.window(), None);
        });
        match result {
            Ok(()) => capture.borrow_mut().notifier_available = true,
            Err(error) => capture.borrow_mut().notifier_error = Some(format!("{error:?}")),
        }
    }
    Ok(ProbeHandle { capture, output })
}
