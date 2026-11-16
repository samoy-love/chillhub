use super::read_limited;
use crate::paths::{atomic_write, contained_path};
use std::{ops::Range, path::Path};

const FILE: &str = "doorstop_config.ini";
const MAX_BYTES: u64 = 256 * 1024;
const UNREADABLE: &str = "Не удалось прочитать настройки загрузчика модов";

#[derive(Clone, Copy)]
enum Encoding {
    Utf8,
    Utf8Bom,
    Utf16Le,
    Utf16Be,
}

impl Encoding {
    fn decode(bytes: &[u8]) -> Result<(Self, String), String> {
        if let Some(bytes) = bytes.strip_prefix(&[0xff, 0xfe]) {
            return Self::decode_utf16(Self::Utf16Le, bytes, u16::from_le_bytes);
        }
        if let Some(bytes) = bytes.strip_prefix(&[0xfe, 0xff]) {
            return Self::decode_utf16(Self::Utf16Be, bytes, u16::from_be_bytes);
        }
        let (encoding, text) = match bytes.strip_prefix(&[0xef, 0xbb, 0xbf]) {
            Some(text) => (Self::Utf8Bom, text),
            None => (Self::Utf8, bytes),
        };
        String::from_utf8(text.to_vec())
            .map(|text| (encoding, text))
            .map_err(|_| UNREADABLE.into())
    }

    fn decode_utf16(
        encoding: Self,
        bytes: &[u8],
        read_unit: fn([u8; 2]) -> u16,
    ) -> Result<(Self, String), String> {
        let (units, tail) = bytes.as_chunks::<2>();
        if !tail.is_empty() {
            return Err(UNREADABLE.into());
        }
        let units: Vec<_> = units.iter().copied().map(read_unit).collect();
        String::from_utf16(&units)
            .map(|text| (encoding, text))
            .map_err(|_| UNREADABLE.into())
    }

    fn encode(self, text: &str) -> Vec<u8> {
        match self {
            Self::Utf8 => text.as_bytes().to_vec(),
            Self::Utf8Bom => [0xef, 0xbb, 0xbf].into_iter().chain(text.bytes()).collect(),
            Self::Utf16Le => [0xff, 0xfe]
                .into_iter()
                .chain(text.encode_utf16().flat_map(u16::to_le_bytes))
                .collect(),
            Self::Utf16Be => [0xfe, 0xff]
                .into_iter()
                .chain(text.encode_utf16().flat_map(u16::to_be_bytes))
                .collect(),
        }
    }
}

fn enabled_value(text: &str) -> Option<Range<usize>> {
    let mut offset = 0;
    for line in text.split_inclusive(['\r', '\n']) {
        let trimmed = line.trim_start();
        if !trimmed.starts_with([';', '#', '['])
            && let Some((key, tail)) = line.split_once('=')
            && key.trim().eq_ignore_ascii_case("enabled")
        {
            let value = tail
                .split([';', '#', '\r', '\n'])
                .next()
                .unwrap_or_default();
            let start = offset + key.len() + 1 + value.len() - value.trim_start().len();
            return Some(start..start + value.trim().len());
        }
        offset += line.len();
    }
    None
}

fn enabled_state(value: &str) -> Option<bool> {
    if value.eq_ignore_ascii_case("true") || value == "1" {
        Some(true)
    } else if value.eq_ignore_ascii_case("false") || value == "0" {
        Some(false)
    } else {
        None
    }
}

/// Existing self-managed loader settings are preserved only while usable. The
/// enabled flag intentionally differs from the published hash between sessions.
pub fn doorstop_config_valid(root: &Path) -> bool {
    let Ok(path) = contained_path(root, Path::new(FILE)) else {
        return false;
    };
    let Ok(bytes) = read_limited(&path, MAX_BYTES) else {
        return false;
    };
    let Ok((_, text)) = Encoding::decode(&bytes) else {
        return false;
    };
    enabled_value(&text).is_some_and(|range| enabled_state(&text[range]).is_some())
}

pub fn set_doorstop_enabled(root: &Path, enabled: bool) -> Result<(), String> {
    let path = contained_path(root, Path::new(FILE))?;
    match std::fs::metadata(&path) {
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
            return if enabled {
                Err("Загрузчик модов не установлен".into())
            } else {
                Ok(())
            };
        }
        Err(error) => return Err(error.to_string()),
        Ok(_) => {}
    }
    let bytes = read_limited(&path, MAX_BYTES)?;
    let (encoding, mut text) = Encoding::decode(&bytes)?;
    let value = enabled_value(&text)
        .ok_or("В настройках загрузчика модов нет параметра enabled. Восстановите моды")?;
    if enabled_state(&text[value.clone()]) != Some(enabled) {
        text.replace_range(value, if enabled { "true" } else { "false" });
        atomic_write(&path, &encoding.encode(&text)).map_err(|e| e.to_string())?;
    }
    Ok(())
}
