use super::*;
#[test]
fn unicode_lines_pause_fade_and_loop_without_skipping_letters() {
    let mut ticker = Ticker::new("Я🎮\n\nДа");
    assert_eq!(ticker.tick(), (45, false));
    assert_eq!(ticker.current(), "Я");
    assert_eq!(ticker.tick(), (450, false));
    assert_eq!(ticker.current(), "Я🎮");
    assert_eq!(ticker.tick(), (150, true));
    assert_eq!(ticker.tick(), (5000, false));
    assert_eq!(ticker.current(), "");
    assert_eq!(ticker.next(), "Да");
    assert_eq!(ticker.tick(), (150, true));
    assert_eq!(ticker.tick(), (45, false));
    assert_eq!(ticker.current(), "Д");
    ticker.tick();
    ticker.tick();
    ticker.tick();
    assert_eq!(ticker.current(), "Я");
}
