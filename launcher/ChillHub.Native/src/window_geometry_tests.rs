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
