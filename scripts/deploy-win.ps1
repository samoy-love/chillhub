param(
    [Parameter(Mandatory=$true)] [string]$SshHost,
    [Parameter(Mandatory=$true)] [string]$SshUser,
    [Parameter(Mandatory=$true)] [string]$KeyPath,
    [string]$Branch = "main",
    [string]$DownloadsDir = "",
    [string]$JwtSecret = "",
    [string]$AdminUser = "admin",
    [string]$AdminPasswordBcrypt = "",
    [string]$AdminPasswordPlain = "",
    [string]$CookieDomain = "launcher.samoy.love",
    [string]$CookieSecure = "true",
    [int]$Parallel = 8,
    [string]$SiteBaseUrl = "https://launcher.samoy.love",
    [switch]$StrictHostKey,
    [switch]$FailOnManifestMismatch,
    [switch]$StartAtRemote,
    [switch]$NoColor
)

$ErrorActionPreference = "Stop"
$UseColor = -not $NoColor

# Console printers (color-aware)
function Write-Info($msg)  { if ($UseColor) { Write-Host "[deploy] $msg" -ForegroundColor Cyan } else { Write-Host "[deploy] $msg" } }
function Write-Warn($msg)  { if ($UseColor) { Write-Host "[warn ] $msg" -ForegroundColor Yellow } else { Write-Host "[warn ] $msg" } }
function Write-Err($msg)   { if ($UseColor) { Write-Host "[error] $msg" -ForegroundColor Red } else { Write-Host "[error] $msg" } }
function Write-Ok($msg)    { if ($UseColor) { Write-Host "[ ok  ] $msg" -ForegroundColor Green } else { Write-Host "[ ok  ] $msg" } }
function Write-Section($msg) {
  $line = '------------------------------------------------------------'
  if ($UseColor) {
    Write-Host $line -ForegroundColor DarkGray
    Write-Host "  $msg" -ForegroundColor Magenta
    Write-Host $line -ForegroundColor DarkGray
  } else {
    Write-Host $line
    Write-Host "  $msg"
    Write-Host $line
  }
}

# Define helper to check for required commands
function Test-CommandAvailable {
  param([Parameter(Mandatory=$true)][string]$Name)
  $cmd = Get-Command $Name -ErrorAction SilentlyContinue
  if (-not $cmd) { throw "Required command not found: $Name" }
}

# Preflight: required tools on Windows host
Test-CommandAvailable go
Test-CommandAvailable tar

# Resolve an executable from a list of candidate names/paths
function Resolve-Executable {
  param([string[]]$Candidates)
  foreach ($c in $Candidates) {
    try {
      $cmd = Get-Command $c -ErrorAction SilentlyContinue
      if ($cmd) { return $cmd.Source }
      if (Test-Path -LiteralPath $c) { return $c }
    } catch {}
  }
  return $null
}

$SSH = Resolve-Executable @(
  'ssh',
  'ssh.exe',
  'C:\\Windows\\System32\\OpenSSH\\ssh.exe',
  'C:\\Program Files\\Git\\usr\\bin\\ssh.exe',
  'C:\\Program Files\\Git\\mingw64\\bin\\ssh.exe'
)
$SCP = Resolve-Executable @(
  'scp',
  'scp.exe',
  'C:\\Windows\\System32\\OpenSSH\\scp.exe',
  'C:\\Program Files\\Git\\usr\\bin\\scp.exe',
  'C:\\Program Files\\Git\\mingw64\\bin\\scp.exe'
)
if (-not $SSH -or -not $SCP) {
  throw "OpenSSH client not found (ssh/scp). Install 'OpenSSH Client' Windows Feature or ensure Git for Windows' ssh/scp are in PATH."
}

# Validate key path
if (-not (Test-Path -LiteralPath $KeyPath)) {
  throw "SSH key not found: $KeyPath"
}

# Paths
$RepoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$BuildRoot = Join-Path $RepoRoot "build"
$BinDir    = Join-Path $BuildRoot "bin"
$SiteDir   = Join-Path $BuildRoot "site"
$AdminUIDir= Join-Path $BuildRoot "launcher_admin_ui"
$SystemdDir= Join-Path $BuildRoot "systemd"
$DeployDir = Join-Path $BuildRoot "deploy"

# Clean and re-create build tree
if (Test-Path $BuildRoot) { Remove-Item -Recurse -Force $BuildRoot }
New-Item -ItemType Directory -Force -Path $BinDir, $SiteDir, $AdminUIDir, $SystemdDir, $DeployDir | Out-Null

# Optional: git checkout a branch locally (only if repo is git)
try {
  $isGit = (git -C $RepoRoot rev-parse --is-inside-work-tree 2>$null) -eq "true"
  if ($isGit) {
    Write-Info "Checking out branch $Branch"
    git -C $RepoRoot fetch --all --prune | Out-Null
    git -C $RepoRoot checkout $Branch | Out-Null
    git -C $RepoRoot pull --ff-only | Out-Null
  }
} catch {}

