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

const SECURITY_CENTER_PRODUCTS: &str = r#"
$names = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntivirusProduct -OperationTimeoutSec 4 | ForEach-Object { $_.displayName })
ConvertTo-Json -InputObject $names -Compress
"#;

fn security_center_names() -> Vec<String> {
    powershell(SECURITY_CENTER_PRODUCTS, None)
        .ok()
        .and_then(|text| serde_json::from_str::<Vec<Option<String>>>(&text).ok())
        .unwrap_or_default()
        .into_iter()
        .flatten()
        .collect()
}

fn uninstall_registry_names() -> Vec<String> {
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
                    let Ok(entry) = uninstall.open_subkey_with_flags(key, KEY_READ | view) else {
                        continue;
                    };
                    let Ok(name) = entry.get_value::<String, _>("DisplayName") else {
                        continue;
                    };
                    names.push(name);
                }
            }
        }
    }
    names
}

fn discovered_names(center: Vec<String>, uninstall: Vec<String>) -> Vec<String> {
    let other = guides().len() - 1;
    let mut names = std::collections::BTreeMap::new();
    for candidate in center.into_iter().chain(
        uninstall
            .into_iter()
            .filter(|name| matching_guide(name) != other),
    ) {
        let name = candidate.trim();
        if !name.is_empty() {
            names
                .entry(name.to_lowercase())
                .or_insert_with(|| name.to_owned());
        }
    }
    names.into_values().collect()
}

fn recommended_guide_from(names: &[String]) -> usize {
    let other = guides().len() - 1;
    let matches: Vec<_> = names.iter().map(|name| matching_guide(name)).collect();
    if let Some(vendor) = matches
        .iter()
        .copied()
        .find(|index| *index > 0 && *index < other)
    {
        return vendor;
    }
    if matches.contains(&other) || !matches.contains(&0) {
        return other;
    }
    0
}

pub fn recommended_guide() -> usize {
    recommended_guide_from(&discovered_names(
        security_center_names(),
        uninstall_registry_names(),
    ))
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
#[path = "security_guides_tests.rs"]
mod tests;
