use chrono::Datelike;
use serde::Deserialize;
#[derive(Deserialize)]
struct Release {
    version: String,
    date: String,
    changes: Vec<String>,
}
pub fn should_show(last_seen: &str, current: &str) -> bool {
    let Ok(current) = crate::update::version_parts(current) else {
        return false;
    };
    let last = crate::update::version_parts(last_seen).ok();
    if last.is_some_and(|last| current <= last) {
        return false;
    }
    let releases: Vec<Release> =
        serde_json::from_str(include_str!("../Data/changelog.json")).unwrap_or_default();
    releases.iter().any(|release| {
        !release.changes.is_empty()
            && crate::update::version_parts(&release.version)
                .is_ok_and(|version| version <= current && last.is_none_or(|last| version > last))
    })
}
pub struct Entry {
    pub version: String,
    pub date: String,
    pub items: Vec<String>,
    pub fresh: bool,
}
fn display_date(value: &str) -> String {
    const MONTHS: [&str; 12] = [
        "января",
        "февраля",
        "марта",
        "апреля",
        "мая",
        "июня",
        "июля",
        "августа",
        "сентября",
        "октября",
        "ноября",
        "декабря",
    ];
    chrono::NaiveDate::parse_from_str(value, "%Y-%m-%d")
        .map(|date| {
            format!(
                "{} {} {}",
                date.day(),
                MONTHS[date.month0() as usize],
                date.year()
            )
        })
        .unwrap_or_else(|_| value.to_owned())
}
pub fn entries_since(last_seen: &str) -> Vec<Entry> {
    let last = crate::update::version_parts(last_seen).ok();
    let current = crate::update::version_parts(env!("CARGO_PKG_VERSION")).ok();
    let releases: Vec<Release> = serde_json::from_str(include_str!("../Data/changelog.json"))
        .expect("validated bundled changelog");
    releases
        .into_iter()
        .map(|r| {
            let fresh = last.zip(current).is_some_and(|(last, current)| {
                crate::update::version_parts(&r.version)
                    .is_ok_and(|version| version > last && version <= current)
            });
            Entry {
                version: r.version,
                date: display_date(&r.date),
                fresh,
                items: r.changes,
            }
        })
        .collect()
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn release_dates_share_russian_format_without_losing_existing_text() {
        assert_eq!(display_date("2026-11-10"), "10 ноября 2026");
        assert_eq!(display_date("10 ноября 2026"), "10 ноября 2026");
        assert_eq!(display_date("unknown"), "unknown");
    }
    #[test]
    fn new_marks_use_previous_seen_version_and_disappear_on_reopen() {
        for seen in ["", "unknown", env!("CARGO_PKG_VERSION"), "9.0.0"] {
            assert!(entries_since(seen).iter().all(|entry| !entry.fresh));
        }
        let entries = entries_since("1.8.0");
        for version in [env!("CARGO_PKG_VERSION"), "1.8.1"] {
            assert!(entries.iter().find(|e| e.version == version).unwrap().fresh);
        }
        for version in ["1.8.0", "1.7.20", "1.0"] {
            assert!(!entries.iter().find(|e| e.version == version).unwrap().fresh);
        }
        let first = entries.iter().find(|e| e.version == "1.0").unwrap();
        assert_eq!(first.date, "20 сентября 2025");
        assert_eq!(first.items.len(), 2);
        assert!(first.items[0].starts_with("Первый Chill Hub:"));
    }
    #[test]
    fn first_install_upgrade_repeat_and_rollback_have_distinct_gates() {
        assert!(should_show("", env!("CARGO_PKG_VERSION")));
        assert!(should_show("1.7.20", env!("CARGO_PKG_VERSION")));
        assert!(!should_show(
            env!("CARGO_PKG_VERSION"),
            env!("CARGO_PKG_VERSION")
        ));
        assert!(!should_show("2.0.0", env!("CARGO_PKG_VERSION")));
        assert!(should_show("1.8.0", "1.8.1"));
        assert!(!should_show("", "unknown"));
        assert!(
            !should_show("", "0.9.0"),
            "no history predates the first release"
        );
        assert!(
            should_show("1.8.0.1", "1.8.1"),
            "last-seen version need not have its own history entry"
        );
        assert!(
            !should_show("1.8.1", "1.8.1.1"),
            "an already seen entry is not new in a later build without release notes"
        );
        assert!(
            !should_show("99.0.0", "99.0.1"),
            "no entries in the requested version interval"
        );
    }
    #[test]
    fn current_release_is_first_and_player_changes_are_bounded() {
        let entries: Vec<Release> =
            serde_json::from_str(include_str!("../Data/changelog.json")).unwrap();
        assert_eq!(entries[0].version, env!("CARGO_PKG_VERSION"));
        assert!(entries.len() > 20);
        let current = &entries[0];
        assert!(!current.changes.is_empty() && current.changes.len() <= 3);
        assert!(
            current
                .changes
                .iter()
                .all(|c| !c.trim().is_empty() && c.chars().count() <= 110)
        );
        assert!(entries.iter().all(|r| !r.changes.is_empty()));
    }
}