if ($StartAtRemote) {
  Write-Section "StartAtRemote"
  Write-Warn "Skipping local build and upload; proceeding directly to remote deploy"
  $sshCommon = if ($StrictHostKey) {
    @(
      "-i", $KeyPath,
      "-o", "StrictHostKeyChecking=accept-new",
      "-o", "ConnectTimeout=10",
      "-o", "ServerAliveInterval=15",
      "-o", "ServerAliveCountMax=4"
    )
  } else {
    @(
      "-i", $KeyPath,
      "-o", "StrictHostKeyChecking=no",
      "-o", "ConnectTimeout=10",
      "-o", "ServerAliveInterval=15",
      "-o", "ServerAliveCountMax=4"
    )
  }
  $Remote = "${SshUser}@${SshHost}"
  # Ensure remote deploy dir exists and upload the latest nginx site config even in StartAtRemote mode
  try {
    & $SSH @sshCommon $Remote "mkdir -p deploy/deploy" | Out-Null
    $localConf = Join-Path $RepoRoot "deploy/launcher.conf"
    if (Test-Path -LiteralPath $localConf) {
      & $SCP @sshCommon $localConf "${Remote}:deploy/deploy/launcher.conf" | Out-Null
    }
  } catch {
    Write-Warn "Failed to pre-upload launcher.conf in StartAtRemote mode: $($_.Exception.Message)"
  }
} else {
  # Auto-detect remote architecture to build correct binaries (avoids Exec format errors)
  $sshCommonDetect = if ($StrictHostKey) {
    @("-i", $KeyPath, "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4")
  } else {
    @("-i", $KeyPath, "-o", "StrictHostKeyChecking=no", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4")
  }
  $remoteDetect = "${SshUser}@${SshHost}"
  $unameM = ""
  try {
    $unameM = (& $SSH @sshCommonDetect $remoteDetect "/bin/sh -c 'uname -m'" 2>$null | Select-Object -First 1).ToString().Trim().ToLower()
  } catch {}
  $goArch = "amd64"
  switch -regex ($unameM) {
    '^(aarch64|arm64)$' { $goArch = 'arm64'; break }
    '^(x86_64|amd64)$'  { $goArch = 'amd64'; break }
  }
  Write-Section ("Build (linux/amd64 + linux/arm64)")
  Write-Info ("Building Go servers for both linux/amd64 and linux/arm64 (server will pick correct arch)")
  $prevGOOS = $env:GOOS; $prevGOARCH = $env:GOARCH; $prevCGO = $env:CGO_ENABLED
  Push-Location (Join-Path $RepoRoot "server")
  try {
    go mod tidy
    # Build amd64
    $env:GOOS = "linux"; $env:GOARCH = "amd64"; $env:CGO_ENABLED = "0"
    go build -o (Join-Path $BinDir "api.amd64")   ./cmd/api
    go build -o (Join-Path $BinDir "admin.amd64") ./cmd/admin
    # Build arm64
    $env:GOOS = "linux"; $env:GOARCH = "arm64"; $env:CGO_ENABLED = "0"
    go build -o (Join-Path $BinDir "api.arm64")   ./cmd/api
    go build -o (Join-Path $BinDir "admin.arm64") ./cmd/admin
  } finally {
    Pop-Location
    # Restore env
    $env:GOOS = $prevGOOS; $env:GOARCH = $prevGOARCH; $env:CGO_ENABLED = $prevCGO
  }

  # Prepare bundle (copy landing, admin_ui, systemd, nginx config)
  Write-Section "Prepare deploy bundle"
  Write-Info "Preparing deploy bundle"
  New-Item -ItemType Directory -Force -Path $SiteDir,$AdminUIDir,$SystemdDir | Out-Null
  $landingDir  = (Join-Path $RepoRoot 'landing')
  $adminUIDirS = (Join-Path (Join-Path $RepoRoot 'server') 'admin_ui')
  $systemdSrc  = (Join-Path (Join-Path $RepoRoot 'deploy') 'systemd')

  Copy-Item -Path (Join-Path $landingDir  '*') -Destination $SiteDir    -Recurse -Force
  Copy-Item -Path (Join-Path $adminUIDirS '*') -Destination $AdminUIDir -Recurse -Force
  if (Test-Path $systemdSrc) {
    Copy-Item -Path (Join-Path $systemdSrc '*') -Destination $SystemdDir -Recurse -Force
  }
  Copy-Item -Path (Join-Path (Join-Path $RepoRoot 'deploy') 'launcher.conf') -Destination (Join-Path $DeployDir 'launcher.conf') -Force

  # Upload bundle via scp (ensure remote dirs exist, avoid wildcard expansion issues)
  Write-Section "Upload bundle"
  Write-Info "Preparing remote directory structure"
  $sshCommon = if ($StrictHostKey) {
    @(
      "-i", $KeyPath,
      "-o", "StrictHostKeyChecking=accept-new",
      "-o", "ConnectTimeout=10",
      "-o", "ServerAliveInterval=15",
      "-o", "ServerAliveCountMax=4"
    )
  } else {
    @(
      "-i", $KeyPath,
      "-o", "StrictHostKeyChecking=no",
      "-o", "ConnectTimeout=10",
      "-o", "ServerAliveInterval=15",
      "-o", "ServerAliveCountMax=4"
    )
  }
  $Remote = "${SshUser}@${SshHost}"
  # Quick SSH preflight to catch key-permissions issues early
  try {
    & $SSH @sshCommon $Remote "true" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "ssh exited with rc=$LASTEXITCODE" }
  } catch {
    Write-Err "SSH preflight failed. If you see 'UNPROTECTED PRIVATE KEY FILE' or 'Permission denied (publickey)', fix key ACLs on Windows:"
    Write-Err "  icacls \"$KeyPath\" /inheritance:r"
    Write-Err "  icacls \"$KeyPath\" /grant:r \"$env:USERNAME:R\""
    throw
  }
  & $SSH @sshCommon $Remote "mkdir -p deploy/site deploy/launcher_admin_ui deploy/bin deploy/systemd deploy/deploy" | Out-Null
  # Clean previous contents to avoid stale or malformed entries (e.g., accidental Windows-path directories)
  & $SSH @sshCommon $Remote "rm -rf deploy/site/* deploy/launcher_admin_ui/* deploy/bin/* deploy/systemd/*" | Out-Null

# Helpers for copy operations (parallel scp jobs)
function Invoke-ScpItem {
  param(
    [Parameter(Mandatory=$true)][string]$Path,
    [Parameter(Mandatory=$true)][string]$RemoteDir
  )
  & $SCP @sshCommon -r "$Path" "${Remote}:$RemoteDir/"
}

function Copy-DirRemoteParallel {
  param(
    [Parameter(Mandatory=$true)][string]$LocalDir,
    [Parameter(Mandatory=$true)][string]$RemoteDir,
    [int]$MaxParallel = 8
  )
  Write-Info "scp (parallel $MaxParallel) -> $RemoteDir"
  # Ensure remote directory exists
  & $SSH @sshCommon $Remote "mkdir -p '$RemoteDir'" | Out-Null
  # Enumerate immediate children to balance scp jobs
  $children = Get-ChildItem -LiteralPath $LocalDir -Force -ErrorAction SilentlyContinue
  if (-not $children) { return }
  $jobs = @()
  $anyFail = $false
  foreach ($it in $children) {
    $p = $it.FullName
    # throttle jobs
    while ($jobs.Count -ge $MaxParallel) {
      $completed = Wait-Job -Job $jobs -Any -Timeout 5
      if ($completed) {
        $done = $jobs | Where-Object { $_.State -ne 'Running' }
        foreach ($j in $done) {
          Receive-Job -Job $j | ForEach-Object { Write-Host $_ }
          Remove-Job -Job $j -Force | Out-Null
        }
        $jobs = $jobs | Where-Object { $_.State -eq 'Running' }
      }
    }
    $jobs += Start-Job -ScriptBlock {
      param($scp,$sshArgs,$remote,$itemPath,$remoteDir)
      $leaf = Split-Path -Leaf $itemPath
      Write-Output ("[copy] START {0} -> {1}" -f $leaf, $remoteDir)
      # Use quiet scp and legacy protocol (-O) to avoid slow SFTP mode; suppress console I/O for speed
      & $scp @sshArgs -q -O -r "$itemPath" "${remote}:$remoteDir/" *> $null
      $rc = $LASTEXITCODE
      if ($rc -eq 0) { Write-Output ("[copy] DONE  {0}" -f $leaf) } else { Write-Output ("[copy] FAIL  {0} (rc={1})" -f $leaf,$rc); throw "scp failed ($rc) for $leaf" }
    } -ArgumentList $SCP,$sshCommon,$Remote,$p,$RemoteDir
  }
  # Drain remaining jobs
  if ($jobs.Count -gt 0) {
    Wait-Job -Job $jobs | Out-Null
    foreach ($j in $jobs) {
      if ($j.State -eq 'Failed') { $anyFail = $true }
      Receive-Job -Job $j | ForEach-Object { Write-Host $_ }
      Remove-Job -Job $j -Force | Out-Null
    }
  }
  if ($anyFail) { throw "One or more scp jobs failed for $RemoteDir" }
}

function Copy-FileRemote($LocalFile, $RemotePath) {
  Write-Info "scp -> $RemotePath"
  & $SCP @sshCommon "$LocalFile" "${Remote}:$RemotePath"
}

Write-Info ("Uploading bundle to {0}:~/deploy" -f $Remote)
# Package landing, admin_ui, bin, and systemd as tar.gz to avoid Windows path issues
$siteTgz    = Join-Path $env:TEMP "site-$([Guid]::NewGuid().ToString('N')).tgz"
$adminTgz   = Join-Path $env:TEMP "adminui-$([Guid]::NewGuid().ToString('N')).tgz"
$binTgz     = Join-Path $env:TEMP "bin-$([Guid]::NewGuid().ToString('N')).tgz"
$systemdTgz = if (Test-Path $SystemdDir) { Join-Path $env:TEMP "systemd-$([Guid]::NewGuid().ToString('N')).tgz" } else { $null }
Write-Info "Creating site archive"
& tar -czf $siteTgz -C $landingDir .
Write-Info "Creating admin_ui archive"
& tar -czf $adminTgz -C $adminUIDirS .
Write-Info "Creating bin archive"
& tar -czf $binTgz -C $BinDir .
if ($systemdTgz) {
  Write-Info "Creating systemd archive"
  & tar -czf $systemdTgz -C $SystemdDir .
}

# Upload archives
Copy-FileRemote $siteTgz  'deploy/site.tgz'
Copy-FileRemote $adminTgz 'deploy/admin_ui.tgz'
Copy-FileRemote $binTgz   'deploy/bin.tgz'
if ($systemdTgz) { Copy-FileRemote $systemdTgz 'deploy/systemd.tgz' }

# Extract on remote and clean archives
& $SSH @sshCommon $Remote "/bin/bash -lc 'mkdir -p deploy/site deploy/launcher_admin_ui deploy/bin deploy/systemd; rm -rf deploy/site/* deploy/launcher_admin_ui/* deploy/bin/* deploy/systemd/*; tar -xzf deploy/site.tgz -C deploy/site; tar -xzf deploy/admin_ui.tgz -C deploy/launcher_admin_ui; tar -xzf deploy/bin.tgz -C deploy/bin; if [ -f deploy/systemd.tgz ]; then tar -xzf deploy/systemd.tgz -C deploy/systemd; fi; rm -f deploy/site.tgz deploy/admin_ui.tgz deploy/bin.tgz deploy/systemd.tgz'" | Out-Null

# Remove local temp archives
foreach ($f in @($siteTgz, $adminTgz, $binTgz, $systemdTgz)) { if ($f) { Remove-Item -Force $f -ErrorAction SilentlyContinue } }

# Upload config
Copy-FileRemote (Join-Path $DeployDir 'launcher.conf') 'deploy/deploy/launcher.conf'

  # Handle DownloadsDir
  # If local path exists, upload its contents to remote /var/www/site/downloads
  # Otherwise, pass it to the remote script to sync server-side if it's a server path
  $serverDownloads = ""
  if ($DownloadsDir -and (Test-Path -LiteralPath $DownloadsDir)) {
    Write-Section "Downloads"
    Write-Info "Creating downloads archive and uploading to remote /var/www/site/downloads"
    $downloadsTgz = Join-Path $env:TEMP "downloads-$([Guid]::NewGuid().ToString('N')).tgz"
    & tar -czf $downloadsTgz -C $DownloadsDir .
    Copy-FileRemote $downloadsTgz 'deploy/downloads.tgz'
    & $SSH @sshCommon $Remote "/bin/bash -lc 'mkdir -p /var/www/site/downloads; rm -rf /var/www/site/downloads/*; tar -xzf deploy/downloads.tgz -C /var/www/site/downloads; rm -f deploy/downloads.tgz'" | Out-Null
    # Remove local downloads archive
    if (Test-Path $downloadsTgz) { Remove-Item -Force $downloadsTgz -ErrorAction SilentlyContinue }
  } else {
    $serverDownloads = $DownloadsDir
  }
}

