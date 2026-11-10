use super::{UpdateOutcome, UpdatePaths};
use std::{collections::BTreeMap, io::Read, path::Path};

fn read_bounded(path: &Path, limit: u64) -> Option<Vec<u8>> {
    let file = crate::manifest::open_regular(path, false, false, false).ok()?;
    if file.metadata().ok()?.len() > limit {
        return None;
    }
    let mut bytes = Vec::new();
    file.take(limit + 1).read_to_end(&mut bytes).ok()?;
    (bytes.len() as u64 <= limit).then_some(bytes)
}

fn unescape(value: &str) -> String {
    let mut result = String::new();
    let mut chars = value.chars();
    while let Some(c) = chars.next() {
        if c == '\\' {
            match chars.next() {
                Some('n') => result.push('\n'),
                Some(next) => result.push(next),
                None => result.push('\\'),
            }
        } else {
            result.push(c);
        }
    }
    result
}

pub(super) fn read(paths: &UpdatePaths) -> Option<UpdateOutcome> {
    let bytes = read_bounded(&paths.install.join("launcher.update-status"), 64 * 1024)?;
    let fingerprint = blake3::hash(&bytes).to_hex().to_string();
    if read_bounded(&paths.config.join("legacy-update-ack"), 64).as_deref()
        == Some(fingerprint.as_bytes())
    {
        return None;
    }
    let text = std::str::from_utf8(&bytes)
        .ok()?
        .trim_start_matches('\u{feff}');
    let fields: BTreeMap<_, _> = text
        .lines()
        .filter_map(|line| {
            let (key, value) = line.split_once('=')?;
            Some((key.to_ascii_lowercase(), unescape(value)))
        })
        .collect();
    let outcome = fields.get("outcome")?.to_ascii_lowercase();
    if !matches!(
        outcome.as_str(),
        "ok" | "copy-errors" | "integrity" | "fatal"
    ) {
        return None;
    }
    let success = outcome == "ok";
    let message = fields
        .get("message")
        .filter(|value| !value.trim().is_empty())
        .cloned()
        .unwrap_or_else(|| {
            if success {
                "Обновление установлено"
            } else {
                "Предыдущее обновление не завершилось. Проверьте обновления ещё раз."
            }
            .into()
        });
    Some(UpdateOutcome {
        version: fields.get("version").cloned().unwrap_or_default(),
        success,
        message,
        legacy_fingerprint: Some(fingerprint),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    #[test]
    fn legacy_outcome_is_shown_once_without_losing_a_newer_result() {
        let temp = tempfile::tempdir().unwrap();
        let staged = super::super::tests::package(temp.path());
        let paths = &staged.paths;
        let file = paths.install.join("launcher.update-status");
        let first = "outcome=fatal\nversion=1.8.1\ntime=first\nmessage=Ошибка\\nПуть C:\\\\Games\n";
        fs::write(&file, first).unwrap();
        let shown = super::super::previous_outcome(paths).unwrap();
        assert!(!shown.success);
        assert_eq!(shown.message, "Ошибка\nПуть C:\\Games");
        fs::write(&file, first.replace("time=first", "time=second")).unwrap();
        assert!(!super::super::acknowledge_outcome(paths, &shown).unwrap());
        let next = super::super::previous_outcome(paths).unwrap();
        assert!(super::super::acknowledge_outcome(paths, &next).unwrap());
        assert!(super::super::previous_outcome(paths).is_none());
        assert!(file.exists());
        fs::write(&file, "outcome=ok\nversion=1.8.2\nmessage=Готово\n").unwrap();
        assert!(super::super::previous_outcome(paths).unwrap().success);
    }
    #[test]
    fn malformed_or_oversized_legacy_outcomes_are_ignored() {
        let temp = tempfile::tempdir().unwrap();
        let staged = super::super::tests::package(temp.path());
        let file = staged.paths.install.join("launcher.update-status");
        for data in [
            b"message=only".to_vec(),
            b"outcome=unknown".to_vec(),
            vec![b'x'; 65537],
        ] {
            fs::write(&file, data).unwrap();
            assert!(read(&staged.paths).is_none());
        }
    }
    #[test]
    fn native_result_supersedes_existing_legacy_message() {
        let temp = tempfile::tempdir().unwrap();
        let staged = super::super::tests::package(temp.path());
        fs::write(
            staged.paths.install.join("launcher.update-status"),
            "outcome=fatal\nmessage=Old failure\n",
        )
        .unwrap();
        let current = UpdateOutcome {
            version: "1.8.2".into(),
            success: true,
            message: "New success".into(),
            legacy_fingerprint: None,
        };
        super::super::write_outcome(&staged.paths, &current).unwrap();
        assert_eq!(
            super::super::previous_outcome(&staged.paths),
            Some(current.clone())
        );
        assert!(super::super::acknowledge_outcome(&staged.paths, &current).unwrap());
        assert!(super::super::previous_outcome(&staged.paths).is_none());
    }
}
