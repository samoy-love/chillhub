//! Pace decorative animation without delaying input or native window resizing.
use std::time::{Duration, Instant};

pub const FRAME_INTERVAL: Duration = Duration::from_nanos(1_000_000_000 / 60);

#[derive(Default)]
pub struct FrameBudget {
    last_frame: Option<Instant>,
    urgent: bool,
}
impl FrameBudget {
    pub fn invalidate_immediately(&mut self) {
        self.urgent = true;
    }
    /// None means render now; a duration means keep the invalidation pending.
    pub fn request(&mut self, now: Instant) -> Option<Duration> {
        if !self.urgent
            && let Some(last) = self.last_frame
        {
            let elapsed = now.saturating_duration_since(last);
            if elapsed < FRAME_INTERVAL {
                return Some(FRAME_INTERVAL - elapsed);
            }
        }
        self.urgent = false;
        self.last_frame = Some(now);
        None
    }
}

#[cfg(test)]
#[path = "frame_pacing_tests.rs"]
mod tests;