# Remote deploy script (executed on server)
$remoteScript = @'
set -eux
DEPLOY_DIR="$HOME/deploy"
SITE_DIR="/var/www/site"
LAUNCHER_DIR="/var/www/launcher"
OPT_DIR="/opt/chillhub"
NGINX_SITE_AVAILABLE="/etc/nginx/sites-available/launcher.conf"
NGINX_SITE_ENABLED="/etc/nginx/sites-enabled/launcher.conf"
SITE_BASE="%%SITE_BASE_URL%%"
FAIL_ON_MISMATCH="%%FAIL_ON_MISMATCH%%"
MISM_TOTAL=0

# Pre-checks
shopt -s expand_aliases
if ! sudo -n true 2>/dev/null; then
  echo "[error] sudo requires a password; please configure NOPASSWD for user $(whoami) or run with appropriate privileges"
  exit 1
fi
alias sudo='sudo -n'
if ! command -v rsync >/dev/null 2>&1; then
  echo "[error] rsync is not installed on the server"
  exit 1
fi

sudo mkdir -p "$SITE_DIR" "$LAUNCHER_DIR/admin_ui" "$OPT_DIR"

# Sync landing site
sudo rsync -a --delete "$DEPLOY_DIR/site/" "$SITE_DIR/"
# Sync Admin UI static only
sudo rsync -a --delete "$DEPLOY_DIR/launcher_admin_ui/" "$LAUNCHER_DIR/admin_ui/"

# Diagnostics: compare source vs destination for key files
if [ -f "$DEPLOY_DIR/site/index.html" ] && [ -f "$SITE_DIR/index.html" ]; then
  echo "[diag] site/index.html src=$(sha256sum \"$DEPLOY_DIR/site/index.html\" | awk '{print $1}') dst=$(sha256sum \"$SITE_DIR/index.html\" | awk '{print $1}')"
fi
if [ -f "$DEPLOY_DIR/launcher_admin_ui/admin.js" ] && [ -f "$LAUNCHER_DIR/admin_ui/admin.js" ]; then
  echo "[diag] admin_ui/admin.js src=$(sha256sum \"$DEPLOY_DIR/launcher_admin_ui/admin.js\" | awk '{print $1}') dst=$(sha256sum \"$LAUNCHER_DIR/admin_ui/admin.js\" | awk '{print $1}')"
fi
# List images presence in source and destination (first level)
echo "[diag] images (src)"; ls -1 "$DEPLOY_DIR/site/assets/images" 2>/dev/null | sed -n '1,50p' || true
echo "[diag] images (dst)"; ls -1 "$SITE_DIR/assets/images" 2>/dev/null | sed -n '1,50p' || true

# Install binaries (select arch-specific files if generic names not present)
sudo install -d -m 0755 "$OPT_DIR"
SERVER_ARCH=$(uname -m | tr '[:upper:]' '[:lower:]')
API_SRC="$DEPLOY_DIR/bin/api"
ADMIN_SRC="$DEPLOY_DIR/bin/admin"
if [ ! -f "$API_SRC" ] || [ ! -f "$ADMIN_SRC" ]; then
  case "$SERVER_ARCH" in
    aarch64|arm64)
      [ -f "$DEPLOY_DIR/bin/api.arm64" ] && API_SRC="$DEPLOY_DIR/bin/api.arm64"
      [ -f "$DEPLOY_DIR/bin/admin.arm64" ] && ADMIN_SRC="$DEPLOY_DIR/bin/admin.arm64"
      ;;
    x86_64|amd64)
      [ -f "$DEPLOY_DIR/bin/api.amd64" ] && API_SRC="$DEPLOY_DIR/bin/api.amd64"
      [ -f "$DEPLOY_DIR/bin/admin.amd64" ] && ADMIN_SRC="$DEPLOY_DIR/bin/admin.amd64"
      ;;
  esac
fi
if [ ! -f "$API_SRC" ] || [ ! -f "$ADMIN_SRC" ]; then
  echo "[error] Could not locate binaries to install for $SERVER_ARCH"
  ls -lah "$DEPLOY_DIR/bin" || true
  exit 1
fi
sudo install -m 0755 "$API_SRC" "$OPT_DIR/api"
sudo install -m 0755 "$ADMIN_SRC" "$OPT_DIR/admin"

# Validate binary architecture matches server architecture
SERVER_ARCH=$(uname -m | tr '[:upper:]' '[:lower:]')
BIN_SUMMARY() { f="$1"; if command -v file >/dev/null 2>&1; then file "$f"; else echo "$f (no 'file' utility available)"; fi }
echo "[check] Server arch: $SERVER_ARCH"
echo "[check] api binary: $(BIN_SUMMARY "$OPT_DIR/api")"
echo "[check] admin binary: $(BIN_SUMMARY "$OPT_DIR/admin")"
mismatch=0
case "$SERVER_ARCH" in
  aarch64|arm64)
    if command -v file >/dev/null 2>&1; then
      file "$OPT_DIR/api"   | grep -qiE 'aarch64|arm64' || mismatch=1
      file "$OPT_DIR/admin" | grep -qiE 'aarch64|arm64' || mismatch=1
    fi
    ;;
  x86_64|amd64)
    if command -v file >/dev/null 2>&1; then
      file "$OPT_DIR/api"   | grep -qiE 'x86-64|amd64' || mismatch=1
      file "$OPT_DIR/admin" | grep -qiE 'x86-64|amd64' || mismatch=1
    fi
    ;;
esac
if [ "$mismatch" -ne 0 ]; then
  echo "[error] Installed binaries do not match server architecture ($SERVER_ARCH). Aborting."
  exit 1
fi

# Systemd units (optional)
if [ -d "$DEPLOY_DIR/systemd" ]; then
  # Overwrite units unconditionally
  sudo install -m 0644 "$DEPLOY_DIR/systemd/chillhub-api.service" /etc/systemd/system/chillhub-api.service || true
  sudo install -m 0644 "$DEPLOY_DIR/systemd/chillhub-admin.service" /etc/systemd/system/chillhub-admin.service || true
  # Verify sha256 src/dst and optionally abort if mismatch
  if [ -f "$DEPLOY_DIR/systemd/chillhub-api.service" ]; then
    SRC_API_SHA=$(sha256sum "$DEPLOY_DIR/systemd/chillhub-api.service" | awk '{print $1}')
    DST_API_SHA=$(sha256sum "/etc/systemd/system/chillhub-api.service" | awk '{print $1}')
    echo "[systemd] chillhub-api.service src=$SRC_API_SHA dst=$DST_API_SHA"
  fi
  if [ -f "$DEPLOY_DIR/systemd/chillhub-admin.service" ]; then
    SRC_ADM_SHA=$(sha256sum "$DEPLOY_DIR/systemd/chillhub-admin.service" | awk '{print $1}')
    DST_ADM_SHA=$(sha256sum "/etc/systemd/system/chillhub-admin.service" | awk '{print $1}')
    echo "[systemd] chillhub-admin.service src=$SRC_ADM_SHA dst=$DST_ADM_SHA"
  fi
  if [ -n "$FAIL_ON_MISMATCH" ]; then
    EXIT_MISM=0
    [ -n "$SRC_API_SHA$DST_API_SHA" ] && [ "$SRC_API_SHA" != "$DST_API_SHA" ] && EXIT_MISM=1 && echo "[error] systemd api unit sha mismatch"
    [ -n "$SRC_ADM_SHA$DST_ADM_SHA" ] && [ "$SRC_ADM_SHA" != "$DST_ADM_SHA" ] && EXIT_MISM=1 && echo "[error] systemd admin unit sha mismatch"
    if [ "$EXIT_MISM" -eq 1 ]; then echo "[deploy] Aborting due to systemd unit mismatches"; exit 1; fi
  fi
  sudo systemctl daemon-reload || true
  sudo systemctl enable chillhub-api.service || true
  sudo systemctl enable chillhub-admin.service || true
