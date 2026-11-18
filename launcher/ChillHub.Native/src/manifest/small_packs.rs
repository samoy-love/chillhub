use serde::{Deserialize, Deserializer, Serialize};

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SmallPack {
    pub sha256: String,
    pub files: Vec<usize>,
}

pub(super) fn deserialize<'de, D: Deserializer<'de>>(
    deserializer: D,
) -> Result<Vec<SmallPack>, D::Error> {
    // Pack metadata is advisory. An old or malformed publisher may still
    // provide a perfectly usable list of individual files.
    let value = serde_json::Value::deserialize(deserializer)?;
    let serde_json::Value::Array(packs) = value else {
        return Ok(Vec::new());
    };
    Ok(packs
        .into_iter()
        .filter_map(|pack| serde_json::from_value(pack).ok())
        .collect())
}
