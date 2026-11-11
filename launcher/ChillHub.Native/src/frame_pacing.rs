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
mod tests {
    use super::*;
    #[test]
    fn first_frame_and_deadline_render_without_delay() {
        let now = Instant::now();
        let mut budget = FrameBudget::default();
        assert_eq!(budget.request(now), None);
        assert_eq!(budget.request(now + FRAME_INTERVAL), None);
    }
    #[test]
    fn repeated_requests_do_not_move_the_deadline() {
        let now = Instant::now();
        let mut budget = FrameBudget::default();
        budget.request(now);
        for ms in [1, 4, 8, 12] {
            assert_eq!(
                budget.request(now + Duration::from_millis(ms)),
                Some(FRAME_INTERVAL - Duration::from_millis(ms))
            );
        }
        assert_eq!(budget.request(now + FRAME_INTERVAL), None);
    }
    #[test]
    fn input_and_resize_bypass_the_animation_budget() {
        let now = Instant::now();
        let mut budget = FrameBudget::default();
        budget.request(now);
        budget.invalidate_immediately();
        assert_eq!(budget.request(now + Duration::from_millis(1)), None);
        assert_eq!(
            budget.request(now + Duration::from_millis(2)),
            Some(FRAME_INTERVAL - Duration::from_millis(1))
        );
    }
}
