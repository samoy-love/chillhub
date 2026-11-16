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
