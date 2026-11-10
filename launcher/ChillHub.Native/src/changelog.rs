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
pub fn text() -> String {
    text_since("")
}
pub fn text_since(last_seen: &str) -> String {
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
            format!(
                "{} · {}{}\n{}",
                r.version,
                r.date,
                if fresh { " · Новое" } else { "" },
                r.changes
                    .into_iter()
                    .map(|c| format!("• {c}"))
                    .collect::<Vec<_>>()
                    .join("\n")
            )
        })
        .collect::<Vec<_>>()
        .join("\n\n")
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn new_marks_use_previous_seen_version_and_disappear_on_reopen() {
        assert!(!text_since("").contains(" · Новое"));
        assert!(!text_since(env!("CARGO_PKG_VERSION")).contains(" · Новое"));
        assert!(text_since("1.8.0").contains(" · Новое"));
        assert!(!text_since("9.0.0").contains(" · Новое"));
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