fi

# Reload services
sudo systemctl daemon-reload || true
sudo systemctl restart chillhub-api.service || true
sudo systemctl restart chillhub-admin.service || true
API_STATE=$(systemctl is-active chillhub-api.service 2>/dev/null || true)
ADM_STATE=$(systemctl is-active chillhub-admin.service 2>/dev/null || true)
echo "[systemd] api=$API_STATE admin=$ADM_STATE"
if [ -n "$FAIL_ON_MISMATCH" ]; then
  if [ "$API_STATE" != "active" ] || [ "$ADM_STATE" != "active" ]; then echo "[deploy] One or more services not active after restart"; exit 1; fi
fi

# Optional: write admin auth env drop-in (from parameters)
ADMIN_DROPIN_DIR="/etc/systemd/system/chillhub-admin.service.d"
JWT_SECRET="%%JWT_SECRET%%"
ADMIN_USER="%%ADMIN_USER%%"
ADMIN_PASS_BCRYPT="%%ADMIN_PASS_BCRYPT%%"
ADMIN_PASS_PLAIN="%%ADMIN_PASS_PLAIN%%"
COOKIE_DOMAIN="%%COOKIE_DOMAIN%%"
COOKIE_SECURE="%%COOKIE_SECURE%%"
# Debug presence (lengths only, без вывода значений)
echo "[debug] admin env presence: JWT=${#JWT_SECRET} USER=${#ADMIN_USER} BCRYPT=${#ADMIN_PASS_BCRYPT} PLAIN=${#ADMIN_PASS_PLAIN} C_DOM=${#COOKIE_DOMAIN} C_SEC=${#COOKIE_SECURE}"
if [ -n "$ADMIN_USER$ADMIN_PASS_BCRYPT$ADMIN_PASS_PLAIN$JWT_SECRET$COOKIE_DOMAIN$COOKIE_SECURE" ]; then
  sudo mkdir -p "$ADMIN_DROPIN_DIR"
  TMPD=$(mktemp)
  {
    echo "[Service]"
    # Quote values to preserve special characters (e.g., $ in bcrypt hashes)
    [ -n "$COOKIE_DOMAIN" ] && echo "Environment=\"COOKIE_DOMAIN=$COOKIE_DOMAIN\""
    [ -n "$COOKIE_SECURE" ] && echo "Environment=\"COOKIE_SECURE=$COOKIE_SECURE\""
    [ -n "$JWT_SECRET" ] && echo "Environment=\"JWT_SECRET=$JWT_SECRET\""
    [ -n "$ADMIN_USER" ] && echo "Environment=\"ADMIN_USERNAME=$ADMIN_USER\""
    # Write whichever password representations are provided
    [ -n "$ADMIN_PASS_PLAIN" ] && echo "Environment=\"ADMIN_PASSWORD_PLAIN=$ADMIN_PASS_PLAIN\""
    [ -n "$ADMIN_PASS_BCRYPT" ] && echo "Environment=\"ADMIN_PASSWORD_BCRYPT=$ADMIN_PASS_BCRYPT\""
  } > "$TMPD"
  sudo install -m 0644 "$TMPD" "$ADMIN_DROPIN_DIR/override.conf"
  rm -f "$TMPD" || true
  echo "[debug] wrote override.conf (JWT:$([ -n \"$JWT_SECRET\" ] && echo 1 || echo 0) PLAIN:$([ -n \"$ADMIN_PASS_PLAIN\" ] && echo 1 || echo 0) BCRYPT:$([ -n \"$ADMIN_PASS_BCRYPT\" ] && echo 1 || echo 0))"
fi

# Nginx site config
sudo install -m 0644 "$DEPLOY_DIR/deploy/launcher.conf" "$NGINX_SITE_AVAILABLE"
sudo ln -sf "$NGINX_SITE_AVAILABLE" "$NGINX_SITE_ENABLED"
sudo nginx -t
sudo systemctl reload nginx
echo "[check] Nginx site file sha and redirect rule"
sudo sha256sum "$NGINX_SITE_AVAILABLE" || true
if sudo grep -n "error_page 401 =302 /admin/ui/login.html" "$NGINX_SITE_AVAILABLE" >/dev/null 2>&1; then
  echo "[check] redirect rule present in nginx config"
else
  echo "[check] redirect rule NOT present in nginx config"
fi

# Smoke tests
FAIL=0
http_code() { curl -ks --max-time 5 -o /dev/null -w "%{http_code}" "$1"; }
must_200() { url="$1"; name="$2"; code=$(http_code "$url"); if [ "$code" = "200" ]; then echo "[test] PASS $name ($url)"; else echo "[test] FAIL $name ($url) -> $code"; FAIL=1; fi; }

must_200 "$SITE_BASE/admin/ui/login.html" "Admin UI login"
code=$(http_code "$SITE_BASE/admin/")
if [ "$code" = "200" ]; then echo "[test] WARN /admin/ returned 200 (maybe authorized)"; elif [ "$code" = "401" ]; then echo "[test] PASS /admin/ protected (401 Unauthorized)"; elif [ "$code" = "302" ]; then echo "[test] PASS /admin/ protected (302 Found)"; else echo "[test] FAIL /admin/ -> $code"; FAIL=1; fi
must_200 "$SITE_BASE/admin/ui/admin.js" "Admin UI static admin.js"
must_200 "$SITE_BASE/admin/api/health" "Admin API health"
must_200 "$SITE_BASE/" "Landing root"
must_200 "$SITE_BASE/styles.css" "Landing styles"

if curl -ksf --max-time 5 "$SITE_BASE/manifests/launcher/latest.json" >/dev/null; then
  echo "[test] PASS manifests/launcher/latest.json"
else
  echo "[test] WARN manifests/launcher/latest.json not present"
fi
if curl -ksf --max-time 5 "$SITE_BASE/assets/ping.txt" >/dev/null; then
  echo "[test] PASS assets/ping.txt"
else
  echo "[test] WARN assets/ping.txt not present"
fi

if [ "\$FAIL" -ne 0 ]; then
  echo "[deploy] One or more tests FAILED. Collecting diagnostics..."
  echo "---- NGINX TEST ----"; sudo nginx -t || true
  echo "---- NGINX ERROR LOG (last 300) ----"; sudo tail -n 300 /var/log/nginx/error.log || true
  echo "---- NGINX ACCESS LOG (last 300) ----"; sudo tail -n 300 /var/log/nginx/access.log || true
  echo "---- SYSTEMD STATUS (api) ----"; sudo systemctl status chillhub-api.service --no-pager -n 50 || true
  echo "---- SYSTEMD STATUS (admin) ----"; sudo systemctl status chillhub-admin.service --no-pager -n 50 || true
  echo "---- JOURNALCTL (api last 300) ----"; sudo journalctl -u chillhub-api.service -e -n 300 || true
  echo "---- JOURNALCTL (admin last 300) ----"; sudo journalctl -u chillhub-admin.service -e -n 300 || true
  echo "---- ADMIN DROP-IN (masked) ----"; if [ -f "/etc/systemd/system/chillhub-admin.service.d/override.conf" ]; then sudo sed -E 's/(Environment=\"?JWT_SECRET=)[^\"]+/\1<redacted>/' /etc/systemd/system/chillhub-admin.service.d/override.conf | sed -E 's/(Environment=\"?ADMIN_PASSWORD_(PLAIN|BCRYPT)=)[^\"]+/\1<redacted>/' || true; else echo missing; fi
  exit 1
fi

# Final summary
echo "---- REMOTE SUMMARY ----"
echo "server_name launcher.samoy.love -> root:"
sudo nginx -T 2>/dev/null | awk '
  /server_name[[:space:]]+launcher\.samoy\.love\s*;/ {inserver=1}
  inserver && $1=="root" { sub(/;$/, "", $2); print $2; exit }
