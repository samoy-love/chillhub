use crate::{model::GameRecord, paths::AppPaths};
use std::{
    collections::HashSet,
    fs,
    path::{Path, PathBuf},
};
#[derive(Clone, Debug)]
pub struct Orphan {
    pub id: String,
    pub path: PathBuf,
}
pub fn orphaned(paths: &AppPaths, published: &[GameRecord]) -> Result<Vec<Orphan>, String> {
    if !paths.games_dir.exists() {
        return Ok(vec![]);
    }
    crate::manifest::check_no_links(&paths.games_dir).map_err(|e| e.to_string())?;
    let known = published
        .iter()
        .map(|g| g.game_id.to_ascii_lowercase())
        .collect::<HashSet<_>>();
    let mut result = Vec::new();
    for entry in fs::read_dir(&paths.games_dir)
        .map_err(|e| e.to_string())?
        .take(2048)
    {
        let entry = entry.map_err(|e| e.to_string())?;
        let name = entry.file_name().to_string_lossy().into_owned();
        if known.contains(&name.to_ascii_lowercase())
            || crate::paths::validate_game_id(&name).is_err()
        {
            continue;
        }
        let folder = entry.path();
        if !entry.file_type().is_ok_and(|t| t.is_dir())
            || crate::manifest::check_no_links(&folder).is_err()
        {
            continue;
        }
        // Never offer deletion of an arbitrary directory merely because its name looks like an id.
        if folder.join(".version").is_file()
            || folder.join(".mods.version").is_file()
            || folder.join(".updating").is_file()
        {
            result.push(Orphan {
                id: name,
                path: folder,
            });
        }
    }
    result.sort_by(|a, b| a.id.cmp(&b.id));
    Ok(result)
}
pub fn validate_games_location(
    folder: &Path,
    paths: &AppPaths,
    install: &Path,
) -> Result<(), String> {
    if !folder.is_absolute() || folder.parent().is_none() {
        return Err("Выберите отдельную папку для игр, например D:\\Games\\ChillHub".into());
    }
    crate::manifest::check_no_links(folder).map_err(|e| e.to_string())?;
    let folder = location_identity(folder)?;
    for protected in [paths.data_dir.as_path(), install] {
        let protected = location_identity(protected)?;
        if folder == protected
            || folder.starts_with(&format!("{protected}\\"))
            || protected.starts_with(&format!("{folder}\\"))
        {
            return Err("Папки игр, настроек и лаунчера должны быть отдельными".into());
        }
    }
    Ok(())
}

pub(crate) fn location_identity(path: &Path) -> Result<String, String> {
    if let Some(identity) = crate::paths::path_identity(path) {
        return Ok(identity);
    }
    // Settings may name an unplugged/nonexistent drive. It cannot alias a live
    // directory on another drive; do not extend this fallback to access denied,
    // UNC aliases or an unresolved ancestor on a mounted volume.
    #[cfg(windows)]
    {
        use std::path::{Component, Prefix};
        let mut parts = path.components();
        if let Some(Component::Prefix(prefix)) = parts.next()
            && let Prefix::Disk(drive) | Prefix::VerbatimDisk(drive) = prefix.kind()
            && matches!(parts.next(), Some(Component::RootDir))
            && parts
                .clone()
                .all(|part| matches!(part, Component::Normal(_)))
        {
            let root = PathBuf::from(format!("{}:\\", drive as char));
            if fs::metadata(root).is_err_and(|e| {
                e.kind() == std::io::ErrorKind::NotFound
                    || matches!(e.raw_os_error(), Some(21 | 1167))
            }) {
                let suffix = parts
                    .map(|part| part.as_os_str().to_string_lossy().into_owned())
                    .collect::<Vec<_>>()
                    .join("\\");
                return Ok(format!("{}:\\{suffix}", drive as char)
                    .trim_end_matches('\\')
                    .to_lowercase());
            }
        }
    }
    Err("Не удалось проверить расположение папки. Проверьте доступ к диску".into())
}
#[cfg(test)]
#[path = "library_tests.rs"]
mod tests;
