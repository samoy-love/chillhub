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
#[path = "changelog_tests.rs"]
mod tests;
