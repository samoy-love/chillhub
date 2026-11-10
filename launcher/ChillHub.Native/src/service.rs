//! Operations shared by the desktop controller and integration tests.
use crate::{
    config::{AppConfig, ConfigStore, LaunchTarget},
    launch::{self, LaunchAction, LaunchOption},
    model::GameRecord,
    net::{ApiClient, Maintenance},
    paths::AppPaths,
    playtime::PlaytimeStore,
    queue::{DownloadQueue, QueueState},
    transfer::{SyncScope, TransferControl, TransferEngine, TransferRequest},
};
use std::{
    path::{Path, PathBuf},
    sync::Arc,
};

#[derive(Clone)]
pub struct Services {
    pub paths: AppPaths,
    pub config: ConfigStore,
    pub api: ApiClient,
    pub queue: Arc<DownloadQueue>,
    pub playtime: PlaytimeStore,
    pub feedback: Arc<crate::feedback::FeedbackService>,
}
#[derive(Debug, Clone, Copy, Default)]
pub struct DownloadEstimate {
    /// Full size of changed files; block reuse can reduce the actual network traffic.
    pub bytes_to_prepare: u64,
    pub required_free_bytes: u64,
    pub available_bytes: Option<u64>,
}
impl Services {
    pub fn new(mut paths: AppPaths) -> Result<(Self, AppConfig), String> {
        let config = ConfigStore::new(paths.clone());
        let prefs = config.load()?;
        paths = prefs.apply_paths(&paths);
        let api = ApiClient::new(&prefs.api_base_url)
            .map_err(|e| e.to_string())?
            .with_diagnostics(paths.data_dir.clone());
        let queue = Arc::new(
            DownloadQueue::new(paths.data_dir.join("download-queue.json"))
                .map_err(|e| e.to_string())?,
        );
        queue.set_speed_limit(prefs.speed_limit_mbps.max(0) as u64 * 1024 * 1024);
        let playtime = PlaytimeStore::new(&paths);
        let feedback = Arc::new(crate::feedback::FeedbackService::new(
            api.clone(),
            paths.data_dir.clone(),
        ));
        Ok((
            Self {
                paths,
                config,
                api,
                queue,
                playtime,
                feedback,
            },
            prefs,
        ))
    }
    pub fn option(&self, game: &GameRecord, target: LaunchTarget) -> Result<LaunchOption, String> {
        launch::options(game, &self.paths)?
            .into_iter()
            .find(|o| o.target == target)
            .ok_or("Этот вариант игры недоступен".into())
    }
    pub fn verify_game_status(&self, game: &GameRecord) -> Result<bool, String> {
        let root = self.paths.game_root(&game.game_id)?;
        if self.queue.has_pending_for_root(&root) {
            return Ok(false);
        }
        crate::integrity::check(game, &self.paths, &self.api)
    }
    pub fn maintenance_check(&self, action: &LaunchAction) -> Result<(), String> {
        // Installed games remain playable during a network outage. A fresh positive block
        // is authoritative; network failures never masquerade as a maintenance response.
        match self.api.maintenance() {
            Ok(m) if blocked(&m, action) => Err(if m.message.is_empty() {
                "На сервере идут работы. Попробуйте позже".into()
            } else {
                m.message
            }),
            Err(e) if *action != LaunchAction::Play => Err(e.to_string()),
            _ => Ok(()),
        }
    }
    pub fn enqueue_game(
        &self,
        game: &GameRecord,
        target: LaunchTarget,
        version: Option<&str>,
        force: bool,
        mods_only: bool,
    ) -> Result<Vec<String>, String> {
        let option = self.option(game, target)?;
        if target.via_steam() && option.game_dir.as_os_str().is_empty() {
            return Err(option.note);
        }
        let root = if target.via_steam() {
            option.game_dir.clone()
        } else {
            self.paths.game_root(&game.game_id)?
        };
        if self.queue.has_pending_for_root(&root) {
            return Err(
                "Для этой папки уже есть загрузка. Завершите или отмените её в очереди".into(),
            );
        }
        if root.exists() && !crate::playtime::processes_in(&root)?.is_empty() {
            return Err("Закройте игру перед изменением файлов".into());
        }
        let needs_base = !target.via_steam() && !mods_only;
        let needs_mods = target.modded() || mods_only;
        if force {
            if mods_only && !can_verify_mods(&root) {
                return Err("Модпак ещё не установлен. Сначала установите его".into());
            }
            if needs_base && !can_verify_game(game, &root) {
                return Err("Игра ещё не установлена. Сначала установите её".into());
            }
        }
        if !needs_base && !needs_mods {
            return Err("Файлы этой игры обновляются через Steam".into());
        }
        // Validate every part before publishing any queue task: a missing modpack
        // must not leave an unexpected base-game installation running.
        let mods = if needs_mods {
            Some(
                game.mods
                    .as_ref()
                    .filter(|m| m.has_latest)
                    .ok_or("Модпак ещё не опубликован")?,
            )
        } else {
            None
        };
        if needs_base {
            let action = if launch::read_marker(&root, ".version").is_empty() {
                LaunchAction::InstallGame
            } else {
                LaunchAction::UpdateGame
            };
            self.maintenance_check(&action)?;
        }
        if needs_mods {
            let action = if launch::read_marker(&root, ".mods.version").is_empty() {
                LaunchAction::InstallMods
            } else if force {
                LaunchAction::RepairMods
            } else {
                LaunchAction::UpdateMods
            };
            self.maintenance_check(&action)?;
        }
        let prefs = self.config.load()?;
        let owned = match launch::installed_modpack_paths(&root) {
            Ok(paths) => paths,
            // A broken ownership record is repairable, but cannot authorize deletions.
            // The verified published manifest replaces it only after successful commit.
            Err(_) if needs_mods => Vec::new(),
            Err(error) => return Err(error),
        }
        .into_iter()
        .map(|p| p.to_string_lossy().replace('\\', "/"))
        .collect::<Vec<_>>();
        let mut foreign = owned.clone();
        if needs_base && let Some(mods) = mods {
            // Older releases bundled the modpack with the base game and have no ownership
            // marker. Protect the published exact paths before base cleanup, then let the
            // dependent modpack job verify/adopt the existing bytes and publish its markers.
            let url = self
                .api
                .resolve(&mods.manifest_url)
                .map_err(|e| e.to_string())?;
            let manifest = TransferEngine::new()
                .map_err(|e| e.to_string())?
                .fetch_manifest(url.as_str(), &TransferControl::default())
                .map_err(|e| e.to_string())?;
            if (!manifest.game_id.is_empty()
                && !manifest.game_id.eq_ignore_ascii_case(&game.game_id))
                || (!manifest.version.is_empty() && manifest.version != mods.version)
            {
                return Err("Список файлов модпака не соответствует выбранной игре".into());
            }
            foreign.extend(manifest.files.into_iter().map(|file| file.path));
            foreign.sort_by_key(|path| path.to_lowercase());
            foreign.dedup_by(|a, b| a.eq_ignore_ascii_case(b));
        }
        let mut planned = Vec::new();
        if needs_base {
            let selected = version
                .or_else(|| prefs.selected_version(&game.game_id))
                .unwrap_or(&game.latest_version);
            if !crate::config::valid_version(selected) {
                return Err("Не указана доступная версия игры".into());
            }
            if version.is_some()
                && !self
                    .api
                    .builds(&game.game_id)
                    .map_err(|e| e.to_string())?
                    .iter()
                    .any(|v| v == selected)
            {
                return Err("Эта версия больше не опубликована".into());
            }
            let manifest = if selected == game.latest_version {
                self.api.resolve(&game.manifest_url)
            } else {
                self.api.game_manifest_url(&game.game_id, selected)
            }
            .map_err(|e| e.to_string())?;
            let req = TransferRequest {
                game_id: game.game_id.clone(),
                version: selected.into(),
                root: root.clone(),
                manifest_url: manifest.to_string(),
                content_base_url: self
                    .api
                    .game_content_base(&game.game_id, selected)
                    .map_err(|e| e.to_string())?
                    .to_string(),
                scope: SyncScope::WholeRoot,
                foreign_paths: foreign,
                force_rehash: force,
                max_parallel: prefs.download_threads as usize,
                bytes_per_second: prefs.speed_limit_mbps.max(0) as u64 * 1024 * 1024,
                ..Default::default()
            };
            planned.push(req);
        }
        if let Some(mods) = mods {
            let donor = if target.via_steam() {
                self.paths.game_root(&game.game_id).ok()
            } else {
                Some(launch::locate_steam(&mods.steam_app_id, &mods.steam_folder).game_dir)
            };
            let preserve_paths = if launch::doorstop_config_valid(&root) {
                vec!["doorstop_config.ini".into()]
            } else {
                vec![]
            };
            let req = TransferRequest {
                game_id: game.game_id.clone(),
                version: mods.version.clone(),
                root,
                manifest_url: self
                    .api
                    .resolve(&mods.manifest_url)
                    .map_err(|e| e.to_string())?
                    .to_string(),
                content_base_url: self
                    .api
                    .resolve(&mods.content_base_url)
                    .map_err(|e| e.to_string())?
                    .to_string(),
                scope: SyncScope::OwnFilesOnly,
                previous_owned_paths: owned,
                preserve_paths,
                donor_roots: donor.into_iter().filter(|p| p.is_dir()).collect(),
                force_rehash: force,
                max_parallel: prefs.download_threads as usize,
                bytes_per_second: prefs.speed_limit_mbps.max(0) as u64 * 1024 * 1024,
                ..Default::default()
            };
            planned.push(req);
        }
        let mut requests = Vec::new();
        let mut previous = None;
        for mut request in planned {
            request.depends_on = previous;
            let id = self.queue.enqueue(request).map_err(|e| e.to_string())?;
            previous = Some(id.clone());
            requests.push(id);
        }
        if needs_base && let Some(selected) = version {
            self.config
                .update(|cfg| cfg.select_version(&game.game_id, selected, &game.latest_version))?;
        }
        Ok(requests)
    }
    /// Read-only preparation for the space hint. Call from a background worker.
    /// It never creates game directories, version markers, or queue jobs.
    pub fn estimate_download(
        &self,
        game: &GameRecord,
        target: LaunchTarget,
    ) -> Result<DownloadEstimate, String> {
        let option = self.option(game, target)?;
        let root = option.game_dir;
        if root.as_os_str().is_empty() || target == LaunchTarget::SteamVanilla {
            return Err("Размер установки недоступен".into());
        }
        if self.queue.has_pending_for_root(&root) {
            return Err("Размер уточняется после текущей загрузки".into());
        }
        let prefs = self.config.load()?;
        let engine = TransferEngine::new()
            .map_err(|e| e.to_string())?
            .with_hash_cache_dir(self.paths.data_dir.join("hashcache"));
        let control = TransferControl::default();
        let mut requests = Vec::new();
        let owned = launch::installed_modpack_paths(&root)
            .unwrap_or_default()
            .into_iter()
            .map(|p| p.to_string_lossy().replace('\\', "/"))
            .collect::<Vec<_>>();
        let mut foreign = owned.clone();
        if target.modded() {
            let mods = game
                .mods
                .as_ref()
                .filter(|m| m.has_latest)
                .ok_or("Модпак ещё не опубликован")?;
            let manifest_url = self
                .api
                .resolve(&mods.manifest_url)
                .map_err(|e| e.to_string())?
                .to_string();
            let manifest = engine
                .fetch_manifest(&manifest_url, &control)
                .map_err(|e| e.to_string())?;
            foreign.extend(manifest.files.iter().map(|file| file.path.clone()));
            foreign.sort_by_key(|path| path.to_lowercase());
            foreign.dedup_by(|a, b| a.eq_ignore_ascii_case(b));
            requests.push((
                TransferRequest {
                    game_id: game.game_id.clone(),
                    version: mods.version.clone(),
                    root: root.clone(),
                    manifest_url,
                    content_base_url: self
                        .api
                        .resolve(&mods.content_base_url)
                        .map_err(|e| e.to_string())?
                        .to_string(),
                    scope: SyncScope::OwnFilesOnly,
                    previous_owned_paths: owned,
                    preserve_paths: if launch::doorstop_config_valid(&root) {
                        vec!["doorstop_config.ini".into()]
                    } else {
                        vec![]
                    },
                    max_parallel: prefs.download_threads as usize,
                    ..Default::default()
                },
                manifest,
            ));
        }
        if !target.via_steam() {
            let version = prefs
                .selected_version(&game.game_id)
                .unwrap_or(&game.latest_version);
            let url = if version == game.latest_version {
                self.api.resolve(&game.manifest_url)
            } else {
                self.api.game_manifest_url(&game.game_id, version)
            }
            .map_err(|e| e.to_string())?;
            let manifest = engine
                .fetch_manifest(url.as_str(), &control)
                .map_err(|e| e.to_string())?;
            requests.push((
                TransferRequest {
                    game_id: game.game_id.clone(),
                    version: version.into(),
                    root: root.clone(),
                    manifest_url: url.to_string(),
                    content_base_url: self
                        .api
                        .game_content_base(&game.game_id, version)
                        .map_err(|e| e.to_string())?
                        .to_string(),
                    scope: SyncScope::WholeRoot,
                    foreign_paths: foreign,
                    max_parallel: prefs.download_threads as usize,
                    ..Default::default()
                },
                manifest,
            ));
        }
        let mut estimate = DownloadEstimate::default();
        for (request, manifest) in requests {
            let plan = engine
                .plan_manifest(&request, manifest, &control, &|_| {})
                .map_err(|e| e.to_string())?;
            estimate.bytes_to_prepare = estimate
                .bytes_to_prepare
                .saturating_add(plan.total_download_bytes);
            // A conservative sum also covers sequential base + modpack installations.
            estimate.required_free_bytes = estimate
                .required_free_bytes
                .saturating_add(plan.required_free_bytes);
        }
        estimate.available_bytes = root
            .ancestors()
            .find(|path| path.is_dir())
            .and_then(|path| fs2::available_space(path).ok());
        Ok(estimate)
    }
    pub fn idle(&self) -> bool {
        !self.queue.snapshot().iter().any(|q| {
            matches!(
                q.state,
                QueueState::Running
                    | QueueState::Waiting
                    | QueueState::Pausing
                    | QueueState::Cancelling
            )
        })
    }
    pub fn delete_local(&self, game: &GameRecord) -> Result<(), String> {
        let root = self.paths.game_root(&game.game_id)?;
        if !root.exists() {
            return Ok(());
        }
        if self.queue.has_pending_for_root(&root) {
            return Err("Сначала отмените загрузку этой игры".into());
        }
        if !crate::playtime::processes_in(&root)?.is_empty() {
            return Err("Закройте игру перед удалением".into());
        }
        delete_game_directory(&self.paths.games_dir, &root, &game.game_id)?;
        if let Err(error) = crate::shortcuts::remove_for_game(&self.paths, &game.game_id) {
            crate::diagnostics::log(&self.paths.data_dir, "shortcut.remove", &error);
        }
        Ok(())
    }
}