' || true
echo "sha256 of key files:"
if [ -f "\$SITE_DIR/index.html" ]; then sha256sum "\$SITE_DIR/index.html" || true; else echo "missing \$SITE_DIR/index.html"; fi
if [ -f "\$SITE_DIR/styles.css" ]; then sha256sum "\$SITE_DIR/styles.css" || true; else echo "missing \$SITE_DIR/styles.css"; fi
echo "downloads listing (first 50):"
if [ -d "\$SITE_DIR/downloads" ]; then ls -lah "\$SITE_DIR/downloads" | sed -n '1,50p'; else echo "downloads dir missing"; fi

# Per-section mismatch counters
SITE_MISM=0
ADMIN_MISM=0
DL_MISM=0
BIN_MISM=0
SYS_MISM=0

echo "manifest compare (site)"
SITE_MAN_B64="%%SITE_MANIFEST%%"
if [ -n "$SITE_MAN_B64" ]; then
  printf "%s" "$SITE_MAN_B64" | base64 -d > /tmp/site.manifest || true
  mism=0; while IFS=$'\t' read -r rel sha; do f="$SITE_DIR/$rel"; if [ -f "$f" ]; then rsha=$(sha256sum "$f" | awk '{print $1}'); if [ "$rsha" = "$sha" ]; then echo "OK  site $rel"; else echo "FAIL site $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi; else echo "MISS site $rel"; mism=$((mism+1)); fi; done < /tmp/site.manifest; if [ "$mism" -ne 0 ]; then echo "site manifest mismatches: $mism"; fi; SITE_MISM=$mism; MISM_TOTAL=$((MISM_TOTAL+ mism))
fi

echo "manifest compare (admin_ui)"
ADMIN_MAN_B64="%%ADMIN_MANIFEST%%"
if [ -n "$ADMIN_MAN_B64" ]; then
  printf "%s" "$ADMIN_MAN_B64" | base64 -d > /tmp/admin.manifest || true
  mism=0; while IFS=$'\t' read -r rel sha; do f="$LAUNCHER_DIR/admin_ui/$rel"; if [ -f "$f" ]; then rsha=$(sha256sum "$f" | awk '{print $1}'); if [ "$rsha" = "$sha" ]; then echo "OK  admin $rel"; else echo "FAIL admin $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi; else echo "MISS admin $rel"; mism=$((mism+1)); fi; done < /tmp/admin.manifest; if [ "$mism" -ne 0 ]; then echo "admin manifest mismatches: $mism"; fi; ADMIN_MISM=$mism; MISM_TOTAL=$((MISM_TOTAL+ mism))
fi

echo "manifest compare (downloads)"
DL_MAN_B64="%%DOWNLOADS_MANIFEST%%"
if [ -n "$DL_MAN_B64" ]; then
  printf "%s" "$DL_MAN_B64" | base64 -d > /tmp/downloads.manifest || true
  mism=0; while IFS=$'\t' read -r rel sha; do f="$SITE_DIR/downloads/$rel"; if [ -f "$f" ]; then rsha=$(sha256sum "$f" | awk '{print $1}'); if [ "$rsha" = "$sha" ]; then echo "OK  downloads $rel"; else echo "FAIL downloads $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi; else echo "MISS downloads $rel"; mism=$((mism+1)); fi; done < /tmp/downloads.manifest; if [ "$mism" -ne 0 ]; then echo "downloads manifest mismatches: $mism"; fi; DL_MISM=$mism; MISM_TOTAL=$((MISM_TOTAL+ mism))
fi

echo "manifest compare (bin)"
BIN_MAN_B64="%%BIN_MANIFEST%%"
if [ -n "$BIN_MAN_B64" ]; then
  printf "%s" "$BIN_MAN_B64" | base64 -d > /tmp/bin.manifest || true
  mism=0; while IFS=$'\t' read -r rel sha; do case "$rel" in api|admin) f="/opt/chillhub/$rel";; *) f="$DEPLOY_DIR/bin/$rel";; esac; if [ -f "$f" ]; then rsha=$(sha256sum "$f" | awk '{print $1}'); if [ "$rsha" = "$sha" ]; then echo "OK  bin $rel"; else echo "FAIL bin $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi; else echo "MISS bin $rel"; mism=$((mism+1)); fi; done < /tmp/bin.manifest; if [ "$mism" -ne 0 ]; then echo "bin manifest mismatches: $mism"; fi; BIN_MISM=$mism; MISM_TOTAL=$((MISM_TOTAL+ mism))
fi

echo "manifest compare (systemd)"
SYS_MAN_B64="%%SYSTEMD_MANIFEST%%"
if [ -n "$SYS_MAN_B64" ]; then
  printf "%s" "$SYS_MAN_B64" | base64 -d > /tmp/systemd.manifest || true
  mism=0; while IFS=$'\t' read -r rel sha; do f="/etc/systemd/system/$rel"; if [ -f "$f" ]; then rsha=$(sha256sum "$f" | awk '{print $1}'); if [ "$rsha" = "$sha" ]; then echo "OK  systemd $rel"; else echo "FAIL systemd $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi; else echo "MISS systemd $rel"; mism=$((mism+1)); fi; done < /tmp/systemd.manifest; if [ "$mism" -ne 0 ]; then echo "systemd manifest mismatches: $mism"; fi; SYS_MISM=$mism; MISM_TOTAL=$((MISM_TOTAL+ mism))
fi

# Explicit SHA listings for all copied files (from manifests)
echo "==== MANIFEST CHECKS (END) ===="
echo "sha listing (bin installed)"
for f in /opt/chillhub/api /opt/chillhub/admin; do if [ -f "$f" ]; then sha256sum "$f"; else echo "MISS $f"; fi; done

echo "sha listing (admin_ui key file)"
if [ -f "$LAUNCHER_DIR/admin_ui/admin.js" ]; then sha256sum "$LAUNCHER_DIR/admin_ui/admin.js"; else echo "MISS $LAUNCHER_DIR/admin_ui/admin.js"; fi

if [ -s /tmp/site.manifest ]; then
  echo "sha listing (site)"; while IFS=$'\t' read -r rel sha; do f="$SITE_DIR/$rel"; if [ -f "$f" ]; then sha256sum "$f"; else echo "MISS site $rel"; fi; done < /tmp/site.manifest
fi
if [ -s /tmp/admin.manifest ]; then
  echo "sha listing (admin_ui)"; while IFS=$'\t' read -r rel sha; do f="$LAUNCHER_DIR/admin_ui/$rel"; if [ -f "$f" ]; then sha256sum "$f"; else echo "MISS admin $rel"; fi; done < /tmp/admin.manifest
fi
if [ -s /tmp/downloads.manifest ]; then
  echo "sha listing (downloads)"; while IFS=$'\t' read -r rel sha; do f="$SITE_DIR/downloads/$rel"; if [ -f "$f" ]; then sha256sum "$f"; else echo "MISS downloads $rel"; fi; done < /tmp/downloads.manifest
fi
if [ -s /tmp/bin.manifest ]; then
  echo "sha listing (bin)"; while IFS=$'\t' read -r rel sha; do case "$rel" in api|admin) f="/opt/chillhub/$rel";; *) f="$DEPLOY_DIR/bin/$rel";; esac; if [ -f "$f" ]; then sha256sum "$f"; else echo "MISS bin $rel"; fi; done < /tmp/bin.manifest
fi

# Per-section mismatch summary
echo "[summary] mismatches: site=$SITE_MISM admin=$ADMIN_MISM downloads=$DL_MISM bin=$BIN_MISM systemd=$SYS_MISM total=$MISM_TOTAL"

# Cleanup temp manifests (do at the very end so listings work)
rm -f /tmp/site.manifest /tmp/admin.manifest /tmp/bin.manifest /tmp/systemd.manifest /tmp/downloads.manifest || true

# Enforce failure on mismatches if requested (after printing)
if [ -n "$FAIL_ON_MISMATCH" ] && [ "$MISM_TOTAL" -ne 0 ]; then
  echo "[deploy] Manifest mismatches detected (total blocks: $MISM_TOTAL)"
  exit 1
fi
if [ -s /tmp/systemd.manifest ]; then
  echo "sha listing (systemd)"; while IFS=$'\t' read -r rel sha; do f="/etc/systemd/system/$rel"; if [ -f "$f" ]; then sha256sum "$f"; else echo "MISS systemd $rel"; fi; done < /tmp/systemd.manifest
fi

