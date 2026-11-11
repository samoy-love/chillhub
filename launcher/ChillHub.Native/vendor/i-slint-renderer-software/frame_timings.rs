// Copyright © 2026 Chill Hub contributors
// SPDX-License-Identifier: GPL-3.0-only OR LicenseRef-Slint-Royalty-free-2.0 OR LicenseRef-Slint-Software-3.0
//! Bounded, opt-in CPU raster timings for diagnostic builds. No per-frame IO.
use std::{cell::RefCell, time::Instant, vec::Vec};
type Sample = (f64, f64, usize, usize);
std::thread_local! {
    static CAPTURE: RefCell<Option<(Instant, Vec<Sample>)>> = const { RefCell::new(None) };
}
/// Start or stop recording on the rendering thread.
pub fn enable(enabled: bool) {
    CAPTURE.with(|capture| *capture.borrow_mut() = enabled.then(|| (Instant::now(), Vec::new())));
}
/// Samples are (start milliseconds, duration milliseconds, width, height).
pub fn samples() -> Vec<Sample> {
    CAPTURE.with(|capture| {
        capture
            .borrow()
            .as_ref()
            .map(|(_, samples)| samples.clone())
            .unwrap_or_default()
    })
}
pub(crate) fn begin(width: usize, height: usize) -> Option<Frame> {
    CAPTURE.with(|capture| {
        capture
            .borrow()
            .as_ref()
            .filter(|(_, samples)| samples.len() < 32768)
            .map(|_| Frame {
                start: Instant::now(),
                width,
                height,
            })
    })
}
pub(crate) struct Frame {
    start: Instant,
    width: usize,
    height: usize,
}
impl Drop for Frame {
    fn drop(&mut self) {
        let elapsed = self.start.elapsed().as_secs_f64() * 1000.0;
        CAPTURE.with(|capture| {
            if let Some((start, samples)) = capture.borrow_mut().as_mut() {
                samples.push((
                    self.start.duration_since(*start).as_secs_f64() * 1000.0,
                    elapsed,
                    self.width,
                    self.height,
                ));
            }
        });
    }
}
