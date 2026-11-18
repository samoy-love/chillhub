use chrono::Datelike;
use serde::Deserialize;

use crate::update::{compare_release_versions, version_parts};

#[derive(Deserialize)]
struct Release {
    version: String,
    date: String,
    changes: Vec<String>,
}
pub fn should_show(last_seen: &str, current: &str) -> bool {
    if version_parts(current).is_err() {
        return false;
    }
    let last = version_parts(last_seen).is_ok().then_some(last_seen);
    if last.is_some_and(|last| !newer(current, last)) {
        return false;
    }
    let releases: Vec<Release> =
        serde_json::from_str(include_str!("../Data/changelog.json")).unwrap_or_default();
    releases.iter().any(|release| {
        !release.changes.is_empty()
            && through_current(&release.version, current)
            && last.is_none_or(|last| newer(&release.version, last))
    })
}

fn newer(version: &str, than: &str) -> bool {
    compare_release_versions(version, than).is_ok_and(|order| order.is_gt())
}

fn through_current(version: &str, current: &str) -> bool {
    compare_release_versions(version, current).is_ok_and(|order| !order.is_gt())
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
    let current = env!("CARGO_PKG_VERSION");
    let releases: Vec<Release> = serde_json::from_str(include_str!("../Data/changelog.json"))
        .expect("validated bundled changelog");
    releases
        .into_iter()
        .map(|r| {
            let fresh = newer(&r.version, last_seen) && through_current(&r.version, current);
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
#[path = "changelog_tests.rs"]
mod tests;