# FINAL manifest compare (re-check at end so results appear last)
echo "==== FINAL manifest compare (site) ===="
if [ -s /tmp/site.manifest ]; then
  mism=0; while IFS=$'\t' read -r rel sha; do f="$SITE_DIR/$rel"; if [ -f "$f" ]; then rsha=$(sha256sum "$f" | awk '{print $1}'); if [ "$rsha" = "$sha" ]; then echo "OK  site $rel"; else echo "FAIL site $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi; else echo "MISS site $rel"; mism=$((mism+1)); fi; done < /tmp/site.manifest; if [ "$mism" -ne 0 ]; then echo "site manifest mismatches: $mism"; fi; fi

echo "==== FINAL manifest compare (admin_ui) ===="
if [ -s /tmp/admin.manifest ]; then
  mism=0; while IFS=$'\t' read -r rel sha; do f="$LAUNCHER_DIR/admin_ui/$rel"; if [ -f "$f" ]; then rsha=$(sha256sum "$f" | awk '{print $1}'); if [ "$rsha" = "$sha" ]; then echo "OK  admin $rel"; else echo "FAIL admin $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi; else echo "MISS admin $rel"; mism=$((mism+1)); fi; done < /tmp/admin.manifest; if [ "$mism" -ne 0 ]; then echo "admin manifest mismatches: $mism"; fi; fi

echo "==== FINAL manifest compare (downloads) ===="
if [ -s /tmp/downloads.manifest ]; then
  mism=0; while IFS=$'\t' read -r rel sha; do f="$SITE_DIR/downloads/$rel"; if [ -f "$f" ]; then rsha=$(sha256sum "$f" | awk '{print $1}'); if [ "$rsha" = "$sha" ]; then echo "OK  downloads $rel"; else echo "FAIL downloads $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi; else echo "MISS downloads $rel"; mism=$((mism+1)); fi; done < /tmp/downloads.manifest; if [ "$mism" -ne 0 ]; then echo "downloads manifest mismatches: $mism"; fi; fi

echo "==== FINAL manifest compare (bin) ===="
if [ -s /tmp/bin.manifest ]; then
  mism=0; while IFS=$'\t' read -r rel sha; do case "$rel" in api|admin) f="/opt/chillhub/$rel";; *) f="$DEPLOY_DIR/bin/$rel";; esac; if [ -f "$f" ]; then rsha=$(sha256sum "$f" | awk '{print $1}'); if [ "$rsha" = "$sha" ]; then echo "OK  bin $rel"; else echo "FAIL bin $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi; else echo "MISS bin $rel"; mism=$((mism+1)); fi; done < /tmp/bin.manifest; if [ "$mism" -ne 0 ]; then echo "bin manifest mismatches: $mism"; fi; fi

echo "==== FINAL manifest compare (systemd) ===="
if [ -s /tmp/systemd.manifest ]; then
  mism=0; while IFS=$'\t' read -r rel sha; do f="/etc/systemd/system/$rel"; if [ -f "$f" ]; then rsha=$(sha256sum "$f" | awk '{print $1}'); if [ "$rsha" = "$sha" ]; then echo "OK  systemd $rel"; else echo "FAIL systemd $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi; else echo "MISS systemd $rel"; mism=$((mism+1)); fi; done < /tmp/systemd.manifest; if [ "$mism" -ne 0 ]; then echo "systemd manifest mismatches: $mism"; fi; fi

# Final summary of manifest mismatches
echo "[summary] Manifest mismatch blocks total: $MISM_TOTAL"

# Cleanup temp manifests (do this at the very end so listings above can use them)
rm -f /tmp/site.manifest /tmp/admin.manifest /tmp/bin.manifest /tmp/systemd.manifest /tmp/downloads.manifest || true

# Enforce failure on mismatches if requested (after printing final summary)
if [ -n "$FAIL_ON_MISMATCH" ] && [ "$MISM_TOTAL" -ne 0 ]; then
  echo "[deploy] Manifest mismatches detected (total blocks with mismatches: $MISM_TOTAL)"
  exit 1
fi
'@

Write-Section "Remote deploy"
Write-Info "Running remote deploy script via SSH"
# Pipe the here-string content to remote bash
# Inject sanitized parameter values into the remote script
function Convert-ToBashDqEscaped([string]$s){ if ($null -eq $s) { return "" } return ($s -replace '"','\\"') }
$injected = $remoteScript
  # If only plain admin password is provided, derive bcrypt locally (so server doesn't need Go)
  if ([string]::IsNullOrWhiteSpace($AdminPasswordBcrypt) -and -not [string]::IsNullOrWhiteSpace($AdminPasswordPlain)) {
    # Attempt local bcrypt derivation only if Go is present AND a module context exists; otherwise skip to avoid noisy warnings
    $canGo = $false
    try { $canGo = $null -ne (Get-Command go -ErrorAction SilentlyContinue) } catch {}
    $gomod = ""; if ($canGo) { try { $gomod = (& go env GOMOD 2>$null); } catch {} }
    if ($canGo -and -not [string]::IsNullOrWhiteSpace($gomod)) {
      try {
        Write-Info "Deriving bcrypt for admin password locally"
        $tmpGo = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "bcrypt-" + [Guid]::NewGuid().ToString("N") + ".go")
        $goSrc = @'
package main
import (
  "fmt"
  "golang.org/x/crypto/bcrypt"
  "os"
)
func main(){
  p := os.Getenv("PW")
  if p == "" { fmt.Print(""); return }
  h, err := bcrypt.GenerateFromPassword([]byte(p), 12)
  if err != nil { fmt.Print(""); return }
  fmt.Print(string(h))
}
'@
        [System.IO.File]::WriteAllText($tmpGo, $goSrc)
        $env:PW = $AdminPasswordPlain
        $hash = & go run $tmpGo 2>$null
        Remove-Item -Force $tmpGo -ErrorAction SilentlyContinue
        $env:PW = $null
        if (-not [string]::IsNullOrWhiteSpace($hash)) {
          $AdminPasswordBcrypt = $hash
          Write-Ok "bcrypt hash derived locally"
        } else {
          Write-Info "Local bcrypt derivation produced empty result; will rely on server"
        }
      } catch {
        Write-Info "Local bcrypt derivation unavailable; will rely on server"
      }
    } else {
      Write-Info "Skipping local bcrypt derivation (Go/module context not available); will rely on server"
    }
  }
$makeManifestB64 = {
  param([string]$Root)
  if (-not $Root -or -not (Test-Path -LiteralPath $Root)) { return "" }
  $lines = New-Object System.Collections.Generic.List[string]
  Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
    $full = $_.FullName
    # Remove any leading directory separators in a cross-platform safe way
    $rel = $full.Substring($Root.Length).TrimStart([char]'\',[char]'/')
    $rel = $rel -replace '\\','/'
    $h = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash.ToLower()
    $lines.Add("$rel`t$h")
  }
  $txt = [string]::Join("`n", $lines)
  if ([string]::IsNullOrWhiteSpace($txt)) { return "" }
  return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($txt))
}

# Build local manifests
$siteManifestB64    = & $makeManifestB64 $landingDir
$adminManifestB64   = & $makeManifestB64 $adminUIDirS
$binManifestB64     = & $makeManifestB64 $BinDir
$systemdManifestB64 = if (Test-Path $SystemdDir) { & $makeManifestB64 $SystemdDir } else { "" }
$downloadsManifestB64 = if ($DownloadsDir -and (Test-Path -LiteralPath $DownloadsDir)) { & $makeManifestB64 $DownloadsDir } else { "" }

