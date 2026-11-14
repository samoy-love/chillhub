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

    #[cfg(windows)]
    #[test]
    fn aliases_cannot_place_games_inside_install_or_settings_even_before_creation() {
        let temp = tempfile::tempdir().unwrap();
        let parent = temp.path().join("Long shared application directory");
        fs::create_dir(&parent).unwrap();
        let short = crate::paths::short_test_path(&parent);
        let long = fs::canonicalize(&parent).unwrap();
        let paths = AppPaths {
            data_dir: long.join("Settings still missing"),
            legacy_dir: None,
            games_dir: long.join("Separate games"),
        };
        let install = long.join("Launcher still missing");
        for folder in [
            short.clone(),
            short.join("Settings still missing"),
            short.join("Settings still missing/Games"),
            short.join("Launcher still missing/Games"),
        ] {
            assert!(validate_games_location(&folder, &paths, &install).is_err());
        }
        assert!(validate_games_location(&short.join("Separate games"), &paths, &install).is_ok());
        assert!(!paths.data_dir.exists());
        assert!(!install.exists());
    }

    #[cfg(windows)]
    #[test]
    fn unavailable_drive_setting_does_not_create_directories() {
        let temp = tempfile::tempdir().unwrap();
        let paths = AppPaths::isolated(temp.path());
        let drive = (b'D'..=b'Z').rev().find(|drive| {
            fs::metadata(format!("{}:\\", *drive as char))
                .is_err_and(|e| e.kind() == std::io::ErrorKind::NotFound)
        });
        if let Some(drive) = drive {
            let folder = PathBuf::from(format!("{}:\\ChillHub Alias Test\\Games", drive as char));
            let install = temp.path().join("install");
            fs::create_dir(&install).unwrap();
            assert!(validate_games_location(&folder, &paths, &install).is_ok());
            let update = crate::update::UpdatePaths {
                install,
                config: paths.data_dir.clone(),
                games: folder.clone(),
                staging: temp.path().join("staging"),
            };
            assert!(
                update.validate().is_ok(),
                "a disconnected games disk must not block a launcher update"
            );
            let mut invalid = update.clone();
            invalid.install = folder.clone();
            assert!(invalid.validate().is_err());
            invalid = update.clone();
            invalid.config = folder.clone();
            assert!(invalid.validate().is_err());
            invalid = update;
            invalid.staging = folder.clone();
            assert!(invalid.validate().is_err());
            assert!(!folder.exists());
        }
    }
}
