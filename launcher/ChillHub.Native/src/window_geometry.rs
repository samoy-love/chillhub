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
mod tests {
    use super::*;
    #[test]
    fn restore_fits_small_hidpi_workarea_and_bad_preferences() {
        for saved in [
            (0., 0.),
            (1., 1.),
            (f64::NAN, f64::INFINITY),
            (4000., 3000.),
        ] {
            let (w, h) = restored_inner_size(saved, 1.25, (16, 39), (1366, 728));
            assert!(w <= 1350 && h <= 689 && w > 0 && h > 0, "{w}×{h}");
        }
        assert_eq!(
            restored_inner_size((1200., 800.), 1., (16, 39), (2560, 1560)),
            (1184, 761)
        );
        assert_eq!(
            restored_inner_size((1., 1.), 1., (100, 100), (20, 20)),
            (1, 1)
        );
    }
}