# Diagnostics: show local manifest Base64 lengths so we know they are non-empty
$siteLen      = if ([string]::IsNullOrEmpty($siteManifestB64)) { 0 } else { $siteManifestB64.Length }
$adminLen     = if ([string]::IsNullOrEmpty($adminManifestB64)) { 0 } else { $adminManifestB64.Length }
$binLen       = if ([string]::IsNullOrEmpty($binManifestB64)) { 0 } else { $binManifestB64.Length }
$systemdLen   = if ([string]::IsNullOrEmpty($systemdManifestB64)) { 0 } else { $systemdManifestB64.Length }
$downloadsLen = if ([string]::IsNullOrEmpty($downloadsManifestB64)) { 0 } else { $downloadsManifestB64.Length }
Write-Info ("[debug] site manifest b64 length:      {0}" -f $siteLen)
Write-Info ("[debug] admin manifest b64 length:     {0}" -f $adminLen)
Write-Info ("[debug] bin manifest b64 length:       {0}" -f $binLen)
Write-Info ("[debug] systemd manifest b64 length:   {0}" -f $systemdLen)
Write-Info ("[debug] downloads manifest b64 length: {0}" -f $downloadsLen)
$injected = $injected.Replace('%%DOWNLOADS_DIR%%', (Convert-ToBashDqEscaped $serverDownloads))
$injected = $injected.Replace('%%JWT_SECRET%%', (Convert-ToBashDqEscaped $JwtSecret))
$injected = $injected.Replace('%%ADMIN_USER%%', (Convert-ToBashDqEscaped $AdminUser))
$injected = $injected.Replace('%%ADMIN_PASS_BCRYPT%%', (Convert-ToBashDqEscaped $AdminPasswordBcrypt))
$injected = $injected.Replace('%%ADMIN_PASS_PLAIN%%', (Convert-ToBashDqEscaped $AdminPasswordPlain))
$injected = $injected.Replace('%%COOKIE_DOMAIN%%', (Convert-ToBashDqEscaped $CookieDomain))
$injected = $injected.Replace('%%COOKIE_SECURE%%', (Convert-ToBashDqEscaped $CookieSecure))
$injected = $injected.Replace('%%SITE_BASE_URL%%', (Convert-ToBashDqEscaped $SiteBaseUrl))
$failFlag = if ($FailOnManifestMismatch.IsPresent) { "1" } else { "" }
$injected = $injected.Replace('%%FAIL_ON_MISMATCH%%', (Convert-ToBashDqEscaped $failFlag))
$injected = $injected.Replace('%%SITE_MANIFEST%%', (Convert-ToBashDqEscaped $siteManifestB64))
$injected = $injected.Replace('%%ADMIN_MANIFEST%%', (Convert-ToBashDqEscaped $adminManifestB64))
$injected = $injected.Replace('%%BIN_MANIFEST%%', (Convert-ToBashDqEscaped $binManifestB64))
$injected = $injected.Replace('%%SYSTEMD_MANIFEST%%', (Convert-ToBashDqEscaped $systemdManifestB64))
$injected = $injected.Replace('%%DOWNLOADS_MANIFEST%%', (Convert-ToBashDqEscaped $downloadsManifestB64))
# Normalize newlines to LF to avoid CR issues on remote bash
$normalized = ($injected -replace "`r`n","`n") -replace "`r",""
# Write to a local temp file as UTF-8 (no BOM)
$tmpLocal = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'chillhub-deploy.ps1.tmp')
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($tmpLocal, $normalized, $utf8NoBom)
# Upload to remote fixed path
& $SCP @sshCommon $tmpLocal "${Remote}:/tmp/chillhub-deploy.sh"
# cleanup local temp file
Remove-Item -Force $tmpLocal -ErrorAction SilentlyContinue

# Build a small remote wrapper to avoid complex quoting via SSH
$wrapperLocal = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'run-remote.sh')
$wrapperContent = @'
#!/bin/bash
set -euo pipefail
rc=0
run_cmd="/bin/bash /tmp/chillhub-deploy.sh"
# Pre-run: quick grep to ensure vars are injected in the script
echo "[precheck] injected vars in /tmp/chillhub-deploy.sh (grep)" || true
grep -nE '^(JWT_SECRET|ADMIN_USER|ADMIN_PASS_BCRYPT|ADMIN_PASS_PLAIN|COOKIE_DOMAIN|COOKIE_SECURE)=' /tmp/chillhub-deploy.sh || true
# Execute and capture reliably to a file, then print it unconditionally
{ eval "$run_cmd"; } > /tmp/chillhub-deploy.log 2>&1 || rc=$?
echo "--- REMOTE LOG DUMP BEGIN ---"
cat /tmp/chillhub-deploy.log 2>/dev/null || true
echo "--- REMOTE LOG DUMP END ---"
# keep /tmp/chillhub-deploy.log for later retrieval; clean the deploy script and wrapper
rm -f /tmp/chillhub-deploy.sh "$0" 2>/dev/null || true
exit $rc
'@
$wrapperNormalized = ($wrapperContent -replace "`r`n","`n") -replace "`r",""
[System.IO.File]::WriteAllText($wrapperLocal, $wrapperNormalized, $utf8NoBom)
& $SCP @sshCommon $wrapperLocal "${Remote}:/tmp/run-remote.sh"
Remove-Item -Force $wrapperLocal -ErrorAction SilentlyContinue
& $SSH @sshCommon $Remote '/bin/bash' '/tmp/run-remote.sh'
if ($LASTEXITCODE -ne 0) {
  throw "Remote deploy failed (rc=$LASTEXITCODE)"
}

# Try to fetch remote log (if present) so that all remote output including manifest comparison is visible locally
try {
  $remoteLogLocal = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'chillhub-deploy.remote.log')
  & $SCP @sshCommon "${Remote}:/tmp/chillhub-deploy.log" $remoteLogLocal | Out-Null
  if (Test-Path -LiteralPath $remoteLogLocal) {
    Write-Section "Remote script log"
    Get-Content -Raw -LiteralPath $remoteLogLocal | Write-Host
    Remove-Item -Force $remoteLogLocal -ErrorAction SilentlyContinue
  }
} catch {
  Write-Warn ("Could not fetch remote log: {0}" -f $_.Exception.Message)
}

# Post-deploy external smoke tests (from this machine)
Write-Section "Post-deploy smoke (external)"
function Get-HttpCode {
  param([Parameter(Mandatory=$true)][string]$Url)
  try {
    $resp = Invoke-WebRequest -Uri $Url -Method Head -UseBasicParsing -TimeoutSec 7
    return [int]$resp.StatusCode
  } catch {
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
      return [int]$_.Exception.Response.StatusCode
    }
    return 0
  }
}
function Get-HttpCodeGet {
  param([Parameter(Mandatory=$true)][string]$Url)
  try {
    $resp = Invoke-WebRequest -Uri $Url -Method Get -UseBasicParsing -TimeoutSec 7
    return [int]$resp.StatusCode
  } catch {
    if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
      return [int]$_.Exception.Response.StatusCode
    }
    return 0
  }
}

$base = $SiteBaseUrl.TrimEnd('/')
$checks = @(
  @{ url = "$base/";               name = "Landing root";           expect = 200 },
  @{ url = "$base/styles.css";      name = "Landing styles";         expect = 200 },
  @{ url = "$base/assets/ping.txt"; name = "Assets ping";            expect = 200 },
  @{ url = "$base/admin/ui/login.html"; name = "Admin UI login";     expect = 200 },
  # Admin API health is protected by nginx (auth_request); expect 401 externally
  @{ url = "$base/admin/api/health";    name = "Admin API health (ext)";   expect = 401 },
  # Public server API check: use GET for JSON endpoints (some servers return 405 to HEAD)
  @{ url = "$base/api/games";           name = "Server API games (GET)";   expect = 200 },
  @{ url = "$base/admin/";              name = "Admin gate (401)";   expect = 401 }
)

$fail = 0
foreach ($c in $checks) {
  # Use GET for JSON endpoints where HEAD might return 405
  if ($c.name -like "*GET*") {
    $code = Get-HttpCodeGet $c.url
  } else {
    $code = Get-HttpCode $c.url
  }

  if ($code -eq $c.expect) {
    Write-Ok ("{0} -> {1}" -f $c.name, $code)
  } else {
    Write-Warn ("{0} -> {1} (expected {2})" -f $c.name, $code, $c.expect)
    $fail = 1
  }
}

if ($fail -eq 0) {
  Write-Ok ("Deploy verified at {0}" -f $base)
} else {
  Write-Warn ("Deploy finished with warnings at {0}. See checks above." -f $base)
}

# Final remote summary (explicit)
Write-Section "Final summary (remote)"
try {
  $summaryLocal = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), 'summary-remote.sh')
  $summaryContent = @'
#!/bin/bash
set -euo pipefail
printf "%s %s\n" "[sum] Server arch:" "$(uname -m)"
printf "%s\n" "[sum] Binaries:"
if command -v file >/dev/null 2>&1; then
  file /opt/chillhub/api /opt/chillhub/admin 2>/dev/null || true
else
  ls -l /opt/chillhub/api /opt/chillhub/admin 2>/dev/null || true
