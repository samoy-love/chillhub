//! Read-only diagnostics and an explicitly confirmed, separate security helper.
//! No exception is inferred from a download failure and no protection is disabled.
use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::{Read, Write},
    path::{Component, Path, PathBuf},
    process::{Child, Command, Stdio},
    time::{Duration, Instant},
};

#[path = "security_guides.rs"]
mod guides;
#[path = "security_session.rs"]
mod session;
pub use guides::{
    AntivirusGuide, copy_paths, guides, matching_guide, open_guide, recommended_guide,
};
pub use session::SecurityLifetime;
pub(crate) use session::update_lease;
#[cfg(windows)]
pub use session::{ElevatedSession, elevate_helper, elevated_request_from_args};

pub const WARNING: &str = "Антивирус перестанет проверять файлы в выбранных папках, включая новые игры и моды. Исключение не подтверждает безопасность файлов.";
pub const HELPER_NAME: &str = "ChillHub.SecurityHelper.exe";
pub const MANUAL_URL: &str = "https://support.microsoft.com/en-us/windows/security/threat-malware-protection/virus-and-threat-protection-in-the-windows-security-app";

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct SecurityReport {
    pub defender_available: bool,
    pub antivirus_enabled: Option<bool>,
    pub realtime_enabled: Option<bool>,
    pub tamper_protected: Option<bool>,
    pub antivirus_names: Vec<String>,
    pub detections: Vec<Detection>,
    pub diagnostic_error: String,
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", default)]
pub struct Detection {
    pub threat_id: u64,
    pub action_success: bool,
    pub resources: Vec<String>,
    pub status_id: i64,
}

impl SecurityReport {
    pub fn summary(&self) -> String {
        let mut lines = Vec::new();
        if !self.antivirus_names.is_empty() {
            lines.push(format!("Антивирус: {}", self.antivirus_names.join(", ")));
        }
        lines.push(if self.defender_available {
            format!(
                "Microsoft Defender: {}",
                match self.antivirus_enabled {
                    Some(true) => "включён",
                    Some(false) => "выключен или работает в пассивном режиме",
                    None => "состояние неизвестно",
                }
            )
        } else {
            "Не удалось получить состояние Microsoft Defender".into()
        });
        if let Some(value) = self.realtime_enabled {
            lines.push(format!(
                "Защита в реальном времени: {}",
                if value {
                    "включена"
                } else {
                    "выключена"
                }
            ));
        }
        if let Some(value) = self.tamper_protected {
            lines.push(format!(
                "Защита от изменений: {}",
                if value {
                    "включена"
                } else {
                    "выключена"
                }
            ));
        }
        if !self.detections.is_empty() {
            lines.push(format!("В журнале Defender есть записи обнаружений: {}. Это история; она не доказывает, что файл сейчас в карантине.", self.detections.len()));
        }
        if !self.diagnostic_error.is_empty() {
            lines.push(self.diagnostic_error.clone());
        }
        lines.push(
            "Эта проверка не меняет настройки защиты и не восстанавливает файлы из карантина."
                .into(),
        );
        lines.join("\n\n")
    }
}

const INSPECT: &str = r#"
$result = @{ defenderAvailable=$false; antivirusNames=@(); detections=@(); diagnosticError='' }
try { $result.antivirusNames = @(Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntivirusProduct -ErrorAction Stop | ForEach-Object { $_.displayName }) } catch {}
try {
  $s = Get-MpComputerStatus -ErrorAction Stop
  $result.defenderAvailable=$true
  $result.antivirusEnabled=[bool]$s.AntivirusEnabled
  $result.realtimeEnabled=[bool]$s.RealTimeProtectionEnabled
  $result.tamperProtected=[bool]$s.IsTamperProtected
} catch { $result.diagnosticError='Windows не предоставила состояние Defender. Откройте «Безопасность Windows» для проверки.' }
try { $result.detections=@(Get-MpThreatDetection -ErrorAction Stop | Select-Object -First 50 | ForEach-Object { @{ threatId=[ulong]$_.ThreatID; actionSuccess=[bool]$_.ActionSuccess; resources=@($_.Resources | Select-Object -First 8); statusId=[long]$_.ThreatStatusID } }) } catch {}
$result | ConvertTo-Json -Depth 6 -Compress
"#;

pub fn inspect() -> Result<SecurityReport, String> {
    serde_json::from_str(&powershell(INSPECT, None)?)
        .map_err(|e| format!("Не удалось прочитать состояние защиты: {e}"))
}

/// Reject broad locations, links, devices, remote shares and alternate streams.
/// Existing directories are required; creating a folder is never elevated.
pub fn validate_directory(path: &Path) -> Result<PathBuf, String> {
    validate_directory_policy(path, true)
}

fn validate_directory_policy(path: &Path, must_exist: bool) -> Result<PathBuf, String> {
    let raw = path
        .to_str()
        .ok_or("Путь содержит неподдерживаемые символы")?;
    if raw.is_empty()
        || raw.len() > 1024
        || raw
            .chars()
            .any(|c| c.is_control() || "*?%\"<>|".contains(c))
        || raw.starts_with("\\\\")
        || raw.starts_with("//")
        || !path.is_absolute()
        || path
            .components()
            .any(|c| matches!(c, Component::ParentDir | Component::CurDir))
    {
        return Err("Выберите отдельную локальную папку без ссылок и масок".into());
    }
    let separators: Vec<_> = raw.split(['\\', '/']).filter(|s| !s.is_empty()).collect();
    if separators.iter().any(|s| s.ends_with(['.', ' '])) {
        return Err("В конце имени папки не должно быть точки или пробела".into());
    }
    #[cfg(windows)]
    if raw.get(2..).is_some_and(|s| s.contains(':')) {
        return Err("Специальные пути не поддерживаются".into());
    }
    crate::manifest::check_no_links(path).map_err(|e| e.to_string())?;
    if (must_exist && !path.is_dir()) || path.is_file() {
        return Err("Выбранная папка не существует".into());
    }
    let mut existing = path;
    let mut missing = Vec::new();
    while !existing.exists() {
        missing.push(existing.file_name().ok_or("Некорректный путь")?.to_owned());
        existing = existing.parent().ok_or("Не найдена папка")?;
    }
    let mut canonical = fs::canonicalize(existing).map_err(|e| e.to_string())?;
    for part in missing.into_iter().rev() {
        canonical.push(part);
    }
    let canonical_text = canonical.to_string_lossy();
    let resolved = PathBuf::from(
        canonical_text
            .strip_prefix("\\\\?\\")
            .unwrap_or(&canonical_text),
    );
    let path = resolved.as_path();
    if path.parent().is_none() || path.parent().is_some_and(|p| p.parent().is_none()) {
        return Err("Нельзя исключать весь диск или его корневую папку".into());
    }
    for key in [
        "WINDIR",
        "SystemRoot",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramData",
        "USERPROFILE",
        "APPDATA",
        "LOCALAPPDATA",
        "TEMP",
        "TMP",
    ] {
        if let Some(value) = std::env::var_os(key) {
            let protected = PathBuf::from(value);
            if within(&protected, path)
                || (["WINDIR", "SystemRoot"].contains(&key) && within(path, &protected))
            {
                return Err("Эта папка слишком широка или содержит системные файлы. Выберите папку Chill Hub или отдельной игры".into());
            }
        }
    }
    if separators
        .iter()
        .position(|s| s.eq_ignore_ascii_case("Users"))
        .is_some_and(|i| separators.len() <= i + 2)
        || path
            .file_name()
            .is_some_and(|s| s.to_string_lossy().eq_ignore_ascii_case("steamapps"))
        || (path
            .file_name()
            .is_some_and(|s| s.to_string_lossy().eq_ignore_ascii_case("common"))
            && path
                .parent()
                .and_then(Path::file_name)
                .is_some_and(|s| s.to_string_lossy().eq_ignore_ascii_case("steamapps")))
        || path.join("steamapps").is_dir()
    {
        return Err("Выберите отдельную игру, а не весь профиль или библиотеку Steam".into());
    }
    Ok(path.to_path_buf())
}

pub fn validate_directories(paths: &[PathBuf]) -> Result<Vec<PathBuf>, String> {
    validate_for_operation(paths, SecurityOperation::Add)
}

fn validate_for_operation(
    paths: &[PathBuf],
    operation: SecurityOperation,
) -> Result<Vec<PathBuf>, String> {
    if paths.is_empty() || paths.len() > 32 {
        return Err("Выберите от одной до 32 папок".into());
    }
    let mut result: Vec<PathBuf> = Vec::new();
    for path in paths {
        let path = validate_directory_policy(path, operation == SecurityOperation::Add)?;
        if !result.iter().any(|p| same_path(p, &path)) {
            result.push(path);
        }
    }
    Ok(result)
}

#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "lowercase")]
pub enum SecurityOperation {
    Add,
    Remove,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct SecurityRequest {
    pub paths: Vec<PathBuf>,
    pub operation: SecurityOperation,
}

pub fn launch_helper(paths: &[PathBuf], operation: SecurityOperation) -> Result<Child, String> {
    let paths = validate_requested_paths(paths)?;
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    let helper = exe
        .parent()
        .ok_or("Не найдена папка лаунчера")?
        .join(HELPER_NAME);
    crate::manifest::check_no_links(&helper).map_err(|e| e.to_string())?;
    if !helper.is_file() {
        return Err("Помощник безопасности отсутствует. Переустановите Chill Hub".into());
    }
    let request = SecurityRequest { paths, operation };
    Command::new(&helper)
        .arg("--request")
        .arg(encode_request(&request)?)
        .spawn()
        .map_err(|e| format!("Не удалось открыть настройку: {e}"))
}

pub fn encode_request(request: &SecurityRequest) -> Result<String, String> {
    use base64::Engine;
    Ok(base64::engine::general_purpose::STANDARD
        .encode(serde_json::to_vec(request).map_err(|e| e.to_string())?))
}
pub fn decode_request(value: &str) -> Result<SecurityRequest, String> {
    use base64::Engine;
    if value.len() > 64 * 1024 {
        return Err("Слишком большой запрос настройки".into());
    }
    let bytes = base64::engine::general_purpose::STANDARD
        .decode(value)
        .map_err(|_| "Некорректный запрос настройки")?;
    let mut request: SecurityRequest =
        serde_json::from_slice(&bytes).map_err(|_| "Некорректный запрос настройки")?;
    request.paths = validate_requested_paths(&request.paths)?;
    Ok(request)
}

fn validate_requested_paths(paths: &[PathBuf]) -> Result<Vec<PathBuf>, String> {
    if paths.is_empty() || paths.len() > 32 {
        return Err("Выберите от одной до 32 папок".into());
    }
    let mut result: Vec<PathBuf> = Vec::new();
    for path in paths {
        let path = validate_directory_policy(path, false)?;
        if !result.iter().any(|p| same_path(p, &path)) {
            result.push(path);
        }
    }
    Ok(result)
}

fn filter_owned_removals(
    request: &SecurityRequest,
    journal: &Path,
) -> Result<SecurityRequest, String> {
    if request.operation == SecurityOperation::Remove {
        let owned = read_journal(journal)?;
        if request
            .paths
            .iter()
            .any(|path| !owned.added_paths.iter().any(|p| same_path(p, path)))
        {
            return Err(
                "Можно удалить только исключения, добавленные Chill Hub. Другие записи не изменены"
                    .into(),
            );
        }
    }
    Ok(request.clone())
}

fn prepare_directories(request: &SecurityRequest) -> Result<(), String> {
    let paths = validate_requested_paths(&request.paths)?;
    if request.operation == SecurityOperation::Add {
        for path in paths {
            if !path.is_dir() && is_elevated() {
                return Err(
                    "Создайте выбранную папку без прав администратора и повторите настройку".into(),
                );
            }
            crate::manifest::DirectoryGuard::acquire(&path, true).map_err(|e| e.to_string())?;
        }
    }
    Ok(())
}

pub fn mark_introduction_shown() -> Result<(), String> {
    let path = journal_path()?;
    let _lock = journal_lock(&path)?;
    let mut journal = read_journal(&path)?;
    journal.introduction_shown = true;
    crate::paths::atomic_write(
        &path,
        &serde_json::to_vec(&journal).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())
}

/// None is the initial offer; Some is a separate external Steam game folder.
/// Checking never consumes the offer and does not open a helper or change AV.
pub fn should_offer(extra: Option<&Path>, games_root: &Path) -> Result<bool, String> {
    should_offer_in(&journal_path()?, extra, games_root)
}
pub fn should_offer_at(
    data: &Path,
    extra: Option<&Path>,
    games_root: &Path,
) -> Result<bool, String> {
    should_offer_in(&data.join("security-exclusions.json"), extra, games_root)
}

fn should_offer_in(
    journal: &Path,
    extra: Option<&Path>,
    games_root: &Path,
) -> Result<bool, String> {
    let state = read_journal(journal)?;
    match extra {
        None => Ok(!state.introduction_shown),
        Some(path) => {
            let path = validate_directory_policy(path, false)?;
            Ok(!within(&path, games_root)
                && !state
                    .offered_steam_paths
                    .iter()
                    .any(|p| same_path(p, &path)))
        }
    }
}

/// Call only after the offer has actually been displayed/declined by the user.
pub fn acknowledge_offer(extra: Option<&Path>) -> Result<(), String> {
    acknowledge_offer_in(&journal_path()?, extra)
}
pub fn acknowledge_offer_at(data: &Path, extra: Option<&Path>) -> Result<(), String> {
    acknowledge_offer_in(&data.join("security-exclusions.json"), extra)
}

fn acknowledge_offer_in(path: &Path, extra: Option<&Path>) -> Result<(), String> {
    let _lock = journal_lock(path)?;
    let mut state = read_journal(path)?;
    if let Some(extra) = extra {
        let extra = validate_directory_policy(extra, false)?;
        if !state
            .offered_steam_paths
            .iter()
            .any(|p| same_path(p, &extra))
        {
            state.offered_steam_paths.push(extra);
        }
    } else {
        state.introduction_shown = true;
    }
    crate::paths::atomic_write(
        path,
        &serde_json::to_vec(&state).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ExclusionResult {
    pub path: String,
    pub status: String,
    pub detail: String,
    #[serde(default)]
    pub added: bool,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase", default)]
struct SecurityJournal {
    introduction_shown: bool,
    added_paths: Vec<PathBuf>,
    offered_steam_paths: Vec<PathBuf>,
    manual_paths: Vec<serde_json::Value>,
}

fn journal_path() -> Result<PathBuf, String> {
    let data = std::env::var_os("CHILLHUB_DATA_DIR")
        .map(PathBuf::from)
        .or_else(|| std::env::var_os("APPDATA").map(|p| PathBuf::from(p).join("ChillHub")))
        .ok_or("Не найдена папка настроек")?;
    crate::manifest::check_no_links(&data).map_err(|e| e.to_string())?;
    Ok(data.join("security-exclusions.json"))
}

fn read_journal(path: &Path) -> Result<SecurityJournal, String> {
    crate::manifest::check_no_links(path).map_err(|e| e.to_string())?;
    match fs::read(path) {
        Ok(bytes) if bytes.len() <= 1024 * 1024 => serde_json::from_slice(&bytes)
            .map_err(|_| "Журнал исключений повреждён. Он сохранён для восстановления".into()),
        Ok(_) => Err("Журнал исключений слишком велик".into()),
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(SecurityJournal::default()),
        Err(e) => Err(e.to_string()),
    }
}

fn record_results(path: &Path, results: &[ExclusionResult]) -> Result<(), String> {
    let _lock = journal_lock(path)?;
    let mut journal = read_journal(path)?;
    for result in results {
        let item = PathBuf::from(&result.path);
        if result.added && !journal.added_paths.iter().any(|p| same_path(p, &item)) {
            journal.added_paths.push(item.clone());
        }
        if result.status == "removed" {
            journal.added_paths.retain(|p| !same_path(p, &item));
        }
    }
    crate::paths::atomic_write(
        path,
        &serde_json::to_vec(&journal).map_err(|e| e.to_string())?,
    )
    .map_err(|e| e.to_string())
}

fn journal_lock(path: &Path) -> Result<crate::update::ProcessLock, String> {
    use sha2::{Digest, Sha256};
    let key = format!(
        "{:X}",
        Sha256::digest(path.to_string_lossy().to_uppercase().as_bytes())
    );
    crate::update::ProcessLock::acquire(&format!("Local\\ChillHub.SecurityJournal.{key}"))
}

/// Compatibility with the installed NSIS entry points. None means the offer was
/// already shown or there are no application-owned exceptions to remove.
pub fn request_from_args(args: &[String]) -> Result<Option<SecurityRequest>, String> {
    if args.len() == 2 && args[0] == "--request" {
        return filter_owned_removals(&decode_request(&args[1])?, &journal_path()?).map(Some);
    }
    let mut offer = false;
    let mut remove = false;
    let mut paths = Vec::new();
    let mut index = 0;
    while index < args.len() {
        match args[index].as_str() {
            "--offer" => offer = true,
            "--remove" => remove = true,
            "--games" | "--updates" | "--extra" => {
                index += 1;
                paths.push(PathBuf::from(
                    args.get(index).ok_or("Не указан путь к папке")?,
                ));
            }
            _ => return Err("Неизвестный параметр помощника".into()),
        }
        index += 1;
    }
    if offer && remove {
        return Err("Противоречивые параметры помощника".into());
    }
    let journal_file = journal_path()?;
    let _journal_lock = journal_lock(&journal_file)?;
    let journal = read_journal(&journal_file)?;
    if remove {
        if journal.added_paths.is_empty() {
            return Ok(None);
        }
        let paths = validate_for_operation(&journal.added_paths, SecurityOperation::Remove)?;
        return Ok(Some(SecurityRequest {
            paths,
            operation: SecurityOperation::Remove,
        }));
    }
    if offer && journal.introduction_shown {
        return Ok(None);
    }
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    paths.insert(0, exe.parent().ok_or("Не найдена папка лаунчера")?.into());
    if !args.iter().any(|arg| arg == "--updates") {
        paths.push(std::env::temp_dir().join("ChillHub/SelfUpdate"));
    }
    // Validate every supplied path, including currently missing folders. We do
    // not silently accept an unsafe path and do not create folders with elevation.
    for path in &paths {
        validate_directory_policy(path, false)?;
    }
    let paths = validate_requested_paths(&paths)?;
    Ok(Some(SecurityRequest {
        paths,
        operation: SecurityOperation::Add,
    }))
}

// Data is read from stdin, never interpolated into PowerShell source. Each
// directory is revalidated in Rust at the elevated boundary immediately before
// this script. Only Add/Remove-MpPreference for these explicit paths are used.
const CHANGE: &str = r#"
$request = [Console]::In.ReadToEnd() | ConvertFrom-Json
$checkExe = $null
try {
  $platform = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Microsoft\Windows Defender\Platform'
  $candidates = @(Get-ChildItem -LiteralPath $platform -Directory -ErrorAction SilentlyContinue | Sort-Object { try { [version]($_.Name.Split('-')[0]) } catch { [version]'0.0' } } -Descending | ForEach-Object { Join-Path $_.FullName 'MpCmdRun.exe' })
  $candidates += Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Windows Defender\MpCmdRun.exe'
  foreach ($candidate in $candidates) {
    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
      $signature=Get-AuthenticodeSignature -LiteralPath $candidate
      if ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Subject -match 'O=Microsoft Corporation') { $checkExe=$candidate; break }
    }
  }
} catch {}
function Test-Effective([string]$path) {
  if (!$checkExe) { return $null }
  $process=$null
  try {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$checkExe; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.Arguments='-CheckExclusion -Path "'+$path+'"'
    $process=[Diagnostics.Process]::Start($start)
    $out=$process.StandardOutput.ReadToEndAsync(); $err=$process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(15000)) { $process.Kill(); return $null }
    if ($process.ExitCode -eq 0) { return $true }
    if ($process.ExitCode -eq 1) { return $false }
  } catch {} finally { if ($process) { $process.Dispose() } }
  return $null
}
$results = @()
foreach ($path in $request.paths) {
  $present=$false; $attempted=$false; $added=$false
  try {
    $before = @((Get-MpPreference -ErrorAction Stop).ExclusionPath)
    $present = $before -contains $path
    if ($request.operation -eq 'remove') {
      if ($present) { Remove-MpPreference -ExclusionPath $path -ErrorAction Stop }
      $exists = @((Get-MpPreference -ErrorAction Stop).ExclusionPath) -contains $path
      $status = if ($exists) { 'blocked' } else { 'removed' }
      $detail = if ($exists) { 'Windows не применила изменение. Проверьте политику защиты.' } else { 'Эта запись удалена. Другие исключения не изменены.' }
    } else {
      $effective=Test-Effective $path
      if (!$present -and $effective -ne $true) { $attempted=$true; Add-MpPreference -ExclusionPath $path -ErrorAction Stop }
      $exists = @((Get-MpPreference -ErrorAction Stop).ExclusionPath) -contains $path
      $added=$attempted -and $exists
      if ($effective -ne $true) {
        $effective=Test-Effective $path
        for ($retry=0; $retry -lt 10 -and $effective -eq $false; $retry++) { Start-Sleep -Milliseconds 500; $effective=Test-Effective $path }
      }
      $status = if ($effective -eq $true) { if ($added) {'applied'} else {'existing'} } elseif ($effective -eq $false) { 'blocked' } else { 'unknown' }
      $detail = if ($effective -eq $true) { 'Defender подтвердил, что папка исключена из проверки. Можно повторить загрузку.' } elseif ($effective -eq $false) { 'Запись не действует. Проверьте политику Windows и защиту от изменений.' } else { 'Не удалось подтвердить действие исключения. Проверьте состояние в безопасности Windows.' }
    }
    $results += @{ path=$path; status=$status; detail=$detail; added=$added }
  } catch { $message=$_.Exception.Message; if ($attempted) { try { $added=@((Get-MpPreference -ErrorAction Stop).ExclusionPath) -contains $path } catch {} }; $results += @{ path=$path; status='failed'; detail='Не удалось изменить исключение: ' + $message; added=$added } }
}
ConvertTo-Json -InputObject @($results) -Depth 4 -Compress
"#;

/// The caller MUST invoke this only from the helper's explicit confirmation
/// callback. Even an elevated helper starts with a confirmation window.
pub fn apply_confirmed(request: &SecurityRequest) -> Result<Vec<ExclusionResult>, String> {
    let journal = journal_path()?;
    let request = filter_owned_removals(request, &journal)?;
    apply_with_sink(&request, |result| {
        record_results(&journal, std::slice::from_ref(result))
    })
}

fn apply_with_sink(
    request: &SecurityRequest,
    mut record: impl FnMut(&ExclusionResult) -> Result<(), String>,
) -> Result<Vec<ExclusionResult>, String> {
    if !is_elevated() {
        return Err("Для этого действия требуется подтверждение Windows".into());
    }
    let request = SecurityRequest {
        paths: validate_for_operation(&request.paths, request.operation)?,
        operation: request.operation,
    };
    let _guards: Vec<_> = request
        .paths
        .iter()
        .map(|path| {
            let mut existing = path.as_path();
            while !existing.exists() {
                existing = existing.parent().ok_or("Не найдена папка")?;
            }
            crate::manifest::DirectoryGuard::acquire(existing, false).map_err(|e| e.to_string())
        })
        .collect::<Result<_, _>>()?;
    let mut results = Vec::new();
    for path in &request.paths {
        let one = SecurityRequest {
            paths: vec![path.clone()],
            operation: request.operation,
        };
        let input = serde_json::to_vec(&one).map_err(|e| e.to_string())?;
        let result = powershell(CHANGE, Some(&input))
            .and_then(|text| {
                let mut parsed: Vec<ExclusionResult> = serde_json::from_str(&text)
                    .map_err(|e| format!("Не удалось прочитать результат настройки: {e}"))?;
                if parsed.len() != 1 || !same_path(Path::new(&parsed[0].path), path) {
                    return Err("Некорректный ответ Windows".into());
                }
                Ok(parsed.remove(0))
            })
            .unwrap_or_else(|error| ExclusionResult {
                path: path.display().to_string(),
                status: "failed".into(),
                detail: error,
                added: false,
            });
        // Persist or transmit before moving to the next path, so a later timeout
        // cannot discard already completed exclusions.
        record(&result)?;
        results.push(result);
    }
    Ok(results)
}

pub fn open_windows_security() -> Result<(), String> {
    shell_open("windowsdefender://threat")
}
pub fn open_manual() -> Result<(), String> {
    shell_open(MANUAL_URL)
}

#[cfg(windows)]
pub fn is_elevated() -> bool {
    use windows_sys::Win32::{
        Foundation::CloseHandle,
        Security::{GetTokenInformation, TOKEN_ELEVATION, TOKEN_QUERY, TokenElevation},
        System::Threading::{GetCurrentProcess, OpenProcessToken},
    };
    let mut token = std::ptr::null_mut();
    if unsafe { OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token) } == 0 {
        return false;
    }
    let mut elevation: TOKEN_ELEVATION = unsafe { std::mem::zeroed() };
    let mut size = 0;
    let ok = unsafe {
        GetTokenInformation(
            token,
            TokenElevation,
            &mut elevation as *mut _ as *mut _,
            std::mem::size_of::<TOKEN_ELEVATION>() as u32,
            &mut size,
        )
    };
    unsafe {
        CloseHandle(token);
    }
    ok != 0 && elevation.TokenIsElevated != 0
}
#[cfg(not(windows))]
pub fn is_elevated() -> bool {
    false
}

#[cfg(not(windows))]
pub fn elevate_helper(_: &SecurityRequest) -> Result<(), String> {
    Err("Доступно только в Windows".into())
}

#[cfg(windows)]
fn shell_open(target: &str) -> Result<(), String> {
    use windows_sys::Win32::UI::{Shell::ShellExecuteW, WindowsAndMessaging::SW_SHOWNORMAL};
    let target: Vec<_> = target.encode_utf16().chain(Some(0)).collect();
    let result = unsafe {
        ShellExecuteW(
            std::ptr::null_mut(),
            std::ptr::null(),
            target.as_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            SW_SHOWNORMAL,
        )
    } as isize;
    if result <= 32 {
        Err("Не удалось открыть справку Windows".into())
    } else {
        Ok(())
    }
}
#[cfg(not(windows))]
fn shell_open(_: &str) -> Result<(), String> {
    Err("Доступно только в Windows".into())
}

#[cfg(windows)]
pub(crate) fn powershell(script: &str, input: Option<&[u8]>) -> Result<String, String> {
    use base64::Engine;
    use std::os::windows::process::CommandExt;
    let shell = crate::shortcuts::system_directory()?.join("WindowsPowerShell/v1.0/powershell.exe");
    let script = format!(
        "[Console]::InputEncoding=[System.Text.UTF8Encoding]::new($false); [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; {script}"
    );
    let bytes: Vec<_> = script.encode_utf16().flat_map(u16::to_le_bytes).collect();
    let encoded = base64::engine::general_purpose::STANDARD.encode(bytes);
    let mut child = Command::new(shell)
        .args([
            "-NoLogo",
            "-NoProfile",
            "-NonInteractive",
            "-EncodedCommand",
            &encoded,
        ])
        .creation_flags(0x08000000)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| e.to_string())?;
    if let Some(mut stdin) = child.stdin.take()
        && let Some(input) = input
    {
        stdin.write_all(input).map_err(|e| e.to_string())?;
    }
    let stdout = child
        .stdout
        .take()
        .ok_or("Не удалось прочитать ответ Windows")?;
    let stderr = child
        .stderr
        .take()
        .ok_or("Не удалось прочитать ответ Windows")?;
    let read = |stream: Box<dyn Read + Send>| {
        std::thread::spawn(move || {
            let mut bytes = Vec::new();
            stream
                .take(2 * 1024 * 1024)
                .read_to_end(&mut bytes)
                .map(|_| bytes)
        })
    };
    let out = read(Box::new(stdout));
    let err = read(Box::new(stderr));
    let start = Instant::now();
    let status = loop {
        if let Some(status) = child.try_wait().map_err(|e| e.to_string())? {
            break status;
        }
        if start.elapsed() > Duration::from_secs(45) {
            let _ = child.kill();
            let _ = child.wait();
            return Err(
                "Windows не ответила вовремя. Проверьте состояние в «Безопасности Windows»".into(),
            );
        }
        std::thread::sleep(Duration::from_millis(40));
    };
    let output = out
        .join()
        .map_err(|_| "Не удалось прочитать ответ")?
        .map_err(|e| e.to_string())?;
    let error = err
        .join()
        .map_err(|_| "Не удалось прочитать ответ")?
        .map_err(|e| e.to_string())?;
    if !status.success() {
        return Err(format!(
            "Windows не выполнила запрос: {}",
            String::from_utf8_lossy(&error).trim()
        ));
    }
    Ok(String::from_utf8_lossy(&output)
        .trim_start_matches('\u{feff}')
        .trim()
        .to_owned())
}
#[cfg(not(windows))]
pub(crate) fn powershell(_: &str, _: Option<&[u8]>) -> Result<String, String> {
    Err("Диагностика доступна только в Windows".into())
}

