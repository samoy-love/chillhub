//! Official manual guidance; no third-party product settings are modified.
use super::*;

#[derive(Clone, Deserialize)]
pub struct AntivirusGuide {
    pub id: String,
    pub name: String,
    pub instructions: String,
    pub url: String,
    aliases: Vec<String>,
}

pub fn guides() -> &'static [AntivirusGuide] {
    static GUIDES: std::sync::OnceLock<Vec<AntivirusGuide>> = std::sync::OnceLock::new();
    GUIDES.get_or_init(|| {
        serde_json::from_str(include_str!("../Data/security-guides.json"))
            .expect("embedded antivirus guides are valid")
    })
}

pub fn matching_guide(name: &str) -> usize {
    let name = name.to_lowercase();
    guides()
        .iter()
        .enumerate()
        // Prefer the specific vendor: "Bitdefender" also contains "defender".
        .filter_map(|(index, guide)| {
            guide
                .aliases
                .iter()
                .filter(|alias| name.contains(alias.as_str()))
                .map(String::len)
                .max()
                .map(|length| (index, length))
        })
        .max_by_key(|(_, length)| *length)
        .map(|(index, _)| index)
        .unwrap_or(guides().len() - 1)
}

pub fn discovered_names() -> Vec<String> {
    let mut names = Vec::new();
    #[cfg(windows)]
    {
        use winreg::{
            RegKey,
            enums::{
                HKEY_CURRENT_USER, HKEY_LOCAL_MACHINE, KEY_READ, KEY_WOW64_32KEY, KEY_WOW64_64KEY,
            },
        };
        for hive in [HKEY_LOCAL_MACHINE, HKEY_CURRENT_USER] {
            for view in [KEY_WOW64_32KEY, KEY_WOW64_64KEY] {
                let Ok(uninstall) = RegKey::predef(hive).open_subkey_with_flags(
                    "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall",
                    KEY_READ | view,
                ) else {
                    continue;
                };
                for key in uninstall.enum_keys().filter_map(Result::ok) {
                    let Ok(entry) = uninstall.open_subkey_with_flags(key, KEY_READ) else {
                        continue;
                    };
                    let Ok(name) = entry.get_value::<String, _>("DisplayName") else {
                        continue;
                    };
                    if matching_guide(&name) != guides().len() - 1
                        && !names.iter().any(|n: &String| n.eq_ignore_ascii_case(&name))
                    {
                        names.push(name);
                    }
                }
            }
        }
    }
    names.sort();
    names
}

pub fn recommended_guide() -> usize {
    discovered_names()
        .iter()
        .map(|n| matching_guide(n))
        .find(|i| *i > 0 && *i < guides().len() - 1)
        .unwrap_or(0)
}

pub fn open_guide(index: usize) -> Result<(), String> {
    let guide = guides().get(index).ok_or("Выберите антивирус")?;
    if guide.url.is_empty() {
        return Err("Откройте справку вашего антивируса: производитель пока не выбран".into());
    }
    shell_open(&guide.url)
}

pub fn copy_paths(paths: &[PathBuf]) -> Result<(), String> {
    let paths = validate_requested_paths(paths)?;
    let text = paths
        .iter()
        .map(|p| p.display().to_string())
        .collect::<Vec<_>>()
        .join("\r\n");
    powershell(
        "Set-Clipboard -Value ([Console]::In.ReadToEnd())",
        Some(text.as_bytes()),
    )
    .map(|_| ())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn unknown_guide_cannot_open_a_link_or_copy_invalid_paths() {
        assert!(open_guide(usize::MAX).is_err());
        assert!(open_guide(guides().len() - 1).is_err());
        assert!(copy_paths(&[PathBuf::from("relative/path")]).is_err());
    }

    #[test]
    fn vendor_aliases_are_case_insensitive_and_prefer_the_specific_product() {
        for (index, guide) in guides().iter().enumerate() {
            for alias in &guide.aliases {
                assert_eq!(matching_guide(&alias.to_uppercase()), index);
            }
        }
        assert_eq!(
            guides()[matching_guide("Bitdefender Total Security")].id,
            "bitdefender"
        );
        assert_eq!(
            guides()[matching_guide("Microsoft Defender Antivirus")].id,
            "defender"
        );
    }

    #[test]
    #[cfg(windows)]
    fn installed_vendor_discovery_is_sorted_unique_and_recognized() {
        // Read-only registry discovery: no product is installed or configured.
        let names = discovered_names();
        assert!(names.windows(2).all(|pair| pair[0] < pair[1]));
        for (index, name) in names.iter().enumerate() {
            assert!(matching_guide(name) < guides().len() - 1);
            assert!(
                !names[..index]
                    .iter()
                    .any(|other| other.eq_ignore_ascii_case(name))
            );
        }
        let recommended = recommended_guide();
        assert!(recommended < guides().len() - 1);
        if names.is_empty() {
            assert_eq!(recommended, 0);
        }
    }

    #[test]
    fn all_vendor_guides_have_safe_official_links_and_known_aliases() {
        assert_eq!(guides().len(), 12);
        assert_eq!(guides()[matching_guide("ESET NOD32 Antivirus")].id, "eset");
        assert_eq!(guides()[matching_guide("unknown product")].id, "other");
        for guide in guides().iter().filter(|g| g.id != "other") {
            let url = url::Url::parse(&guide.url).unwrap();
            assert_eq!(url.scheme(), "https");
            assert!(url.username().is_empty());
            assert!(!guide.instructions.is_empty());
        }
        assert!(
            guides()
                .iter()
                .find(|g| g.id == "mcafee")
                .unwrap()
                .instructions
                .contains("не поддерживается")
        );
    }
}
