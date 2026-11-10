//! Optional legacy header decoration. No audio, network, or background polling.
use crate::AppWindow;
use slint::ComponentHandle;
use std::{cell::RefCell, rc::Rc};

struct Ticker {
    lines: Vec<&'static str>,
    line: usize,
    chars: usize,
    fading: bool,
}
impl Ticker {
    fn new(text: &'static str) -> Self {
        Self {
            lines: text.split('\n').map(str::trim_end).collect(),
            line: 0,
            chars: 0,
            fading: false,
        }
    }
    fn current(&self) -> String {
        self.lines[self.line].chars().take(self.chars).collect()
    }
    fn next(&self) -> &'static str {
        self.lines[(self.line + 1) % self.lines.len()]
    }
    fn tick(&mut self) -> (u64, bool) {
        if self.fading {
            self.line = (self.line + 1) % self.lines.len();
            self.chars = 0;
            self.fading = false;
        } else if self.chars >= self.lines[self.line].chars().count() {
            self.fading = true;
            return (150, true);
        }
        let count = self.lines[self.line].chars().count();
        self.chars = (self.chars + 1).min(count);
        (
            if count == 0 {
                5000
            } else if self.chars == count {
                450
            } else {
                45
            },
            false,
        )
    }
}

pub(super) fn bind(ui: &AppWindow) {
    let ticker = Rc::new(RefCell::new(Ticker::new(include_str!(
        "../Assets/karaoke.txt"
    ))));
    ui.set_karaoke_next(ticker.borrow().next().into());
    let weak = ui.as_weak();
    ui.on_karaoke_tick(move || {
        if let Some(ui) = weak.upgrade() {
            let mut ticker = ticker.borrow_mut();
            let (delay, fading) = ticker.tick();
            ui.set_karaoke_text(ticker.current().into());
            ui.set_karaoke_next(ticker.next().into());
            ui.set_karaoke_delay(delay as i32);
            ui.set_karaoke_fading(fading);
        }
    });
}

#[cfg(test)]
mod tests {
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
}
