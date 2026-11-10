//! Self-update is staged outside the installation and committed by a separate process.
//! These functions never apply an update merely because a newer version was found.
use crate::manifest::{
    DirectoryGuard, Manifest, ManifestFile, check_no_links, safe_join, validate_manifest,
};
use crate::paths::atomic_write;
use crate::transfer::{SyncScope, TransferControl, TransferEngine, TransferEvent, TransferRequest};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::{
    collections::HashSet,
    fs,
    io::Read,
    path::{Component, Path, PathBuf},
    process::{Child, Command},
    time::{Duration, Instant},
};

#[path = "update_maintenance.rs"]
mod maintenance;
pub use maintenance::{
    IntegrityReport, cleanup_completed_sessions, ensure_update_allowed, update_attempt_count,
    verify_installed_release,
};

pub const PRESERVED: &[&str] = &[
    "config.json",
    "launcher.version",
    "launcher.update-status",
    "Uninstall.exe",
];
pub const UPDATER_NAME: &str = "ChillHub.Updater.exe";
pub const LAUNCHER_NAME: &str = "ChillHub.exe";

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct UpdatePaths {
    pub install: PathBuf,
    pub config: PathBuf,
    pub games: PathBuf,
    pub staging: PathBuf,
}

impl UpdatePaths {
    pub fn validate(&self) -> Result<(), String> {
        for path in [&self.install, &self.config, &self.games, &self.staging] {
            if !path.is_absolute()
                || path.parent().is_none()
                || path
                    .components()
                    .any(|c| matches!(c, Component::ParentDir | Component::CurDir))
            {
                return Err("Для обновления нужны отдельные абсолютные пути".into());
            }
            check_no_links(path).map_err(|e| e.to_string())?;
        }
        // Games are only excluded from update targets. An unplugged local games
        // drive must not block repairing the launcher on an available drive.
        let all = [
            path_key(&self.install)?,
            path_key(&self.config)?,
            crate::library::location_identity(&self.games)?,
            path_key(&self.staging)?,
        ];
        for (i, a) in all.iter().enumerate() {
            for b in &all[i + 1..] {
                if a == b || a.starts_with(&format!("{b}\\")) || b.starts_with(&format!("{a}\\")) {
                    return Err("Установка, настройки, игры и временный пакет должны находиться в разных папках".into());
                }
            }
        }
        if !self.install.is_dir() {
            return Err("Папка установленного лаунчера не найдена".into());
        }
        Ok(())
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct UpdateRelease {
    pub version: String,
    pub manifest_url: String,
    pub content_base_url: String,
}

#[derive(Clone, Debug)]
pub enum UpdateCheck {
    Current { current: String, remote: String },
    Available(UpdateRelease),
}

pub fn version_parts(value: &str) -> Result<[u32; 4], String> {
    let parts: Vec<_> = value.split('.').collect();
    if !(3..=4).contains(&parts.len()) {
        return Err("Некорректный номер версии".into());
    }
    let mut parsed = [0; 4];
    for (i, part) in parts.iter().enumerate() {
        if part.is_empty() || part.len() > 9 || !part.bytes().all(|b| b.is_ascii_digit()) {
            return Err("Некорректный номер версии".into());
        }
        parsed[i] = part.parse().map_err(|_| "Некорректный номер версии")?;
    }
    Ok(parsed)
}

/// Only the configured production origin can distribute executable updates.
pub fn trusted_url(raw: &str) -> Result<url::Url, String> {
    let url = url::Url::parse(raw).map_err(|_| "Некорректный адрес обновления")?;
    if url.scheme() != "https"
        || url.host_str() != Some("launcher.samoy.love")
        || url.port_or_known_default() != Some(443)
        || !url.username().is_empty()
        || url.password().is_some()
        || url.fragment().is_some()
        || url.query().is_some()
    {
        return Err("Обновление разрешено только с сервера Chill Hub".into());
    }
    Ok(url)
}

pub fn check_release(
    client: &reqwest::blocking::Client,
    api: &str,
    current: &str,
) -> Result<UpdateCheck, String> {
    let current_parts = version_parts(current)?;
    let base = api.trim_end_matches('/');
    trusted_url(base)?;
    let response = client
        .get(format!("{base}/manifests/launcher/latest.json"))
        .timeout(Duration::from_secs(15))
        .send()
        .map_err(|e| format!("Не удалось проверить обновления: {e}"))?;
    trusted_url(response.url().as_str())?;
    let response = response
        .error_for_status()
        .map_err(|e| format!("Сервер обновлений недоступен: {e}"))?;
    #[derive(Deserialize)]
    struct Latest {
        version: String,
    }
    let latest: Latest = serde_json::from_reader(response.take(64 * 1024))
        .map_err(|_| "Сервер прислал некорректный номер версии")?;
    if version_parts(&latest.version)? <= current_parts {
        return Ok(UpdateCheck::Current {
            current: current.into(),
            remote: latest.version,
        });
    }
    Ok(UpdateCheck::Available(UpdateRelease {
        manifest_url: format!("{base}/manifests/launcher/{}.json", latest.version),
        content_base_url: format!("{base}/content/launcher/{}/files", latest.version),
        version: latest.version,
    }))
}

pub fn is_preserved(path: &str) -> bool {
    PRESERVED.iter().any(|p| p.eq_ignore_ascii_case(path))
}

fn is_artifact(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    lower.starts_with("updater/")
        || [
            "filelist.txt",
            "emptydirs.txt",
            "deletelist.txt",
            "apply-update.log",
            "apply-update.cmd",
        ]
        .contains(&lower.as_str())
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct StagedFile {
    pub source: String,
    pub target: String,
    pub manifest: ManifestFile,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct StagedUpdate {
    pub version: String,
    pub current_version: String,
    pub paths: UpdatePaths,
    pub session: PathBuf,
    pub files: Vec<StagedFile>,
    pub delete_paths: Vec<String>,
    pub empty_dirs: Vec<String>,
}

fn normalized_payload(manifest: &Manifest) -> Result<(Vec<StagedFile>, Vec<String>), String> {
    validate_manifest(manifest)?;
    if manifest.files.is_empty() {
        return Err("Пакет обновления пуст".into());
    }
    let prefix = if manifest
        .files
        .iter()
        .any(|f| f.path.eq_ignore_ascii_case(LAUNCHER_NAME))
    {
        String::new()
    } else {
        let first = manifest.files[0]
            .path
            .split_once('/')
            .ok_or("В пакете нет ChillHub.exe")?
            .0;
        let prefix = format!("{first}/");
        if !manifest.files.iter().all(|f| f.path.starts_with(&prefix)) {
            return Err("Не удалось определить корень пакета".into());
        }
        prefix
    };
    let mut files = Vec::new();
    for file in &manifest.files {
        let target = file
            .path
            .strip_prefix(&prefix)
            .ok_or("Разные корни пакета")?
            .to_owned();
        if is_preserved(&target) || is_artifact(&target) {
            return Err(format!("В пакет попало состояние установки: {target}"));
        }
        files.push(StagedFile {
            source: file.path.clone(),
            target,
            manifest: file.clone(),
        });
    }
    for name in [LAUNCHER_NAME, UPDATER_NAME, "ChillHub.SecurityHelper.exe"] {
        if !files.iter().any(|f| f.target.eq_ignore_ascii_case(name)) {
            return Err(format!("Пакет неполный: отсутствует {name}"));
        }
    }
    let mut dirs = Vec::new();
    for dir in &manifest.empty_dirs {
        let target = dir
            .strip_prefix(&prefix)
            .ok_or("Папка находится вне пакета")?;
        if is_preserved(target) || is_artifact(target) {
            return Err("Недопустимая папка пакета".into());
        }
        dirs.push(target.to_owned());
    }
    Ok((files, dirs))
}

pub fn stage_update(
    release: &UpdateRelease,
    paths: &UpdatePaths,
    control: &TransferControl,
    progress: &(dyn Fn(TransferEvent) + Sync),
) -> Result<StagedUpdate, String> {
    paths.validate()?;
    ensure_update_allowed(paths, &release.version)?;
    if version_parts(&release.version)? <= version_parts(env!("CARGO_PKG_VERSION"))? {
        return Err("Переход на эту версию не требуется".into());
    }
    trusted_url(&release.manifest_url)?;
    trusted_url(&release.content_base_url)?;
    let engine = TransferEngine::for_update().map_err(|e| e.to_string())?;
    let manifest = engine
        .fetch_manifest(&release.manifest_url, control)
        .map_err(|e| e.to_string())?;
    if manifest.version != release.version
        || (!manifest.game_id.is_empty() && manifest.game_id != "launcher")
    {
        return Err("Версия пакета не совпала с выбранным обновлением".into());
    }
    let (files, empty_dirs) = normalized_payload(&manifest)?;
    let session = paths.staging.join(uuid::Uuid::new_v4().to_string());
    let stage_lease = maintenance::StageLease::begin(paths, &session, &release.version)?;
    let payload = session.join("payload");
    fs::create_dir_all(&payload).map_err(|e| e.to_string())?;
    maintenance::reuse_verified_payload(paths, &files, &payload, control, progress)?;
    let request = TransferRequest {
        game_id: "launcher".into(),
        version: release.version.clone(),
        root: payload,
        manifest_url: release.manifest_url.clone(),
        content_base_url: release.content_base_url.clone(),
        scope: SyncScope::OwnFilesOnly,
        write_markers: false,
        allow_cleanup: false,
        ..Default::default()
    };
    let plan = engine
        .plan_manifest(&request, manifest, control, progress)
        .map_err(|e| e.to_string())?;
    engine
        .execute(&request, &plan, control, progress)
        .map_err(|e| e.to_string())?;
    let old: Vec<String> = fs::read(paths.config.join("installed-payload.json"))
        .ok()
        .and_then(|b| serde_json::from_slice(&b).ok())
        .unwrap_or_default();
    let present: HashSet<_> = files
        .iter()
        .map(|f| f.target.to_ascii_lowercase())
        .collect();
    let delete_paths = old
        .into_iter()
        .filter(|p| {
            !present.contains(&p.to_ascii_lowercase()) && !is_preserved(p) && !is_artifact(p)
        })
        .collect();
    let staged = StagedUpdate {
        version: release.version.clone(),
        current_version: env!("CARGO_PKG_VERSION").into(),
        paths: paths.clone(),
        session,
        files,
        delete_paths,
        empty_dirs,
    };
    validate_staged(&staged)?;
    for file in &staged.files {
        verify(
            &safe_join(&staged.session.join("payload"), &file.source)?,
            &file.manifest,
        )?;
    }
    stage_lease.prepared()?;
    let _ = cleanup_completed_sessions(paths);
    Ok(staged)
}

fn validate_staged(staged: &StagedUpdate) -> Result<(), String> {
    staged.paths.validate()?;
    if version_parts(&staged.version)? <= version_parts(&staged.current_version)? {
        return Err("Обновление не повышает версию".into());
    }
    if !within(&staged.session, &staged.paths.staging)
        || same_path(&staged.session, &staged.paths.staging)
    {
        return Err("Пакет находится вне временной папки".into());
    }
    check_no_links(&staged.session).map_err(|e| e.to_string())?;
    let mut targets = HashSet::new();
    for file in &staged.files {
        safe_join(&staged.session.join("payload"), &file.source)?;
        safe_join(&staged.paths.install, &file.target)?;
        if is_preserved(&file.target)
            || is_artifact(&file.target)
            || !targets.insert(file.target.to_ascii_lowercase())
        {
            return Err("Пакет пересекается с состоянием установки или содержит повтор".into());
        }
        validate_manifest(&Manifest {
            files: vec![file.manifest.clone()],
            ..Default::default()
        })?;
    }
    for name in [LAUNCHER_NAME, UPDATER_NAME, "ChillHub.SecurityHelper.exe"] {
        if !targets.contains(&name.to_ascii_lowercase()) {
            return Err(format!("Нет обязательного файла: {name}"));
        }
    }
    for path in &staged.delete_paths {
        safe_join(&staged.paths.install, path)?;
        if is_preserved(path) || is_artifact(path) || !targets.insert(path.to_ascii_lowercase()) {
            return Err("Недопустимый список удаляемых файлов".into());
        }
    }
    for dir in &staged.empty_dirs {
        safe_join(&staged.paths.install, dir)?;
        if is_preserved(dir) || is_artifact(dir) {
            return Err("Недопустимая папка пакета".into());
        }
    }
    Ok(())
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct UpdateJob {
    pub staged: StagedUpdate,
    pub parent_pid: u32,
    pub parent_executable: PathBuf,
    pub restart_args: Vec<String>,
    pub token: String,
    #[serde(default)]
    pub mode: UpdateMode,
}

#[derive(Clone, Copy, Debug, Default, Serialize, Deserialize, PartialEq, Eq)]
pub enum UpdateMode {
    #[default]
    Apply,
    Recovery,
}

pub fn launch_updater(staged: &StagedUpdate, restart_args: &[String]) -> Result<Child, String> {
    launch_job(staged, restart_args, UpdateMode::Apply)
}

pub fn launch_recovery(staged: &StagedUpdate, restart_args: &[String]) -> Result<Child, String> {
    if !staged.session.join("journal.json").is_file() {
        return Err("Журнал восстановления не найден".into());
    }
    launch_job(staged, restart_args, UpdateMode::Recovery)
}

fn launch_job(
    staged: &StagedUpdate,
    restart_args: &[String],
    mode: UpdateMode,
) -> Result<Child, String> {
    validate_staged(staged)?;
    let attempts_path = staged.paths.config.join("update-attempts.json");
    let count = update_attempt_count(&staged.paths, &staged.version)?;
    if mode == UpdateMode::Apply {
        ensure_update_allowed(&staged.paths, &staged.version)?;
    }
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    if !same_path(&exe, &staged.paths.install.join(LAUNCHER_NAME)) {
        return Err("Обновление доступно только для установленного ChillHub.exe".into());
    }
    let work = staged.session.join("work");
    fs::create_dir_all(&work).map_err(|e| e.to_string())?;
    let source = safe_join(&staged.paths.install, UPDATER_NAME)?;
    let updater = work.join(UPDATER_NAME);
    if mode != UpdateMode::Recovery || !updater.is_file() {
        fs::copy(&source, &updater)
            .map_err(|e| format!("Не удалось подготовить модуль обновления: {e}"))?;
        if hash_file(&source)? != hash_file(&updater)? {
            return Err("Копия модуля обновления повреждена".into());
        }
    }
    let job = UpdateJob {
        staged: staged.clone(),
        parent_pid: std::process::id(),
        parent_executable: exe,
        restart_args: restart_args.to_vec(),
        token: uuid::Uuid::new_v4().to_string(),
        mode,
    };
    let bytes = serde_json::to_vec(&job).map_err(|e| e.to_string())?;
    let job_path = work.join("job.json");
    atomic_write(&job_path, &bytes).map_err(|e| e.to_string())?;
    let mut child = Command::new(&updater)
        .arg("--job")
        .arg(&job_path)
        .arg("--sha256")
        .arg(format!("{:x}", Sha256::digest(&bytes)))
        .spawn()
        .map_err(|e| format!("Не удалось запустить обновление: {e}"))?;
    let ready = work.join("ready");
    let started = Instant::now();
    while started.elapsed() < Duration::from_secs(10) {
        if fs::read_to_string(&ready).ok().as_deref() == Some(&job.token) {
            if mode == UpdateMode::Apply {
                let attempts = UpdateAttempts {
                    version: staged.version.clone(),
                    count: count + 1,
                };
                atomic_write(
                    &attempts_path,
                    &serde_json::to_vec(&attempts).map_err(|e| e.to_string())?,
                )
                .map_err(|e| format!("Не удалось сохранить состояние обновления: {e}"))?;
            }
            return Ok(child);
        }
        if child.try_wait().map_err(|e| e.to_string())?.is_some() {
            return Err(
                "Модуль обновления завершился до начала работы. Лаунчер продолжает работать".into(),
            );
        }
        std::thread::sleep(Duration::from_millis(40));
    }
    // Parent remains alive. The updater times out without writing installation files.
    Err("Модуль обновления не подтвердил готовность. Лаунчер продолжает работать".into())
}

pub fn read_job(path: &Path, expected_hash: &str) -> Result<UpdateJob, String> {
    check_no_links(path).map_err(|e| e.to_string())?;
    let meta = fs::metadata(path).map_err(|e| e.to_string())?;
    if meta.len() > 16 * 1024 * 1024 {
        return Err("Слишком большой запрос обновления".into());
    }
    let bytes = fs::read(path).map_err(|e| e.to_string())?;
    if format!("{:x}", Sha256::digest(&bytes)) != expected_hash {
        return Err("Запрос обновления изменился".into());
    }
    let job: UpdateJob = serde_json::from_slice(&bytes).map_err(|e| e.to_string())?;
    validate_staged(&job.staged)?;
    if job.token.len() != 36
        || job.parent_pid == 0
        || job.parent_pid == std::process::id()
        || !same_path(
            &job.parent_executable,
            &job.staged.paths.install.join(LAUNCHER_NAME),
        )
        || !same_path(path, &job.staged.session.join("work/job.json"))
    {
        return Err("Некорректный запрос обновления".into());
    }
    let own = std::env::current_exe().map_err(|e| e.to_string())?;
    if !same_path(&own, &job.staged.session.join("work").join(UPDATER_NAME)) {
        return Err("Модуль запущен вне подготовленной сессии".into());
    }
    Ok(job)
}

/// Called by the separate updater. The parent is identified while still alive;
/// a process handle prevents PID reuse from turning a timeout into a false success.
pub fn execute_job(job: &UpdateJob, progress: &(dyn Fn(&str, f32) + Sync)) -> Result<(), String> {
    let _install = ProcessLock::acquire(&update_mutex_name(&job.staged.paths.install)?)?;
    let _security = ProcessLock::acquire("Local\\ChillHub.SecurityUi")?;
    let _security_lifetime = crate::security::update_lease(&job.staged.paths.config)?;
    let _session = maintenance::SessionLease::acquire(&job.staged.paths, &job.staged.session)?;
    let parent = ParentProcess::open(job.parent_pid, &job.parent_executable)?;
    atomic_write(&job.staged.session.join("work/ready"), job.token.as_bytes())
        .map_err(|e| e.to_string())?;
    progress("Ожидаем завершения лаунчера…", 0.0);
    parent.wait(Duration::from_secs(120))?;
    let result = match job.mode {
        UpdateMode::Apply => apply_transaction(&job.staged, progress),
        UpdateMode::Recovery => {
            progress("Восстанавливаем предыдущую версию…", 0.0);
            recover_interrupted(&job.staged).and_then(|recovered| {
                if recovered {
                    progress("Предыдущая версия восстановлена", 1.0);
                    Ok(())
                } else {
                    Err("Эта сессия уже завершена; повторное восстановление не требуется".into())
                }
            })
        }
    };
    let state = UpdateOutcome {
        version: if job.mode == UpdateMode::Recovery {
            job.staged.current_version.clone()
        } else {
            job.staged.version.clone()
        },
        success: result.is_ok(),
        message: result
            .clone()
            .err()
            .unwrap_or_else(|| "Обновление установлено".into()),
    };
    let _ = write_outcome(&job.staged.paths, &state);
    // Restart the known launcher only, never an executable path supplied by the manifest.
    if job.parent_executable.is_file()
        && safe_to_restart(&job.staged)
        && (job.mode != UpdateMode::Recovery || result.is_ok())
    {
        if result.is_ok() {
            maintenance::refresh_shell_icons(&job.parent_executable);
        }
        maintenance::retry_io(3, Duration::from_millis(500), || {
            Command::new(&job.parent_executable)
                .args(&job.restart_args)
                .current_dir(&job.staged.paths.install)
                .spawn()
        })
        .map_err(|e| format!("{}\nНе удалось открыть лаунчер: {e}", state.message))?;
    }
    result
}

fn safe_to_restart(staged: &StagedUpdate) -> bool {
    let path = staged.session.join("journal.json");
    if !path.exists() {
        return true;
    }
    let Some(journal) = fs::read(path)
        .ok()
        .and_then(|b| serde_json::from_slice::<Journal>(&b).ok())
    else {
        return false;
    };
    if journal.committed {
        return true;
    }
    journal.files.iter().all(|entry| {
        let Ok(target) = safe_join(&staged.paths.install, &entry.target) else {
            return false;
        };
        if !entry.existed {
            return !target.exists();
        }
        let Ok(backup) = safe_join(&staged.session.join("backup"), &entry.target) else {
            return false;
        };
        match (hash_file(&target), hash_file(&backup)) {
            (Ok(a), Ok(b)) => a == b,
            _ => false,
        }
    })
}

pub fn previous_outcome(paths: &UpdatePaths) -> Option<UpdateOutcome> {
    fs::read(paths.config.join("update-status.json"))
        .ok()
        .and_then(|bytes| serde_json::from_slice(&bytes).ok())
}

fn outcome_lock(paths: &UpdatePaths) -> Result<ProcessLock, String> {
    ProcessLock::acquire(&format!(
        "Local\\ChillHub.UpdateOutcome.{:x}",
        Sha256::digest(path_key(&paths.config)?.as_bytes())
    ))
}

fn write_outcome(paths: &UpdatePaths, state: &UpdateOutcome) -> Result<(), String> {
    let _lock = outcome_lock(paths)?;
    atomic_write(
        &paths.config.join("update-status.json"),
        &serde_json::to_vec(state).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())
}

/// Acknowledge only after presenting this exact outcome. A newer result is kept.
pub fn acknowledge_outcome(paths: &UpdatePaths, shown: &UpdateOutcome) -> Result<bool, String> {
    let _lock = outcome_lock(paths)?;
    if previous_outcome(paths).as_ref() != Some(shown) {
        return Ok(false);
    }
    maintenance::remove_file(&paths.config.join("update-status.json"))?;
    Ok(true)
}

/// Discovery only. A recovery is offered explicitly and must run outside the
/// launcher process with the same update locks as a normal application.
pub fn pending_recoveries(paths: &UpdatePaths) -> Result<Vec<StagedUpdate>, String> {
    paths.validate()?;
    if !paths.staging.exists() {
        return Ok(Vec::new());
    }
    let mut pending = Vec::new();
    for entry in fs::read_dir(&paths.staging).map_err(|e| e.to_string())? {
        let entry = entry.map_err(|e| e.to_string())?;
        if !entry.file_type().map_err(|e| e.to_string())?.is_dir() {
            continue;
        }
        let session = entry.path();
        check_no_links(&session).map_err(|e| e.to_string())?;
        let journal = session.join("journal.json");
        if !journal.exists() {
            continue;
        }
        let state: Journal = serde_json::from_slice(&fs::read(journal).map_err(|e| e.to_string())?)
            .map_err(|e| e.to_string())?;
        if state.committed || state.recovered {
            continue;
        }
        let job: UpdateJob = serde_json::from_slice(
            &fs::read(session.join("work/job.json")).map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
        validate_staged(&job.staged)?;
        if !same_path(&job.staged.paths.install, &paths.install)
            || !same_path(&job.staged.session, &session)
        {
            return Err("Журнал относится к другой установке".into());
        }
        pending.push(job.staged);
    }
    Ok(pending)
}

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq, Eq)]
pub struct UpdateOutcome {
    pub version: String,
    pub success: bool,
    pub message: String,
}

#[derive(Serialize, Deserialize)]
struct UpdateAttempts {
    version: String,
    count: u32,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct Backup {
    target: String,
    existed: bool,
    #[serde(default)]
    attributes: Option<u32>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
struct Journal {
    committed: bool,
    #[serde(default)]
    recovered: bool,
    files: Vec<Backup>,
}

pub fn apply_transaction(
    staged: &StagedUpdate,
    progress: &(dyn Fn(&str, f32) + Sync),
) -> Result<(), String> {
    validate_staged(staged)?;
    let _install_guard =
        DirectoryGuard::acquire(&staged.paths.install, false).map_err(|e| e.to_string())?;
    let _session_guard =
        DirectoryGuard::acquire(&staged.session, false).map_err(|e| e.to_string())?;
    let payload = staged.session.join("payload");
    let backup = staged.session.join("backup");
    let journal_path = staged.session.join("journal.json");
    if journal_path.exists() {
        return Err(
            "Эта сессия уже применялась. Требуется восстановление, повторное применение запрещено"
                .into(),
        );
    }
    for file in &staged.files {
        verify(&safe_join(&payload, &file.source)?, &file.manifest)?;
    }
    fs::create_dir_all(&backup).map_err(|e| e.to_string())?;
    let mut journal = Journal {
        committed: false,
        recovered: false,
        files: Vec::new(),
    };
    for target in staged
        .files
        .iter()
        .map(|f| &f.target)
        .chain(staged.delete_paths.iter())
    {
        let path = safe_join(&staged.paths.install, target)?;
        let existed = path.try_exists().map_err(|e| e.to_string())?;
        if existed {
            if !path.is_file() {
                return Err(format!("На месте файла находится папка: {target}"));
            }
            copy_synced(&path, &safe_join(&backup, target)?)?;
        }
        journal.files.push(Backup {
            target: target.clone(),
            existed,
            attributes: maintenance::file_attributes(&path)?,
        });
    }
    // Marker participates in rollback, but is never accepted from a payload.
    let marker = staged.paths.install.join("launcher.version");
    let existed = marker.try_exists().map_err(|e| e.to_string())?;
    if existed {
        copy_synced(&marker, &backup.join("launcher.version"))?;
    }
    journal.files.push(Backup {
        target: "launcher.version".into(),
        existed,
        attributes: maintenance::file_attributes(&marker)?,
    });
    atomic_write(
        &journal_path,
        &serde_json::to_vec(&journal).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;
    let apply = (|| {
        for (i, file) in staged.files.iter().enumerate() {
            progress(
                "Устанавливаем обновление…",
                i as f32 / staged.files.len().max(1) as f32,
            );
            replace_verified(
                &safe_join(&payload, &file.source)?,
                &safe_join(&staged.paths.install, &file.target)?,
                &file.manifest,
            )?;
        }
        for file in &staged.files {
            verify(
                &safe_join(&staged.paths.install, &file.target)?,
                &file.manifest,
            )?;
        }
        for target in &staged.delete_paths {
            let path = safe_join(&staged.paths.install, target)?;
            if path.exists() {
                maintenance::remove_file(&path)?;
            }
        }
        for dir in &staged.empty_dirs {
            fs::create_dir_all(safe_join(&staged.paths.install, dir)?)
                .map_err(|e| e.to_string())?;
        }
        maintenance::clear_readonly(&marker)?;
        atomic_write(&marker, staged.version.as_bytes()).map_err(|e| e.to_string())?;
        journal.committed = true;
        atomic_write(
            &journal_path,
            &serde_json::to_vec(&journal).map_err(|e| e.to_string())?,
        )
        .map_err(|e| e.to_string())?;
        Ok::<(), String>(())
    })();
    if let Err(error) = apply {
        progress("Восстанавливаем предыдущую версию…", 0.0);
        return match rollback(staged, &journal) {
            Ok(()) => {
                journal.recovered = true;
                atomic_write(
                    &journal_path,
                    &serde_json::to_vec(&journal).map_err(|e| e.to_string())?,
                )
                .map_err(|e| {
                    format!(
                        "Предыдущая версия восстановлена, но не удалось записать результат: {e}"
                    )
                })?;
                Err(format!(
                    "Обновление не установлено. Предыдущая версия восстановлена: {error}"
                ))
            }
            Err(recovery) => Err(format!(
                "Обновление прервано: {error}. Не удалось полностью восстановить файлы: {recovery}. Резервная копия: {}",
                backup.display()
            )),
        };
    }
    let inventory: Vec<_> = staged.files.iter().map(|f| &f.target).collect();
    // Inventory is cleanup metadata, not a condition for a successfully committed payload.
    if let Ok(bytes) = serde_json::to_vec(&inventory) {
        let _ = atomic_write(&staged.paths.config.join("installed-payload.json"), &bytes);
    }
    let _ = fs::remove_file(staged.paths.config.join("update-attempts.json"));
    maintenance::cleanup_legacy_artifacts(&staged.paths.install);
    progress("Обновление установлено", 1.0);
    Ok(())
}

fn rollback(staged: &StagedUpdate, journal: &Journal) -> Result<(), String> {
    let mut failures = Vec::new();
    for entry in journal.files.iter().rev() {
        let result = (|| {
            let target = safe_join(&staged.paths.install, &entry.target)?;
            if entry.existed {
                let source = safe_join(&staged.session.join("backup"), &entry.target)?;
                let bytes = fs::read(source).map_err(|e| e.to_string())?;
                maintenance::clear_readonly(&target)?;
                maintenance::retry_io(5, Duration::from_millis(200), || {
                    atomic_write(&target, &bytes)
                })
                .map_err(|e| e.to_string())?;
                maintenance::restore_attributes(&target, entry.attributes)?;
            } else if target.is_file() {
                maintenance::remove_file(&target)?;
            }
            Ok::<(), String>(())
        })();
        if let Err(e) = result {
            failures.push(format!("{}: {e}", entry.target));
        }
    }
    if failures.is_empty() {
        Ok(())
    } else {
        Err(failures.join("; "))
    }
}

pub fn recover_interrupted(staged: &StagedUpdate) -> Result<bool, String> {
    validate_staged(staged)?;
    let mut journal: Journal = serde_json::from_slice(
        &fs::read(staged.session.join("journal.json")).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;
    if journal.committed || journal.recovered {
        return Ok(false);
    }
    let allowed: HashSet<_> = staged
        .files
        .iter()
        .map(|f| f.target.to_ascii_lowercase())
        .chain(staged.delete_paths.iter().map(|p| p.to_ascii_lowercase()))
        .chain(std::iter::once("launcher.version".into()))
        .collect();
    if journal
        .files
        .iter()
        .any(|f| !allowed.contains(&f.target.to_ascii_lowercase()))
    {
        return Err("Журнал восстановления повреждён".into());
    }
    rollback(staged, &journal)?;
    journal.recovered = true;
    atomic_write(
        &staged.session.join("journal.json"),
        &serde_json::to_vec(&journal).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())?;
    Ok(true)
}

fn copy_synced(source: &Path, target: &Path) -> Result<(), String> {
    check_no_links(source).map_err(|e| e.to_string())?;
    check_no_links(target).map_err(|e| e.to_string())?;
    if let Some(parent) = target.parent() {
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
    }
    let _source_guard = DirectoryGuard::acquire(source.parent().ok_or("У файла нет папки")?, false)
        .map_err(|e| e.to_string())?;
    let _target_guard = DirectoryGuard::acquire(target.parent().ok_or("У файла нет папки")?, false)
        .map_err(|e| e.to_string())?;
    maintenance::retry_io(5, Duration::from_millis(200), || fs::copy(source, target))
        .map_err(|e| e.to_string())?;
    maintenance::clear_readonly(target)?;
    fs::OpenOptions::new()
        .write(true)
        .open(target)
        .and_then(|f| f.sync_all())
        .map_err(|e| e.to_string())
}

fn replace_verified(source: &Path, target: &Path, file: &ManifestFile) -> Result<(), String> {
    let _target_guard = DirectoryGuard::acquire(target.parent().ok_or("У файла нет папки")?, true)
        .map_err(|e| e.to_string())?;
    let temp = target.with_file_name(format!(".{}.update", uuid::Uuid::new_v4()));
    let result = (|| {
        copy_synced(source, &temp)?;
        verify(&temp, file)?;
        #[cfg(windows)]
        {
            use std::os::windows::ffi::OsStrExt;
            use windows_sys::Win32::Storage::FileSystem::{
                MOVEFILE_REPLACE_EXISTING, MOVEFILE_WRITE_THROUGH, MoveFileExW,
            };
            let a: Vec<_> = temp.as_os_str().encode_wide().chain(Some(0)).collect();
            let b: Vec<_> = target.as_os_str().encode_wide().chain(Some(0)).collect();
            maintenance::clear_readonly(target)?;
            maintenance::retry_io(5, Duration::from_millis(200), || {
                if unsafe {
                    MoveFileExW(
                        a.as_ptr(),
                        b.as_ptr(),
                        MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
                    )
                } == 0
                {
                    Err(std::io::Error::last_os_error())
                } else {
                    Ok(())
                }
            })
            .map_err(|e| e.to_string())?;
        }
        #[cfg(not(windows))]
        fs::rename(&temp, target).map_err(|e| e.to_string())?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(temp);
    }
    result
}

pub fn hash_file(path: &Path) -> Result<String, String> {
    let mut file =
        crate::manifest::open_regular(path, false, false, false).map_err(|e| e.to_string())?;
    let mut hash = Sha256::new();
    let mut buf = [0u8; 128 * 1024];
    loop {
        let n = file.read(&mut buf).map_err(|e| e.to_string())?;
        if n == 0 {
            break;
        }
        hash.update(&buf[..n]);
    }
    Ok(format!("{:x}", hash.finalize()))
}

fn verify(path: &Path, expected: &ManifestFile) -> Result<(), String> {
    let mut file =
        crate::manifest::open_regular(path, false, false, false).map_err(|e| e.to_string())?;
    if file.metadata().map_err(|e| e.to_string())?.len() != expected.size {
        return Err(format!("Размер файла не совпал: {}", expected.path));
    }
    let mut sha = Sha256::new();
    let mut blake = blake3::Hasher::new();
    let mut buf = [0u8; 128 * 1024];
    loop {
        let n = file.read(&mut buf).map_err(|e| e.to_string())?;
        if n == 0 {
            break;
        }
        sha.update(&buf[..n]);
        blake.update(&buf[..n]);
    }
    if expected
        .sha256
        .as_ref()
        .is_some_and(|h| !h.is_empty() && !h.eq_ignore_ascii_case(&format!("{:x}", sha.finalize())))
        || (!expected.blake3.is_empty()
            && !expected
                .blake3
                .eq_ignore_ascii_case(blake.finalize().to_hex().as_str()))
    {
        return Err(format!("Проверка файла не пройдена: {}", expected.path));
    }
    Ok(())
}

fn path_key(path: &Path) -> Result<String, String> {
    crate::paths::path_identity(path)
        .ok_or_else(|| "Не удалось проверить расположение папки обновления".into())
}
fn same_path(a: &Path, b: &Path) -> bool {
    matches!((path_key(a), path_key(b)), (Ok(a), Ok(b)) if a == b)
}
fn within(path: &Path, root: &Path) -> bool {
    matches!((path_key(path), path_key(root)), (Ok(p), Ok(r)) if p == r || p.starts_with(&format!("{r}\\")))
}
pub fn update_mutex_name(install: &Path) -> Result<String, String> {
    let hash = Sha256::digest(path_key(install)?.as_bytes());
    Ok(format!(
        "Local\\ChillHub.Updater.{}",
        hash[..8]
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect::<String>()
    ))
}

#[cfg(windows)]
pub struct ProcessLock(windows_sys::Win32::Foundation::HANDLE);
#[cfg(windows)]
impl ProcessLock {
    pub fn acquire(name: &str) -> Result<Self, String> {
        use windows_sys::Win32::{
            Foundation::{CloseHandle, WAIT_ABANDONED, WAIT_OBJECT_0},
            System::Threading::{CreateMutexW, WaitForSingleObject},
        };
        let name: Vec<_> = name.encode_utf16().chain(Some(0)).collect();
        let handle = unsafe { CreateMutexW(std::ptr::null(), 0, name.as_ptr()) };
        if handle.is_null() {
            return Err(std::io::Error::last_os_error().to_string());
        }
        if !matches!(
            unsafe { WaitForSingleObject(handle, 0) },
            WAIT_OBJECT_0 | WAIT_ABANDONED
        ) {
            unsafe {
                CloseHandle(handle);
            }
            return Err("Обновление или настройка антивируса уже выполняется".into());
        }
        Ok(Self(handle))
    }
}
#[cfg(windows)]
impl Drop for ProcessLock {
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::System::Threading::ReleaseMutex(self.0);
            windows_sys::Win32::Foundation::CloseHandle(self.0);
        }
    }
}
#[cfg(not(windows))]
pub struct ProcessLock;
#[cfg(not(windows))]
impl ProcessLock {
    pub fn acquire(_: &str) -> Result<Self, String> {
        Ok(Self)
    }
}

#[cfg(windows)]
struct ParentProcess(windows_sys::Win32::Foundation::HANDLE);
#[cfg(windows)]
impl ParentProcess {
    fn open(pid: u32, expected: &Path) -> Result<Self, String> {
        use windows_sys::Win32::{
            Foundation::CloseHandle,
            System::Threading::{
                OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION, PROCESS_SYNCHRONIZE,
                QueryFullProcessImageNameW,
            },
        };
        let h = unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                0,
                pid,
            )
        };
        if h.is_null() {
            return Err("Исходный процесс лаунчера не найден. Файлы не изменены".into());
        }
        let mut buf = vec![0u16; 32768];
        let mut size = buf.len() as u32;
        let ok = unsafe { QueryFullProcessImageNameW(h, 0, buf.as_mut_ptr(), &mut size) };
        if ok == 0
            || !same_path(
                Path::new(&String::from_utf16_lossy(&buf[..size as usize])),
                expected,
            )
        {
            unsafe {
                CloseHandle(h);
            }
            return Err("PID принадлежит другому приложению".into());
        }
        Ok(Self(h))
    }
    fn wait(&self, timeout: Duration) -> Result<(), String> {
        use windows_sys::Win32::{
            Foundation::WAIT_OBJECT_0, System::Threading::WaitForSingleObject,
        };
        if unsafe { WaitForSingleObject(self.0, timeout.as_millis() as u32) } != WAIT_OBJECT_0 {
            return Err("Лаунчер не завершился. Обновление не применено".into());
        }
        Ok(())
    }
}
#[cfg(windows)]
impl Drop for ParentProcess {
    fn drop(&mut self) {
        unsafe {
            windows_sys::Win32::Foundation::CloseHandle(self.0);
        }
    }
}
#[cfg(not(windows))]
struct ParentProcess;
#[cfg(not(windows))]
impl ParentProcess {
    fn open(_: u32, _: &Path) -> Result<Self, String> {
        Err("Обновление поддерживается только в Windows".into())
    }
    fn wait(&self, _: Duration) -> Result<(), String> {
        unreachable!()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[cfg(windows)]
    #[test]
    fn aliases_share_update_identity_and_cannot_bypass_separate_directories() {
        let temp = tempfile::tempdir().unwrap();
        let parent = temp.path().join("Long shared updater directory");
        fs::create_dir(&parent).unwrap();
        let short = crate::paths::short_test_path(&parent);
        let canonical = fs::canonicalize(&parent).unwrap();
        let install = canonical.join("Launcher installation");
        fs::create_dir(&install).unwrap();
        let aliased_install = short.join("Launcher installation");
        assert_eq!(
            update_mutex_name(&install).unwrap(),
            update_mutex_name(&aliased_install).unwrap()
        );
        assert!(same_path(
            &install.join("Missing.exe"),
            &aliased_install.join("Missing.exe")
        ));
        assert!(!same_path(Path::new("relative"), Path::new("relative")));
        assert!(update_mutex_name(Path::new("relative")).is_err());
        let mut paths = UpdatePaths {
            install: install.clone(),
            config: canonical.join("Settings"),
            games: short.join("Games"),
            staging: short.join("Staging"),
        };
        assert!(paths.validate().is_ok());
        paths.games = aliased_install.join("Future games");
        assert!(paths.validate().is_err());
        paths.games = short.join("Settings/Future games");
        assert!(paths.validate().is_err());
        paths.games = short.join("Games");
        paths.staging = short.join("Settings/Future updates");
        assert!(paths.validate().is_err());
        assert!(!install.join("Future games").exists());
        assert!(!paths.config.exists());
    }

    #[test]
    fn downgrade_and_bad_versions() {
        assert!(version_parts("1.7.20").unwrap() < version_parts("1.8.0").unwrap());
        for s in ["../1.8.1", "1.8", "1.8.1.exe", "1.-2.3", " 1.8.1"] {
            assert!(version_parts(s).is_err(), "{s}");
        }
    }
    #[test]
    fn preserve_is_exact_root_path() {
        assert!(is_preserved("CONFIG.JSON"));
        assert!(!is_preserved("data/config.json"));
        assert!(!is_preserved("config.json.bak"));
    }
    #[test]
    fn only_trusted_https_updates() {
        for s in [
            "http://launcher.samoy.love/api",
            "https://evil.invalid",
            "https://launcher.samoy.love.evil.invalid/api",
            "https://user@launcher.samoy.love/api",
            "https://launcher.samoy.love/api#x",
        ] {
            assert!(trusted_url(s).is_err());
        }
        assert!(trusted_url("https://launcher.samoy.love/api").is_ok());
    }
    pub(super) fn package(root: &Path) -> StagedUpdate {
        let paths = UpdatePaths {
            install: root.join("install"),
            config: root.join("config"),
            games: root.join("games"),
            staging: root.join("staging"),
        };
        fs::create_dir_all(&paths.install).unwrap();
        let session = paths.staging.join("session");
        fs::create_dir_all(session.join("payload")).unwrap();
        let files = [LAUNCHER_NAME, UPDATER_NAME, "ChillHub.SecurityHelper.exe"]
            .into_iter()
            .map(|name| {
                let bytes = format!("new {name}");
                fs::write(session.join("payload").join(name), &bytes).unwrap();
                fs::write(paths.install.join(name), "old").unwrap();
                StagedFile {
                    source: name.into(),
                    target: name.into(),
                    manifest: ManifestFile {
                        path: name.into(),
                        size: bytes.len() as u64,
                        sha256: Some(format!("{:x}", Sha256::digest(bytes.as_bytes()))),
                        ..Default::default()
                    },
                }
            })
            .collect();
        StagedUpdate {
            version: "1.8.1".into(),
            current_version: "1.8.0".into(),
            paths,
            session,
            files,
            delete_paths: vec![],
            empty_dirs: vec![],
        }
    }
    #[test]
    fn transaction_preserves_configuration_and_commits_marker() {
        let temp = tempfile::tempdir().unwrap();
        let staged = package(temp.path());
        fs::write(staged.paths.install.join("config.json"), "private").unwrap();
        apply_transaction(&staged, &|_, _| {}).unwrap();
        assert_eq!(
            fs::read_to_string(staged.paths.install.join("config.json")).unwrap(),
            "private"
        );
        assert_eq!(
            fs::read_to_string(staged.paths.install.join("launcher.version")).unwrap(),
            "1.8.1"
        );
    }
    #[test]
    fn corrupted_payload_never_touches_installation() {
        let temp = tempfile::tempdir().unwrap();
        let staged = package(temp.path());
        fs::write(staged.session.join("payload").join(LAUNCHER_NAME), "bad").unwrap();
        assert!(apply_transaction(&staged, &|_, _| {}).is_err());
        assert_eq!(
            fs::read_to_string(staged.paths.install.join(LAUNCHER_NAME)).unwrap(),
            "old"
        );
    }
    #[test]
    fn rejected_overlap_cannot_mutate_state() {
        let temp = tempfile::tempdir().unwrap();
        let mut staged = package(temp.path());
        staged.paths.config = staged.paths.install.join("data");
        assert!(validate_staged(&staged).is_err());
    }
    #[test]
    fn recovery_restores_original_and_removes_new_file() {
        let temp = tempfile::tempdir().unwrap();
        let staged = package(temp.path());
        fs::create_dir_all(staged.session.join("backup")).unwrap();
        fs::write(
            staged.session.join("backup").join(LAUNCHER_NAME),
            "original",
        )
        .unwrap();
        let journal = Journal {
            committed: false,
            recovered: false,
            files: vec![
                Backup {
                    target: LAUNCHER_NAME.into(),
                    existed: true,
                    attributes: None,
                },
                Backup {
                    target: UPDATER_NAME.into(),
                    existed: false,
                    attributes: None,
                },
            ],
        };
        atomic_write(
            &staged.session.join("journal.json"),
            &serde_json::to_vec(&journal).unwrap(),
        )
        .unwrap();
        assert!(recover_interrupted(&staged).unwrap());
        assert_eq!(
            fs::read_to_string(staged.paths.install.join(LAUNCHER_NAME)).unwrap(),
            "original"
        );
        assert!(!staged.paths.install.join(UPDATER_NAME).exists());
    }

    #[test]
    fn error_after_file_replacement_rolls_back_all_files() {
        let temp = tempfile::tempdir().unwrap();
        let mut staged = package(temp.path());
        fs::write(staged.paths.install.join("occupied"), "user file").unwrap();
        staged.empty_dirs.push("occupied".into());
        assert!(apply_transaction(&staged, &|_, _| {}).is_err());
        for name in [LAUNCHER_NAME, UPDATER_NAME, "ChillHub.SecurityHelper.exe"] {
            assert_eq!(
                fs::read_to_string(staged.paths.install.join(name)).unwrap(),
                "old"
            );
        }
        assert!(!staged.paths.install.join("launcher.version").exists());
        assert_eq!(
            fs::read_to_string(staged.paths.install.join("occupied")).unwrap(),
            "user file"
        );
        assert!(safe_to_restart(&staged));
    }
}
