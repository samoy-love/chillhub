/// Restore an outer window size in physical pixels, constrained to its current
/// monitor work area. Bad/old preferences cannot produce unsigned underflow.
pub fn restored_inner_size(
    saved: (f64, f64),
    scale: f64,
    frame: (u32, u32),
    work: (u32, u32),
) -> (u32, u32) {
    let scale = if scale.is_finite() && scale > 0. {
        scale
    } else {
        1.
    };
    fn axis(saved: f64, default: f64, minimum: f64, scale: f64, frame: u32, work: u32) -> u32 {
        let desired = if saved.is_finite() && saved >= minimum {
            saved
        } else {
            default
        };
        let available = work.saturating_sub(frame).max(1);
        let minimum = ((minimum * scale) as u32).min(available);
        ((desired * scale) as u32)
            .saturating_sub(frame)
            .clamp(minimum, available)
    }
    (
        axis(saved.0, 1360., 900., scale, frame.0, work.0),
        axis(saved.1, 850., 500., scale, frame.1, work.1),
    )
}

#[cfg(test)]
#[path = "window_geometry_tests.rs"]
mod tests;
