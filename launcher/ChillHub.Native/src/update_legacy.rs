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
#[path = "update_legacy_tests.rs"]
mod tests;
