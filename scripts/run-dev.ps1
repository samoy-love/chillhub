param(
  [string]$ContentRoot,
  [string]$GamesPath,
  [ValidateSet('local','prod')]
  [string]$Env = 'local',
  [switch]$SetClientConfig,
  [switch]$BuildServers,
  [switch]$ResetAdminAuth
)

# Ensure Unicode I/O (fix mojibake like 'Рє')
try {
  [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($true)
  [Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($true)
} catch {}

# Helper: secure random bytes -> Base64Url (no padding)
function New-Base64Url {
  param([int]$Size = 32)
  $bytes = New-Object byte[] $Size
  try {
    # PowerShell 7+ / .NET 6+: has RandomNumberGenerator.Fill
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
  } catch {
    # Windows PowerShell (.NET Framework): use RNGCryptoServiceProvider via Create()
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
  }
  $b64 = [Convert]::ToBase64String($bytes)
  $b64 = $b64.TrimEnd('=') -replace '\+','-' -replace '/','_'
  return $b64
}

# Helper: generate decent random password (base64url, 16 chars)
function New-RandomPassword {
  param([int]$Len = 16)
  $pw = New-Base64Url -Size 18  # ~24 chars base64url
  if ($pw.Length -gt $Len) { return $pw.Substring(0, $Len) }
  return $pw
}

# Helper (stub): bcrypt generation disabled in dev script; server will hash plain
function New-BcryptFromPlain { param([string]$Plain) return "" }

# Helper: Update client config in %APPDATA%\ChillHub
# ВАЖНО: конфиг живёт в %APPDATA%, а НЕ в %LOCALAPPDATA% — последний является каталогом
# установки лаунчера, и конфиг оттуда попадал в пакет обновления (вечный цикл самообновления).
function Set-ChillHubClientConfig {
  param([string]$ApiBaseUrl, [string]$GamesPath)
  try {
    $configDir = Join-Path $env:APPDATA 'ChillHub'
    if (!(Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }
    $configPath = Join-Path $configDir 'config.json'
    $cfg = @{
      GamesPath = $GamesPath
      DownloadThreads = 8
      ApiBaseUrl = $ApiBaseUrl
      LastGameId = ''
    } | ConvertTo-Json -Depth 4
    Set-Content -Path $configPath -Value $cfg -Encoding UTF8
    Write-Host "[OK]   Launcher config updated: $configPath" -ForegroundColor Green
  } catch {
    Write-Host "[WARN] Failed to update client config: $($_.Exception.Message)" -ForegroundColor Yellow
  }
}

# Utility to start all three apps (API, Admin, Client) and restart them on demand.
# Usage:
#   .\scripts\run-dev.ps1 -ContentRoot "C:\\path\\to\\content" -GamesPath "D:\\Games\\ChillHub"
# Controls:
#   r/к + Enter  -> Restart all
#   q/й + Enter  -> Quit

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir

# Environment endpoint selection
$ApiBaseLocal = 'http://localhost:55700'
$ApiBaseProd  = 'https://launcher.samoy.love'
$ApiBase = if ($Env -eq 'prod') { $ApiBaseProd } else { $ApiBaseLocal }
Write-Host "[INFO] Environment: $Env" -ForegroundColor Cyan
Write-Host "[INFO] ApiBaseUrl:  $ApiBase" -ForegroundColor Cyan

# Configure auth-related environment variables for admin server
function Set-AuthEnv {
  param([ValidateSet('local','prod')][string]$Mode)
  if ($Mode -eq 'local') {
    # Local testing: do NOT set COOKIE_DOMAIN (host-only cookies work on localhost), disable Secure cookies
    try { Remove-Item Env:COOKIE_DOMAIN -ErrorAction SilentlyContinue } catch {}
    $env:COOKIE_SECURE = 'false'
    if (-not $env:JWT_SECRET -or $env:JWT_SECRET.Trim() -eq '') { $env:JWT_SECRET = 'dev-secret-please-change-32bytes-min' }
    # Always force admin username as requested
    $env:ADMIN_USERNAME = 'admin'
    # ADMIN_PASSWORD_PLAIN действует на сервере только вместе с этим флагом:
    # без него сервер громко игнорирует открытый пароль. Флаг ставится здесь,
    # в скрипте локального запуска, и НИКОГДА не попадает в прод-юнит —
    # именно поэтому случайно скопированный туда PLAIN там не сработает.
    $env:ADMIN_ALLOW_PLAIN_PASSWORD = '1'
    # If plain is set, ensure bcrypt is unset so server picks dev fallback
    if ($env:ADMIN_PASSWORD_PLAIN -and $env:ADMIN_PASSWORD_PLAIN.Trim() -ne '') {
      try { Remove-Item Env:ADMIN_PASSWORD_BCRYPT -ErrorAction SilentlyContinue } catch {}
    }
    # Prompt for admin password if neither bcrypt nor plain provided
    if ((-not $env:ADMIN_PASSWORD_BCRYPT -or $env:ADMIN_PASSWORD_BCRYPT.Trim() -eq '') -and (-not $env:ADMIN_PASSWORD_PLAIN -or $env:ADMIN_PASSWORD_PLAIN.Trim() -eq '')) {
      try {
        Write-Host "Set admin password for local Admin UI (username=admin)." -ForegroundColor Cyan
        $p1 = Read-Host -AsSecureString "Enter password"
        $p2 = Read-Host -AsSecureString "Confirm password"
        $plain1 = [Runtime.InteropServices.Marshal]::PtrToStringUni([Runtime.InteropServices.Marshal]::SecureStringToBSTR($p1))
        $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringUni([Runtime.InteropServices.Marshal]::SecureStringToBSTR($p2))
        if ($plain1 -ne $plain2 -or [string]::IsNullOrWhiteSpace($plain1)) { throw "Passwords do not match or empty" }
        $env:ADMIN_PASSWORD_PLAIN = $plain1
        try { Remove-Item Env:ADMIN_PASSWORD_BCRYPT -ErrorAction SilentlyContinue } catch {}
      } catch {
        # Fallback: generate temporary password
        $env:ADMIN_PASSWORD_PLAIN = ([Guid]::NewGuid().ToString('N')).Substring(0,16)
        try { Remove-Item Env:ADMIN_PASSWORD_BCRYPT -ErrorAction SilentlyContinue } catch {}
        Write-Host "[WARN] Using temporary generated admin password (username=admin): $($env:ADMIN_PASSWORD_PLAIN)" -ForegroundColor Yellow
      }
    }
    # Optional: shorter access TTL to test refresh easily
    if (-not $env:JWT_ACCESS_TTL -or $env:JWT_ACCESS_TTL.Trim() -eq '') { $env:JWT_ACCESS_TTL = '24h' }
    if (-not $env:JWT_REFRESH_TTL -or $env:JWT_REFRESH_TTL.Trim() -eq '') { $env:JWT_REFRESH_TTL = '720h' } # 30d
  } else {
    # Prod-like testing: use real domain and Secure cookies
    $env:COOKIE_DOMAIN = 'launcher.samoy.love'
    $env:COOKIE_SECURE = 'true'
    if (-not $env:JWT_ACCESS_TTL -or $env:JWT_ACCESS_TTL.Trim() -eq '') { $env:JWT_ACCESS_TTL = '24h' }
    if (-not $env:JWT_REFRESH_TTL -or $env:JWT_REFRESH_TTL.Trim() -eq '') { $env:JWT_REFRESH_TTL = '720h' }
    if (-not $env:JWT_SECRET -or $env:JWT_SECRET.Trim() -eq '') {
      Write-Host "[WARN] JWT_SECRET is not set. Set a strong secret to mirror production." -ForegroundColor Yellow
    }
    # Always force admin username as requested
    $env:ADMIN_USERNAME = 'admin'
    # For prod-like mode we also reuse local persisted bcrypt if present; otherwise prompt
    $secretDir = Join-Path $env:LOCALAPPDATA 'ChillHub'
    $secretPath = Join-Path $secretDir 'admin.secret.json'
    if (-not $env:ADMIN_PASSWORD_BCRYPT -or $env:ADMIN_PASSWORD_BCRYPT.Trim() -eq '') {
      if (Test-Path $secretPath) {
        try { $sec = Get-Content -Path $secretPath -Raw | ConvertFrom-Json; if ($sec -and $sec.adminBcrypt) { $env:ADMIN_PASSWORD_BCRYPT = [string]$sec.adminBcrypt } } catch {}
      }
    }
    if (-not $env:ADMIN_PASSWORD_BCRYPT -or $env:ADMIN_PASSWORD_BCRYPT.Trim() -eq '') {
      try {
        Write-Host "Set admin password for Admin UI (username=admin)." -ForegroundColor Cyan
        $p1 = Read-Host -AsSecureString "Enter password"
        $p2 = Read-Host -AsSecureString "Confirm password"
        $plain1 = [Runtime.InteropServices.Marshal]::PtrToStringUni([Runtime.InteropServices.Marshal]::SecureStringToBSTR($p1))
        $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringUni([Runtime.InteropServices.Marshal]::SecureStringToBSTR($p2))
        if ($plain1 -ne $plain2 -or [string]::IsNullOrWhiteSpace($plain1)) { throw "Passwords do not match or empty" }
        $hash = New-BcryptFromPlain -Plain $plain1
        if ($hash -and $hash.Trim() -ne '') {
          $env:ADMIN_PASSWORD_BCRYPT = $hash
          try {
            if (!(Test-Path $secretDir)) { New-Item -ItemType Directory -Path $secretDir | Out-Null }
            @{ adminBcrypt = $hash } | ConvertTo-Json | Set-Content -Path $secretPath -Encoding UTF8
            Write-Host "[OK]   Stored admin bcrypt in $secretPath" -ForegroundColor Green
          } catch { Write-Host "[WARN] Could not persist admin bcrypt: $($_.Exception.Message)" -ForegroundColor Yellow }
        } else { throw "bcrypt generation failed" }
      } catch {
        Write-Host "[WARN] ADMIN_PASSWORD_BCRYPT is not set. Please rerun and complete the password prompt." -ForegroundColor Yellow
        if ($script:LastBcryptLog) {
          Write-Host "[DIAG] bcrypt helper details:" -ForegroundColor Yellow
          if ($script:LastBcryptLog.goVersion) { Write-Host ("        goVersion = {0}" -f $script:LastBcryptLog.goVersion) -ForegroundColor DarkYellow }
          if ($null -ne $script:LastBcryptLog.exitCode) { Write-Host ("        exitCode = {0}" -f $script:LastBcryptLog.exitCode) -ForegroundColor DarkYellow }
          if ($script:LastBcryptLog.out) { Write-Host "        go run output:" -ForegroundColor DarkYellow; ($script:LastBcryptLog.out | Out-String).Trim().Split("`n") | ForEach-Object { Write-Host ("          " + $_) -ForegroundColor DarkGray } }
        }
      }
    }
  }
  Write-Host ("[INFO] Auth env: COOKIE_DOMAIN={0} COOKIE_SECURE={1} ACCESS_TTL={2} REFRESH_TTL={3}" -f `
    ($env:COOKIE_DOMAIN), ($env:COOKIE_SECURE), ($env:JWT_ACCESS_TTL), ($env:JWT_REFRESH_TTL)) -ForegroundColor Cyan
}

# Reset admin auth locally: new random password, bcrypt, new JWT secret, print and persist
function Reset-AdminAuth {
  param([ValidateSet('local','prod')][string]$Mode = 'local')
  try {
    # Username fixed
    $env:ADMIN_USERNAME = 'admin'
    # Local-friendly cookies by default when Mode=local
    if ($Mode -eq 'local') {
      try { Remove-Item Env:COOKIE_DOMAIN -ErrorAction SilentlyContinue } catch {}
      $env:COOKIE_SECURE = 'false'
      if (-not $env:JWT_ACCESS_TTL -or $env:JWT_ACCESS_TTL.Trim() -eq '') { $env:JWT_ACCESS_TTL = '24h' }
      if (-not $env:JWT_REFRESH_TTL -or $env:JWT_REFRESH_TTL.Trim() -eq '') { $env:JWT_REFRESH_TTL = '720h' }
    }
    # Generate random password and bcrypt
    $plain = New-RandomPassword -Len 18
    # Dev: let server hash plain on startup. Открытый пароль работает только
    # вместе с ADMIN_ALLOW_PLAIN_PASSWORD — см. LoadConfig в auth.go.
    $env:ADMIN_PASSWORD_PLAIN = $plain
    $env:ADMIN_ALLOW_PLAIN_PASSWORD = '1'
    try { Remove-Item Env:ADMIN_PASSWORD_BCRYPT -ErrorAction SilentlyContinue } catch {}
    # Generate new JWT secret (32 bytes base64url)
    $env:JWT_SECRET = New-Base64Url -Size 32

    Write-Host "[INFO] Using ADMIN_PASSWORD_PLAIN (server will bcrypt on startup)." -ForegroundColor Yellow

    # Print new env for admin
    Write-Host "[ADMIN ENV] Use the following values (already exported in this session):" -ForegroundColor Cyan
    Write-Host ("  ADMIN_USERNAME={0}" -f $env:ADMIN_USERNAME)
    if ($env:ADMIN_PASSWORD_PLAIN) { Write-Host ("  ADMIN_PASSWORD_PLAIN=(set) [dev]" ) } elseif ($env:ADMIN_PASSWORD_BCRYPT) { Write-Host ("  ADMIN_PASSWORD_BCRYPT={0}" -f $env:ADMIN_PASSWORD_BCRYPT) }
    Write-Host ("  JWT_SECRET={0}" -f $env:JWT_SECRET)
    Write-Host ("  COOKIE_DOMAIN={0}" -f ($env:COOKIE_DOMAIN))
    Write-Host ("  COOKIE_SECURE={0}" -f ($env:COOKIE_SECURE))
    Write-Host ("  JWT_ACCESS_TTL={0}" -f ($env:JWT_ACCESS_TTL))
    Write-Host ("  JWT_REFRESH_TTL={0}" -f ($env:JWT_REFRESH_TTL))
    Write-Host "[INFO] Temporary plaintext admin password (for login):" -ForegroundColor Yellow
    Write-Host ("  username=admin  password={0}" -f $plain) -ForegroundColor Yellow

    return $true
  } catch { Write-Host ("[ERROR] Reset-AdminAuth failed: {0}" -f $_.Exception.Message) -ForegroundColor Red; return $false }
}

function Get-ProcIdsByPort {
  param([int]$Port)
  $lines = netstat -ano -p TCP | Select-String ":$Port " | ForEach-Object { $_.ToString() }
  $procIds = @()
  foreach ($ln in $lines) {
    $parts = ($ln -split "\s+") | Where-Object { $_ -ne '' }
    if ($parts.Count -ge 5) {
      $procId = [int]$parts[-1]
      if ($procId -gt 0) { $procIds += $procId }
    }
  }
  $procIds | Select-Object -Unique
}

function Stop-ByPort {
  param([int]$Port)
  $procIds = Get-ProcIdsByPort -Port $Port
  foreach ($procId in $procIds) {
    try { Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue } catch {}
  }
}

function Stop-Client {
  # Kill any dotnet process that's running our WPF project (ChillHub.csproj)
  try {
    # dotnet run via csproj
    $procsCsproj = Get-CimInstance Win32_Process | Where-Object {
      $_.Name -match '^dotnet(\.exe)?$' -and $_.CommandLine -match 'ChillHub\\ChillHub\.csproj'
    }
    foreach ($p in $procsCsproj) { try { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }

    # dotnet exec ChillHub.dll
    $procsDll = Get-CimInstance Win32_Process | Where-Object {
      $_.Name -match '^dotnet(\.exe)?$' -and $_.CommandLine -match 'ChillHub\\bin\\.*ChillHub\.dll'
    }
    foreach ($p in $procsDll) { try { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }

    # powershell wrapper window that launched dotnet run in launcher/ChillHub (skip current shell)
    $currentPid = $PID
    $psWrappers = Get-CimInstance Win32_Process | Where-Object {
      $_.Name -match '^powershell(\.exe)?$' -and $_.ProcessId -ne $currentPid -and $_.CommandLine -match 'launcher\\ChillHub' -and $_.CommandLine -match 'dotnet run'
    }
    foreach ($p in $psWrappers) { try { Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue } catch {} }

    # Also kill compiled WPF process if it exists
    try { Get-Process -Name 'ChillHub' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue } catch {}
  } catch {}
}

function Start-All {
  param([string]$contentRoot, [string]$gamesPath)
  Write-Host "Starting all processes..." -ForegroundColor Green

  # Apply env for Go servers
  & "$scriptDir\env.ps1" -ContentRoot $contentRoot | Out-Host
  # Enforce auth env exclusivity: if plain set, clear bcrypt
  if ($env:ADMIN_PASSWORD_PLAIN -and $env:ADMIN_PASSWORD_PLAIN.Trim() -ne '') {
    try { Remove-Item Env:ADMIN_PASSWORD_BCRYPT -ErrorAction SilentlyContinue } catch {}
    $env:ADMIN_ALLOW_PLAIN_PASSWORD = '1'
  }
  try {
    $gc = Get-Command go -ErrorAction SilentlyContinue
    if ($gc -and $gc.Path) { $script:GoExe = $gc.Path }
    if (-not $script:GoExe) {
      try { $wp = (& where.exe go 2>$null); if ($wp) { $script:GoExe = ($wp -split "`r?`n")[0] } } catch {}
    }
    $gv = (& go version)
    if ($gv) {
      if ($script:GoExe) { Write-Host ("[INFO] `"go version`": $gv (path: $script:GoExe)") -ForegroundColor Cyan }
      else { Write-Host ("[INFO] `"go version`": $gv") -ForegroundColor Cyan }
    }
  } catch {}

  # Ensure ports are free (kill anything bound to 55700/55777)
  Stop-ByPort -Port 55700
  Stop-ByPort -Port 55777

  # Ensure previous client instance is not running
  Stop-Client

  if ($Env -eq 'local') {
    # Ensure Go module deps are present before go run
    Push-Location (Join-Path $repoRoot 'server')
    try { go mod tidy } finally { Pop-Location }

    # Configure auth env (may need bcrypt helper that relies on Go toolchain)
    Set-AuthEnv -Mode $Env
    # Start API server
    $global:apiProc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit","-Command","`"Push-Location '$repoRoot\server'; Write-Host '[API] http://localhost:55700' -ForegroundColor Yellow; go run ./cmd/api`"" -PassThru
    # Start Admin server
    $global:adminProc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit","-Command","`"Push-Location '$repoRoot\server'; Write-Host '[ADMIN] http://localhost:55777/admin' -ForegroundColor Yellow; go run ./cmd/admin`"" -PassThru
  } else {
    # Configure auth env for prod-like testing
    Set-AuthEnv -Mode $Env
    Write-Host "[INFO] Env=prod: skipping local API/Admin servers (use remote nginx/backend)." -ForegroundColor Yellow
    $global:apiProc = $null
    $global:adminProc = $null
  }

  # Start Client (WPF)
  $gp = $gamesPath
  if ([string]::IsNullOrWhiteSpace($gp)) {
    $defaultD = "D:\\Games\\ChillHub"; $defaultC = "C:\\Games\\ChillHub"
    if (Test-Path 'D:\\') { $gp = $defaultD } else { $gp = $defaultC }
  }
  if ($SetClientConfig) {
    Set-ChillHubClientConfig -ApiBaseUrl $ApiBase -GamesPath $gp
  }
  $env:ChillHub_GAMES_PATH = $gp
  Write-Host "[CLIENT] WPF starting (GamesPath=$gp)" -ForegroundColor Yellow
  # Important: escape $ in child command so parent PowerShell doesn't expand it here (would turn into '=1')
  $msbuildAnalyzerProps = ""
  if ($Env -eq 'local') {
    # Suppress analyzer-driven warnings (e.g., StyleCop SA1515) during local runs only
    $msbuildAnalyzerProps = "-p:RunAnalyzersDuringBuild=false -p:RunAnalyzersDuringLiveAnalysis=false -p:TreatWarningsAsErrors=false"
  }
  $clientCmd = "Push-Location '" + (Join-Path $repoRoot 'launcher\ChillHub') + "'; `$env:YL_DEV_SKIP_SELF_UPDATE=1; dotnet run --project .\ChillHub.csproj $msbuildAnalyzerProps"
  $global:clientProc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit","-Command",$clientCmd -PassThru

  $apiPid   = if ($apiProc) { $apiProc.Id } else { '-' }
  $adminPid = if ($adminProc) { $adminProc.Id } else { '-' }
  Write-Host "API PID=$apiPid, Admin PID=$adminPid, Client PID=$($clientProc.Id)" -ForegroundColor Cyan
}

function Stop-All {
  Write-Host "Stopping all processes..." -ForegroundColor DarkYellow
  # First, ensure client app is killed, even if its console is gone
  Stop-Client
  foreach ($p in @($global:apiProc, $global:adminProc, $global:clientProc)) {
    try {
      if ($p -and -not $p.HasExited) {
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        try { Wait-Process -Id $p.Id -Timeout 3 -ErrorAction SilentlyContinue } catch {}
      }
    } catch {}
  }
  # Fallback: ensure ports are freed
  Stop-ByPort -Port 55700
  Stop-ByPort -Port 55777
  # Fallback: ensure client is stopped
  Stop-Client
}

# Initial start
# Optional: build Go servers (Windows dev binaries) before running
if ($BuildServers) {
  Write-Host "[INFO] Building Go servers (windows/amd64)..." -ForegroundColor Cyan
  Push-Location (Join-Path $repoRoot 'server')
  $env:GOOS='windows'; $env:GOARCH='amd64'; $env:CGO_ENABLED='0'
  go mod tidy
  go build -o (Join-Path $repoRoot 'build\dev\api.exe') ./cmd/api
  go build -o (Join-Path $repoRoot 'build\dev\admin.exe') ./cmd/admin
  Pop-Location
  Write-Host "[OK]   Go servers built" -ForegroundColor Green
}
# Optional: reset admin auth before starting processes
if ($ResetAdminAuth) {
  Write-Host "[INFO] Pre-run: resetting admin password and JWT secret..." -ForegroundColor Cyan
  $null = Reset-AdminAuth -Mode $Env
}
Start-All -contentRoot $ContentRoot -gamesPath $GamesPath

# Control loop
$running = $true
$isRestarting = $false
while ($running) {
  $kCyr  = [string][char]0x043A  # 'к'
  $yCyr  = [string][char]0x0439  # 'й'
  $zCyr  = [string][char]0x0437  # 'з'
  $prompt = "Enter command ([r/{0}] restart, [p/{1}] reset-pass, [q/{2}] quit)" -f $kCyr, $zCyr, $yCyr
  $cmd = (Read-Host $prompt).Trim()
  $cmdL = $cmd.ToLower()
  switch ($cmdL) {
    'r' {
      if ($isRestarting) { continue }
      $isRestarting = $true
      try {
        Stop-All
        Start-Sleep -Milliseconds 400
        Start-All -contentRoot $ContentRoot -gamesPath $GamesPath
      } finally { $isRestarting = $false }
    }
    $kCyr {
      if ($isRestarting) { continue }
      $isRestarting = $true
      try {
        Stop-All
        Start-Sleep -Milliseconds 400
        Start-All -contentRoot $ContentRoot -gamesPath $GamesPath
      } finally { $isRestarting = $false }
    }
    'p' {
      if ($isRestarting) { continue }
      $isRestarting = $true
      try {
        Write-Host "Resetting admin password and JWT secret..." -ForegroundColor Cyan
        $ok = Reset-AdminAuth -Mode $Env
        if ($ok) {
          # Restart to apply env to child processes
          Stop-All
          Start-Sleep -Milliseconds 400
          Start-All -contentRoot $ContentRoot -gamesPath $GamesPath
        }
      } finally { $isRestarting = $false }
    }
    $zCyr {
      if ($isRestarting) { continue }
      $isRestarting = $true
      try {
        Write-Host "Resetting admin password and JWT secret..." -ForegroundColor Cyan
        $ok = Reset-AdminAuth -Mode $Env
        if ($ok) {
          Stop-All
          Start-Sleep -Milliseconds 400
          Start-All -contentRoot $ContentRoot -gamesPath $GamesPath
        }
      } finally { $isRestarting = $false }
    }
    'q' { $running = $false }
    $yCyr { $running = $false }
    'quit' { $running = $false }
    'exit' { $running = $false }
    default { Write-Host ("Unknown command. Use r/{0} or q/{1}." -f $kCyr, $yCyr) -ForegroundColor Red }
  }
}

# Cleanup on exit
Stop-All
Write-Host "Bye." -ForegroundColor DarkGray