fn path_key(path: &Path) -> String {
    path.to_string_lossy()
        .replace('/', "\\")
        .trim_end_matches('\\')
        .to_lowercase()
        .trim_start_matches("\\\\?\\")
        .to_owned()
}
fn same_path(a: &Path, b: &Path) -> bool {
    path_key(a) == path_key(b)
}
fn within(path: &Path, root: &Path) -> bool {
    let p = path_key(path);
    let r = path_key(root);
    p == r || p.starts_with(&(r + "\\"))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn broad_and_nonexistent_paths_are_rejected() {
        for path in [
            "C:\\",
            "C:\\Windows",
            "C:\\Users",
            "\\\\server\\share",
            "C:\\safe\\..\\Windows",
            "C:\\file:stream",
            "C:\\*.exe",
        ] {
            assert!(validate_directory(Path::new(path)).is_err(), "{path}");
        }
    }
    #[test]
    fn valid_existing_game_and_request_roundtrip() {
        let tmp = tempfile::tempdir().unwrap();
        let game = tmp.path().join("Games/ChillHub/peak");
        fs::create_dir_all(&game).unwrap();
        let request = SecurityRequest {
            paths: vec![game],
            operation: SecurityOperation::Add,
        };
        let roundtrip = decode_request(&encode_request(&request).unwrap()).unwrap();
        assert_eq!(roundtrip.paths, request.paths);
    }
    #[test]
    fn entire_steam_library_is_rejected() {
        let tmp = tempfile::tempdir().unwrap();
        let root = tmp.path().join("SteamLibrary");
        fs::create_dir_all(root.join("steamapps/common")).unwrap();
        assert!(validate_directory(&root).is_err());
        assert!(validate_directory(&root.join("steamapps/common")).is_err());
    }
    #[test]
    fn diagnostic_does_not_claim_quarantine() {
        let report = SecurityReport {
            detections: vec![Detection::default()],
            ..Default::default()
        };
        assert!(report.summary().contains("не доказывает"));
    }
    #[test]
    fn change_script_never_interpolates_data_or_disables_protection() {
        assert!(CHANGE.contains("[Console]::In.ReadToEnd()"));
        assert!(!CHANGE.contains("Set-MpPreference"));
        assert!(!CHANGE.contains("DisableRealtimeMonitoring"));
        assert!(!CHANGE.contains("Restore"));
    }

    #[test]
    fn journal_tracks_only_entries_added_by_this_application() {
        let temp = tempfile::tempdir().unwrap();
        let path = temp.path().join("security.json");
        record_results(
            &path,
            &[
                ExclusionResult {
                    path: "D:\\Games\\ChillHub".into(),
                    status: "registered".into(),
                    detail: String::new(),
                    added: true,
                },
                ExclusionResult {
                    path: "D:\\Other".into(),
                    status: "registered".into(),
                    detail: String::new(),
                    added: false,
                },
            ],
        )
        .unwrap();
        let journal = read_journal(&path).unwrap();
        assert_eq!(
            journal.added_paths,
            vec![PathBuf::from("D:\\Games\\ChillHub")]
        );
        record_results(
            &path,
            &[ExclusionResult {
                path: "D:\\Games\\ChillHub".into(),
                status: "removed".into(),
                detail: String::new(),
                added: false,
            }],
        )
        .unwrap();
        assert!(read_journal(&path).unwrap().added_paths.is_empty());
    }

    #[test]
    fn stale_game_exclusion_can_be_removed_without_recreating_directory() {
        let temp = tempfile::tempdir().unwrap();
        let missing = temp.path().join("games/chillhub/removed");
        assert!(validate_directory(&missing).is_err());
        assert!(validate_directory_policy(&missing, false).is_ok());
        assert!(!missing.exists());
    }

    #[test]
    fn direct_remove_request_cannot_claim_preexisting_user_exclusion() {
        let temp = tempfile::tempdir().unwrap();
        let journal = temp.path().join("security-exclusions.json");
        let owned = temp.path().join("Games/Owned");
        let existing = temp.path().join("Games/Preexisting");
        record_results(
            &journal,
            &[ExclusionResult {
                path: owned.display().to_string(),
                status: "applied".into(),
                detail: String::new(),
                added: true,
            }],
        )
        .unwrap();
        assert!(
            filter_owned_removals(
                &SecurityRequest {
                    paths: vec![existing],
                    operation: SecurityOperation::Remove
                },
                &journal
            )
            .is_err()
        );
        assert!(
            filter_owned_removals(
                &SecurityRequest {
                    paths: vec![owned.clone()],
                    operation: SecurityOperation::Remove
                },
                &journal
            )
            .is_ok()
        );
        assert_eq!(read_journal(&journal).unwrap().added_paths, vec![owned]);
    }

    #[test]
    fn offers_are_acknowledged_only_after_display_and_never_change_ownership() {
        let temp = tempfile::tempdir().unwrap();
        let journal = temp.path().join("security-exclusions.json");
        let games = temp.path().join("Games/ChillHub");
        let external = temp.path().join("Steam/game");
        assert!(should_offer_in(&journal, None, &games).unwrap());
        assert!(!journal.exists());
        acknowledge_offer_in(&journal, None).unwrap();
        assert!(!should_offer_in(&journal, None, &games).unwrap());
        assert!(!should_offer_in(&journal, Some(&games.join("inside")), &games).unwrap());
        assert!(should_offer_in(&journal, Some(&external), &games).unwrap());
        acknowledge_offer_in(&journal, Some(&external)).unwrap();
        assert!(!should_offer_in(&journal, Some(&external), &games).unwrap());
        assert!(read_journal(&journal).unwrap().added_paths.is_empty());
    }

    #[test]
    fn effective_verification_is_bounded_and_never_promotes_unknown_to_success() {
        assert!(CHANGE.contains("-CheckExclusion -Path"));
        assert!(CHANGE.contains("WaitForExit(15000)"));
        assert!(CHANGE.contains("$retry -lt 10"));
        assert!(CHANGE.contains("else { 'unknown' }"));
        assert!(CHANGE.contains("$added=$attempted -and $exists"));
    }
}
