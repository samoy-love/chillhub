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
    let normalized = |p: &Path| {
        p.to_string_lossy()
            .trim_end_matches(['\\', '/'])
            .replace('/', "\\")
            .to_lowercase()
    };
    let folder = normalized(folder);
    for protected in [paths.data_dir.as_path(), install] {
        let protected = normalized(protected);
        if folder == protected
            || folder.starts_with(&format!("{protected}\\"))
            || protected.starts_with(&format!("{folder}\\"))
        {
            return Err("Папки игр, настроек и лаунчера должны быть отдельными".into());
        }
    }
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn only_marked_unpublished_games_are_offered_for_removal() {
        let temp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(temp.path());
        for name in ["known", "old", "personal"] {
            fs::create_dir_all(paths.games_dir.join(name)).unwrap();
        }
        for name in ["known", "old"] {
            fs::write(paths.games_dir.join(name).join(".version"), "1").unwrap();
        }
        let found = orphaned(
            &paths,
            &[GameRecord {
                game_id: "known".into(),
                ..Default::default()
            }],
        )
        .unwrap();
        assert_eq!(
            found.iter().map(|g| g.id.as_str()).collect::<Vec<_>>(),
            vec!["old"]
        );
    }
    #[test]
    fn game_root_cannot_contain_launcher_or_user_state() {
        let temp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(temp.path());
        let install = temp.path().join("install");
        assert!(validate_games_location(&paths.games_dir, &paths, &install).is_ok());
        assert!(validate_games_location(&paths.data_dir, &paths, &install).is_err());
        assert!(validate_games_location(temp.path(), &paths, &install).is_err());
        assert!(validate_games_location(&install.join("games"), &paths, &install).is_err());
    }
}
