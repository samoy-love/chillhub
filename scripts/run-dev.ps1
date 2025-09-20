param(
  [string]$ContentRoot,
  [string]$GamesPath,
  [ValidateSet('local','prod')]
  [string]$Env = 'local',
  [switch]$SetClientConfig,
  [switch]$BuildServers
)

# Ensure Unicode I/O (fix mojibake like 'Рє')
try {
  [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($true)
  [Console]::InputEncoding  = [System.Text.UTF8Encoding]::new($true)
} catch {}

# Helper: Update client config in %LOCALAPPDATA%\ChillHub
function Set-ChillHubClientConfig {
  param([string]$ApiBaseUrl, [string]$GamesPath)
  try {
    $configDir = Join-Path $env:LOCALAPPDATA 'ChillHub'
    if (!(Test-Path $configDir)) { New-Item -ItemType Directory -Path $configDir | Out-Null }
    $configPath = Join-Path $configDir 'config.json'
    $cfg = @{
      GamesPath = $GamesPath
      DownloadThreads = 8
      Theme = 'dark'
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
#   q + Enter  -> Quit

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Split-Path -Parent $scriptDir

# Environment endpoint selection
$ApiBaseLocal = 'http://localhost:55700'
$ApiBaseProd  = 'https://launcher.samoy.love'
$ApiBase = if ($Env -eq 'prod') { $ApiBaseProd } else { $ApiBaseLocal }
Write-Host "[INFO] Environment: $Env" -ForegroundColor Cyan
Write-Host "[INFO] ApiBaseUrl:  $ApiBase" -ForegroundColor Cyan

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

  # Ensure ports are free (kill anything bound to 55700/55777)
  Stop-ByPort -Port 55700
  Stop-ByPort -Port 55777

  # Ensure previous client instance is not running
  Stop-Client

  # Start API server
  $global:apiProc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit","-Command","`"Push-Location '$repoRoot\\server'; Write-Host '[API] http://localhost:55700' -ForegroundColor Yellow; go run ./cmd/api`"" -PassThru

  # Start Admin server
  $global:adminProc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit","-Command","`"Push-Location '$repoRoot\\server'; Write-Host '[ADMIN] http://localhost:55777/admin' -ForegroundColor Yellow; go run ./cmd/admin`"" -PassThru

  # Start Client (WPF)
  $gp = $gamesPath
  if (-not $gp -or $gp -eq '') {
    $defaultD = "D:\\Games\\ChillHub"; $defaultC = "C:\\Games\\ChillHub"
    if (Test-Path 'D:\\') { $gp = $defaultD } else { $gp = $defaultC }
  }
  if ($SetClientConfig) {
    Set-ChillHubClientConfig -ApiBaseUrl $ApiBase -GamesPath $gp
  }
  $env:ChillHub_GAMES_PATH = $gp
  Write-Host "[CLIENT] WPF starting (GamesPath=$gp)" -ForegroundColor Yellow
  $clientCmd = "Push-Location '" + (Join-Path $repoRoot 'launcher\ChillHub') + "'; dotnet run --project .\ChillHub.csproj"
  $global:clientProc = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoExit","-Command",$clientCmd -PassThru

  Write-Host "API PID=$($apiProc.Id), Admin PID=$($adminProc.Id), Client PID=$($clientProc.Id)" -ForegroundColor Cyan
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
  go build -o (Join-Path $repoRoot 'build\dev\api.exe') ./cmd/api
  go build -o (Join-Path $repoRoot 'build\dev\admin.exe') ./cmd/admin
  Pop-Location
  Write-Host "[OK]   Go servers built" -ForegroundColor Green
}
Start-All -contentRoot $ContentRoot -gamesPath $GamesPath

# Control loop
$running = $true
$isRestarting = $false
while ($running) {
  $kCyr  = [string][char]0x043A  # 'к'
  $prompt = "Enter command ([r/{0}] restart, [q] quit)" -f $kCyr
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
    'q' { $running = $false }
    'quit' { $running = $false }
    'exit' { $running = $false }
    default { Write-Host ("Unknown command. Use r/{0} or q." -f $kCyr) -ForegroundColor Red }
  }
}

# Cleanup on exit
Stop-All
Write-Host "Bye." -ForegroundColor DarkGray