fi
printf "%s %s\n" "[sum] Admin service:" "$(systemctl is-active chillhub-admin.service 2>/dev/null || true)"
printf "%s %s\n" "[sum] Admin health code (external):" "$(curl -ks -o /dev/null -w '%{http_code}' https://launcher.samoy.love/admin/api/health || true)"
printf "%s %s\n" "[sum] Admin gate code:" "$(curl -ks -o /dev/null -w '%{http_code}' https://launcher.samoy.love/admin/ || true)"
printf "%s %s\n" "[sum] Site root:" "$(curl -ks -o /dev/null -w '%{http_code}' https://launcher.samoy.love/ || true)"
printf "%s " "[sum] Downloads dir (server):"; sudo bash -lc 'test -d /var/www/site/downloads && (ls -1 /var/www/site/downloads | wc -l || true) || echo "missing"' || true
printf "%s %s\n" "[sum] Admin health (localhost):" "$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:55777/admin/api/health || true)"
printf "%s %s\n" "[sum] Public API games (localhost GET):" "$(curl -sS -o /dev/null -w '%{http_code}' http://127.0.0.1:55700/api/games || true)"
printf "%s %s\n" "[sum] Nginx config test:" "$(sudo nginx -t 2>&1 | tail -n1 | sed 's/.*: //')"
printf "%s\n" "[sum] Ports (55700/55777):"; (ss -ltnp 2>/dev/null | grep -E '(:55700|:55777)' || true)
printf "%s\n" "[sum] Site root (localhost via nginx):"; (curl -sS -o /dev/null -w '%{http_code}\n' http://127.0.0.1/ || true)
printf "%s\n" "[sum] Admin drop-in env (override.conf, masked):"; (sudo bash -lc 'if test -f /etc/systemd/system/chillhub-admin.service.d/override.conf; then sed -E "s/(Environment=\\\"?JWT_SECRET=)[^\\\"]+/\\1<redacted>/; s/(Environment=\\\"?ADMIN_PASSWORD_(PLAIN|BCRYPT)=)[^\\\"]+/\\1<redacted>/" /etc/systemd/system/chillhub-admin.service.d/override.conf; else echo missing; fi' || true)
# Presence checks (no secret values printed)
if sudo grep -q 'Environment=.*JWT_SECRET=' /etc/systemd/system/chillhub-admin.service.d/override.conf 2>/dev/null; then echo "[sum] override.conf: JWT_SECRET present"; else echo "[sum] override.conf: JWT_SECRET MISSING"; fi
if sudo grep -q 'Environment=.*ADMIN_PASSWORD_BCRYPT=' /etc/systemd/system/chillhub-admin.service.d/override.conf 2>/dev/null; then echo "[sum] override.conf: ADMIN_PASSWORD_BCRYPT present"; else echo "[sum] override.conf: ADMIN_PASSWORD_BCRYPT missing"; fi
if sudo grep -q 'Environment=.*ADMIN_PASSWORD_PLAIN=' /etc/systemd/system/chillhub-admin.service.d/override.conf 2>/dev/null; then echo "[sum] override.conf: ADMIN_PASSWORD_PLAIN present"; else echo "[sum] override.conf: ADMIN_PASSWORD_PLAIN missing"; fi
printf "%s %s\n" "[sum] Cert expiry:" "$(sudo bash -lc 'test -f /etc/letsencrypt/live/launcher.samoy.love/fullchain.pem && openssl x509 -enddate -noout -in /etc/letsencrypt/live/launcher.samoy.love/fullchain.pem | cut -d= -f2 || echo unknown' 2>/dev/null)"
printf "%s %s\n" "[sum] Disk free (/var/www):" "$(df -h /var/www 2>/dev/null | awk 'NR==2{print $4" free of "$2}')"
printf "%s %s\n" "[sum] Uptime:" "$(uptime -p 2>/dev/null || true)"
printf "%s\n" "[sum] Systemd logs (last 10 lines):"; (sudo journalctl -u chillhub-admin.service -n10 2>/dev/null || true)
printf "%s\n" "[sum] End of summary"
echo "---- MANIFEST REPORT ----"
SITE_MAN_B64="%%SITE_MANIFEST%%"
ADMIN_MAN_B64="%%ADMIN_MANIFEST%%"
BIN_MAN_B64="%%BIN_MANIFEST%%"
SYS_MAN_B64="%%SYSTEMD_MANIFEST%%"
DL_MAN_B64="%%DOWNLOADS_MANIFEST%%"
report_cmp(){
  local name="$1" base64="$2" root="$3" special="$4"
  if [ -z "$base64" ]; then echo "[manifest] $name: no manifest provided"; return; fi
  local mf
  mf=$(mktemp)
  printf "%s" "$base64" | base64 -d > "$mf" || true
  local total=0 ok=0 mism=0 miss=0
  while IFS=$'\t' read -r rel sha; do
    [ -z "$rel" ] && continue
    total=$((total+1))
    local f
    case "$special" in
      bin)
        case "$rel" in api|admin) f="/opt/chillhub/$rel";; *) f="$root/$rel";; esac ;;
      sys)
        f="/etc/systemd/system/$rel" ;;
      *)
        f="$root/$rel" ;;
    esac
    if [ -f "$f" ]; then
      rsha=$(sha256sum "$f" | awk '{print $1}')
      echo "PAIR $name $rel exp=$sha dst=$rsha"
      if [ "$rsha" = "$sha" ]; then echo "OK  $name $rel"; ok=$((ok+1)); else echo "FAIL $name $rel expected=$sha got=$rsha"; mism=$((mism+1)); fi
    else
      echo "MISS $name $rel"; miss=$((miss+1))
    fi
  done < "$mf"
  echo "[manifest] $name: total=$total ok=$ok mism=$mism miss=$miss"
  rm -f "$mf" || true
}
report_cmp site    "$SITE_MAN_B64"   "/var/www/site"                ""
report_cmp admin   "$ADMIN_MAN_B64"  "/var/www/launcher/admin_ui"   ""
report_cmp downloads "$DL_MAN_B64"   "/var/www/site/downloads"      ""
report_cmp bin     "$BIN_MAN_B64"    "$HOME/deploy/bin"             "bin"
report_cmp systemd "$SYS_MAN_B64"    "/etc/systemd/system"          "sys"
# Enforce failure on manifest report too
FAIL_FLAG="%%FAIL_ON_MISMATCH%%"
if [ -n "$FAIL_FLAG" ]; then
  if grep -qE '^(FAIL|MISS) ' /tmp/chillhub-deploy.log 2>/dev/null; then
    echo "---- DIAGNOSTICS (summary) ----"
    echo "---- NGINX ERROR LOG (last 200) ----"; sudo tail -n 200 /var/log/nginx/error.log || true
    echo "---- NGINX ACCESS LOG (last 200) ----"; sudo tail -n 200 /var/log/nginx/access.log || true
    echo "[sum] Manifest report indicates mismatches; exiting with 1 due to FAIL_ON_MISMATCH"
    exit 1
  fi
fi
'@
  # Inject manifests into the summary script so it can re-run comparisons explicitly
  $summaryInjected = $summaryContent
  $summaryInjected = $summaryInjected.Replace('%%SITE_MANIFEST%%', (Convert-ToBashDqEscaped $siteManifestB64))
  $summaryInjected = $summaryInjected.Replace('%%ADMIN_MANIFEST%%', (Convert-ToBashDqEscaped $adminManifestB64))
  $summaryInjected = $summaryInjected.Replace('%%BIN_MANIFEST%%', (Convert-ToBashDqEscaped $binManifestB64))
  $summaryInjected = $summaryInjected.Replace('%%SYSTEMD_MANIFEST%%', (Convert-ToBashDqEscaped $systemdManifestB64))
  $summaryInjected = $summaryInjected.Replace('%%DOWNLOADS_MANIFEST%%', (Convert-ToBashDqEscaped $downloadsManifestB64))
  # Also inject fail flag
  $summaryInjected = $summaryInjected.Replace('%%FAIL_ON_MISMATCH%%', (Convert-ToBashDqEscaped $failFlag))
  [System.IO.File]::WriteAllText($summaryLocal, $summaryInjected, $utf8NoBom)
  & $SCP @sshCommon $summaryLocal "${Remote}:/tmp/summary-remote.sh"
  Remove-Item -Force $summaryLocal -ErrorAction SilentlyContinue
  & $SSH @sshCommon $Remote '/bin/bash' '/tmp/summary-remote.sh'
  & $SSH @sshCommon $Remote 'rm -f /tmp/summary-remote.sh' | Out-Null
} catch {
  Write-Warn ("Could not fetch final remote summary: {0}" -f $_.Exception.Message)
}

Write-Ok "Done"

Write-Ok "Done"