pub fn can_verify_game(game: &GameRecord, root: &Path) -> bool {
    !launch::read_marker(root, ".version").is_empty()
        || launch::resolve_game_exe(
            root,
            &game.exe_relative_path,
            &game
                .mods
                .as_ref()
                .map(|m| m.exe_names.clone())
                .unwrap_or_default(),
        )
        .is_ok()
}
pub fn can_verify_mods(root: &Path) -> bool {
    !launch::read_marker(root, ".mods.version").is_empty()
        || root.join(".mods.manifest.json").is_file()
}

/// The library guard prevents ancestor replacement; the root handle is opened
/// with DELETE permission and denies other renames. We rename through that same
/// handle, since a DirectoryGuard of the root would block our own path rename.
fn delete_game_directory(library: &Path, root: &Path, game_id: &str) -> Result<(), String> {
    crate::paths::validate_game_id(game_id)?;
    let _library_guard =
        crate::manifest::DirectoryGuard::acquire(library, false).map_err(|e| e.to_string())?;
    crate::manifest::check_no_links(root).map_err(|e| e.to_string())?;
    let base = std::fs::canonicalize(library).map_err(|e| e.to_string())?;
    let actual = std::fs::canonicalize(root).map_err(|e| e.to_string())?;
    if actual.parent() != Some(base.as_path())
        || root.file_name() != Some(std::ffi::OsStr::new(game_id))
    {
        return Err("Нельзя удалить папку вне библиотеки Chill Hub".into());
    }
    let mut options = std::fs::OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::*;
        options
            .access_mode(DELETE | FILE_READ_ATTRIBUTES)
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS);
    }
    let directory = options
        .open(&actual)
        .map_err(|e| format!("Папка занята или недоступна: {e}"))?;
    let metadata = directory.metadata().map_err(|e| e.to_string())?;
    if !metadata.is_dir() || crate::manifest::is_reparse(&metadata) {
        return Err("Удаление ссылки запрещено".into());
    }
    let mut lock_options = std::fs::OpenOptions::new();
    lock_options
        .read(true)
        .write(true)
        .create(true)
        .truncate(false);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::*;
        // The byte-range lock stays exclusive, but its handle permits our parent
        // directory rename. OPEN_REPARSE_POINT prevents following a planted link.
        lock_options
            .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
            .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT);
    }
    let lock = lock_options
        .open(actual.join(".chillhub.lock"))
        .map_err(|e| e.to_string())?;
    let lock_metadata = lock.metadata().map_err(|e| e.to_string())?;
    if !lock_metadata.is_file() || crate::manifest::is_reparse(&lock_metadata) {
        return Err("Недопустимый файл блокировки".into());
    }
    fs2::FileExt::try_lock_exclusive(&lock).map_err(|_| "Папка игры занята")?;
    let trash = base.join(format!(".removed-{game_id}-{}", uuid::Uuid::new_v4()));
    // Windows rejects renaming a directory with an open descendant. Transfer
    // exclusion is now held by `directory`: its DELETE access is incompatible
    // with DirectoryGuard's no-delete sharing, so no new transfer can enter.
    #[cfg(windows)]
    drop(lock);
    #[cfg(windows)]
    {
        use std::os::windows::{ffi::OsStrExt, io::AsRawHandle};
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_RENAME_INFO, FileRenameInfo, SetFileInformationByHandle,
        };
        let name: Vec<u16> = trash.as_os_str().encode_wide().collect();
        let offset = std::mem::offset_of!(FILE_RENAME_INFO, FileName);
        let size = offset + name.len() * 2;
        // usize storage provides the alignment required by FILE_RENAME_INFO.
        let mut storage = vec![0usize; size.div_ceil(std::mem::size_of::<usize>())];
        let info = storage.as_mut_ptr().cast::<FILE_RENAME_INFO>();
        unsafe {
            (*info).Anonymous.ReplaceIfExists = false;
            (*info).RootDirectory = std::ptr::null_mut();
            (*info).FileNameLength = (name.len() * 2) as u32;
            std::ptr::copy_nonoverlapping(
                name.as_ptr(),
                storage.as_mut_ptr().cast::<u8>().add(offset).cast::<u16>(),
                name.len(),
            );
            if SetFileInformationByHandle(
                directory.as_raw_handle(),
                FileRenameInfo,
                info.cast(),
                size as u32,
            ) == 0
            {
                return Err(format!(
                    "Не удалось переместить проверенную папку: {}",
                    std::io::Error::last_os_error()
                ));
            }
        }
    }
    #[cfg(not(windows))]
    std::fs::rename(&actual, &trash).map_err(|e| e.to_string())?;
    // The exclusive transfer lock survives until the verified directory has left
    // the playable namespace. Close both handles before removing the tombstone.
    #[cfg(not(windows))]
    drop(lock);
    drop(directory);
    crate::manifest::check_no_links(&trash).map_err(|e| e.to_string())?;
    std::fs::remove_dir_all(&trash).map_err(|e| {
        format!(
            "Игра отключена, но не все файлы удалось удалить. Остатки находятся в {}: {e}",
            trash.display()
        )
    })
}
pub fn blocked(state: &Maintenance, action: &LaunchAction) -> bool {
    state.enabled
        && match action {
            LaunchAction::Play => state.blocks.launch,
            LaunchAction::InstallGame => state.blocks.install,
            LaunchAction::InstallMods
            | LaunchAction::UpdateGame
            | LaunchAction::UpdateMods
            | LaunchAction::RepairMods => state.blocks.update,
            LaunchAction::Unavailable => false,
        }
}
pub fn open_directory(path: &Path) -> Result<(), String> {
    if !path.is_dir() {
        return Err("Папка пока не создана".into());
    }
    std::process::Command::new("explorer.exe")
        .arg(path)
        .spawn()
        .map(|_| ())
        .map_err(|e| e.to_string())
}
pub fn desktop_path() -> Result<PathBuf, String> {
    #[cfg(windows)]
    {
        let key = winreg::RegKey::predef(winreg::enums::HKEY_CURRENT_USER)
            .open_subkey(r"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders")
            .map_err(|e| e.to_string())?;
        let path: String = key.get_value("Desktop").map_err(|e| e.to_string())?;
        Ok(path.into())
    }
    #[cfg(not(windows))]
    {
        Err("Ярлыки поддерживаются в Windows".into())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn maintenance_blocks_only_named_operations() {
        let m = Maintenance {
            enabled: true,
            blocks: crate::net::MaintenanceBlocks {
                install: true,
                update: false,
                launch: false,
            },
            ..Default::default()
        };
        assert!(blocked(&m, &LaunchAction::InstallGame));
        assert!(!blocked(&m, &LaunchAction::Play));
        assert!(!blocked(&m, &LaunchAction::UpdateMods));
        assert!(!blocked(&m, &LaunchAction::InstallMods));
        let updating = Maintenance {
            enabled: true,
            blocks: crate::net::MaintenanceBlocks {
                install: false,
                update: true,
                launch: false,
            },
            ..Default::default()
        };
        assert!(blocked(&updating, &LaunchAction::InstallMods));
        let off = Maintenance {
            enabled: false,
            ..m
        };
        assert!(!blocked(&off, &LaunchAction::InstallGame));
    }
    #[test]
    fn delete_removes_only_the_confirmed_game() {
        let temp = tempfile::tempdir().unwrap();
        let library = temp.path().join("games");
        let game = library.join("peak");
        let other = library.join("repo");
        std::fs::create_dir_all(&game).unwrap();
        std::fs::create_dir_all(&other).unwrap();
        std::fs::write(game.join("game.exe"), b"game").unwrap();
        std::fs::write(other.join("keep"), b"other").unwrap();
        delete_game_directory(&library, &game, "peak").unwrap();
        assert!(!game.exists());
        assert_eq!(std::fs::read(other.join("keep")).unwrap(), b"other");
    }
    #[test]
    fn delete_removes_readonly_game_files() {
        let temp = tempfile::tempdir().unwrap();
        let library = temp.path().join("games");
        let game = library.join("peak");
        std::fs::create_dir_all(&game).unwrap();
        let file = game.join("game.exe");
        std::fs::write(&file, b"game").unwrap();
        let mut permissions = std::fs::metadata(&file).unwrap().permissions();
        permissions.set_readonly(true);
        std::fs::set_permissions(&file, permissions).unwrap();
        delete_game_directory(&library, &game, "peak").unwrap();
        assert!(!game.exists());
        assert_eq!(std::fs::read_dir(&library).unwrap().count(), 0);
    }
    #[test]
    fn delete_refuses_an_active_transfer_and_outside_root() {
        let temp = tempfile::tempdir().unwrap();
        let library = temp.path().join("games");
        let game = library.join("peak");
        std::fs::create_dir_all(&game).unwrap();
        let lock =
            crate::manifest::open_regular(&game.join(".chillhub.lock"), true, true, false).unwrap();
        fs2::FileExt::try_lock_exclusive(&lock).unwrap();
        assert!(delete_game_directory(&library, &game, "peak").is_err());
        assert!(game.exists());
        drop(lock);
        let outside = temp.path().join("outside");
        std::fs::create_dir_all(&outside).unwrap();
        assert!(delete_game_directory(&library, &outside, "outside").is_err());
        assert!(outside.exists());
    }

    #[cfg(windows)]
    #[test]
    fn paused_alias_task_blocks_delete_enqueue_estimate_and_integrity_checks() {
        let temp = tempfile::tempdir().unwrap();
        let mut paths = AppPaths::isolated(temp.path());
        let library = temp.path().join("Long game library directory");
        let root = library.join("fixture");
        std::fs::create_dir_all(&root).unwrap();
        std::fs::create_dir_all(&paths.data_dir).unwrap();
        std::fs::write(root.join("Game.exe"), b"inert fixture, not an executable").unwrap();
        paths.games_dir = std::fs::canonicalize(&library).unwrap();
        let prefs = AppConfig {
            games_path: paths.games_dir.to_string_lossy().into_owned(),
            ..Default::default()
        };
        ConfigStore::new(paths.clone()).save(&prefs).unwrap();
        let item = crate::queue::QueueItem {
            id: "paused".into(),
            request: TransferRequest {
                game_id: "fixture".into(),
                root: crate::paths::short_test_path(&library).join("fixture"),
                ..Default::default()
            },
            state: QueueState::Paused,
            progress: None,
            outcome: None,
            error: None,
            cleanup_paths: vec![],
            report: Default::default(),
        };
        std::fs::write(
            paths.data_dir.join("download-queue.json"),
            serde_json::to_vec(&vec![item]).unwrap(),
        )
        .unwrap();
        let (services, _) = Services::new(paths).unwrap();
        let game = GameRecord {
            game_id: "fixture".into(),
            has_latest: true,
            latest_version: "1.0.0".into(),
            exe_relative_path: "Game.exe".into(),
            ..Default::default()
        };
        assert!(
            services
                .delete_local(&game)
                .unwrap_err()
                .contains("отмените")
        );
        assert!(
            services
                .enqueue_game(&game, LaunchTarget::LocalVanilla, None, false, false)
                .unwrap_err()
                .contains("уже есть загрузка")
        );
        assert!(
            services
                .estimate_download(&game, LaunchTarget::LocalVanilla)
                .unwrap_err()
                .contains("после текущей загрузки")
        );
        assert!(!services.verify_game_status(&game).unwrap());
        assert!(root.join("Game.exe").is_file());
        assert_eq!(services.queue.snapshot()[0].state, QueueState::Paused);
        services.queue.shutdown();
    }
}
