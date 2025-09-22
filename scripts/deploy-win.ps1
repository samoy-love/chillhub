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
  # Build Go servers for linux/amd64
  Write-Section "Build (linux/amd64)"
  Write-Info "Building Go servers (linux/amd64)"
  $prevGOOS = $env:GOOS; $prevGOARCH = $env:GOARCH; $prevCGO = $env:CGO_ENABLED
  $env:GOOS = "linux"; $env:GOARCH = "amd64"; $env:CGO_ENABLED = "0"
  Push-Location (Join-Path $RepoRoot "server")
  try {
    go mod tidy
    go build -o (Join-Path $BinDir "api")   ./cmd/api
    go build -o (Join-Path $BinDir "admin") ./cmd/admin
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

# Install binaries
sudo install -d -m 0755 "$OPT_DIR"
sudo install -m 0755 "$DEPLOY_DIR/bin/api" "$OPT_DIR/api"
sudo install -m 0755 "$DEPLOY_DIR/bin/admin" "$OPT_DIR/admin"

# Systemd units (optional)
if [ -d "$DEPLOY_DIR/systemd" ]; then
  sudo install -m 0644 "$DEPLOY_DIR/systemd/chillhub-api.service" /etc/systemd/system/chillhub-api.service || true
  sudo install -m 0644 "$DEPLOY_DIR/systemd/chillhub-admin.service" /etc/systemd/system/chillhub-admin.service || true
  sudo systemctl daemon-reload || true
  sudo systemctl enable chillhub-api.service || true
  sudo systemctl enable chillhub-admin.service || true
fi

# Optional: write admin auth env drop-in (from parameters)
ADMIN_DROPIN_DIR="/etc/systemd/system/chillhub-admin.service.d"
JWT_SECRET="%%JWT_SECRET%%"
ADMIN_USER="%%ADMIN_USER%%"
ADMIN_PASS_BCRYPT="%%ADMIN_PASS_BCRYPT%%"
ADMIN_PASS_PLAIN="%%ADMIN_PASS_PLAIN%%"
COOKIE_DOMAIN="%%COOKIE_DOMAIN%%"
COOKIE_SECURE="%%COOKIE_SECURE%%"
if [ -n "$ADMIN_PASS_PLAIN" ] && [ -z "$ADMIN_PASS_BCRYPT" ]; then
  if command -v go >/dev/null 2>&1; then
    TMPGO=$(mktemp -t bcrypt-XXXXXX.go)
    printf '%s\n' \
      'package main' \
      'import (' \
      '  "fmt"' \
      '  "golang.org/x/crypto/bcrypt"' \
      '  "os"' \
      ')' \
      'func main(){' \
      '  p := os.Getenv("PW")' \
      '  if p == "" { fmt.Println(""); return }' \
      '  h, err := bcrypt.GenerateFromPassword([]byte(p), 12)' \
      '  if err != nil { fmt.Println(""); return }' \
      '  fmt.Print(string(h))' \
      '}' > "$TMPGO"
    # Limit go run to 20s to avoid long module download hangs
    ADMIN_PASS_BCRYPT=$(PW="$ADMIN_PASS_PLAIN" timeout 20s go run "$TMPGO" 2>/dev/null || true)
    rm -f "$TMPGO" || true
  else
    echo "[warn] go not installed on server; skipping bcrypt derivation from plain password"
  fi
fi

if [ -n "$ADMIN_USER$ADMIN_PASS_BCRYPT$JWT_SECRET$COOKIE_DOMAIN$COOKIE_SECURE" ]; then
  sudo mkdir -p "$ADMIN_DROPIN_DIR"
  TMPD=$(mktemp)
  {
    echo "[Service]"
    [ -n "$COOKIE_DOMAIN" ] && echo "Environment=COOKIE_DOMAIN=$COOKIE_DOMAIN"
    [ -n "$COOKIE_SECURE" ] && echo "Environment=COOKIE_SECURE=$COOKIE_SECURE"
    [ -n "$JWT_SECRET" ] && echo "Environment=JWT_SECRET=$JWT_SECRET"
    [ -n "$ADMIN_USER" ] && echo "Environment=ADMIN_USERNAME=$ADMIN_USER"
    [ -n "$ADMIN_PASS_BCRYPT" ] && echo "Environment=ADMIN_PASSWORD_BCRYPT=$ADMIN_PASS_BCRYPT"
  } > "$TMPD"
  sudo install -m 0644 "$TMPD" "$ADMIN_DROPIN_DIR/override.conf"
  rm -f "$TMPD" || true
fi

# Nginx site config
sudo install -m 0644 "$DEPLOY_DIR/deploy/launcher.conf" "$NGINX_SITE_AVAILABLE"
sudo ln -sf "$NGINX_SITE_AVAILABLE" "$NGINX_SITE_ENABLED"
sudo nginx -t
sudo systemctl reload nginx

# Reload services
sudo systemctl daemon-reload || true
sudo systemctl restart chillhub-api.service || true
sudo systemctl restart chillhub-admin.service || true

# Optional: sync external downloads directory
DOWNLOADS_DIR="%%DOWNLOADS_DIR%%"
if [ -n "$DOWNLOADS_DIR" ] && [ -d "$DOWNLOADS_DIR" ]; then
  sudo mkdir -p "$SITE_DIR/downloads"
  sudo rsync -a "$DOWNLOADS_DIR/" "$SITE_DIR/downloads/"
fi

# Smoke tests
FAIL=0
http_code() { curl -ks --max-time 5 -o /dev/null -w "%{http_code}" "$1"; }
must_200() { url="$1"; name="$2"; code=$(http_code "$url"); if [ "$code" = "200" ]; then echo "[test] PASS $name ($url)"; else echo "[test] FAIL $name ($url) -> $code"; FAIL=1; fi; }

must_200 "$SITE_BASE/admin/ui/login.html" "Admin UI login"
code=$(http_code "$SITE_BASE/admin/")
if [ "$code" = "200" ]; then echo "[test] WARN /admin/ returned 200 (maybe authorized)"; elif [ "$code" = "401" ]; then echo "[test] PASS /admin/ protected (401)"; else echo "[test] FAIL /admin/ -> $code"; FAIL=1; fi
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
  echo "---- NGINX ERROR LOG (last 150) ----"; sudo tail -n 150 /var/log/nginx/error.log || true
  echo "---- SYSTEMD STATUS (api) ----"; sudo systemctl status chillhub-api.service --no-pager -n 30 || true
  echo "---- SYSTEMD STATUS (admin) ----"; sudo systemctl status chillhub-admin.service --no-pager -n 30 || true
  echo "---- JOURNALCTL (api last 150) ----"; sudo journalctl -u chillhub-api.service -e -n 150 || true
  echo "---- JOURNALCTL (admin last 150) ----"; sudo journalctl -u chillhub-admin.service -e -n 150 || true
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

echo "manifest compare (site)"
SITE_MAN_B64="%%SITE_MANIFEST%%"
if [ -n "\$SITE_MAN_B64" ]; then
  printf "%s" "\$SITE_MAN_B64" | base64 -d > /tmp/site.manifest || true
  mism=0; while IFS=$'\t' read -r rel sha; do f="\$SITE_DIR/\$rel"; if [ -f "\$f" ]; then rsha=$(sha256sum "\$f" | awk '{print $1}'); if [ "\$rsha" = "\$sha" ]; then echo "OK  site \$rel"; else echo "FAIL site \$rel"; mism=$((mism+1)); fi; else echo "MISS site \$rel"; mism=$((mism+1)); fi; done < /tmp/site.manifest; if [ "\$mism" -ne 0 ]; then echo "site manifest mismatches: \$mism"; fi; MISM_TOTAL=$((MISM_TOTAL+ mism))
fi

echo "manifest compare (admin_ui)"
ADMIN_MAN_B64="%%ADMIN_MANIFEST%%"
if [ -n "\$ADMIN_MAN_B64" ]; then
  printf "%s" "\$ADMIN_MAN_B64" | base64 -d > /tmp/admin.manifest || true
  mism=0; while IFS=$'\t' read -r rel sha; do f="\$LAUNCHER_DIR/admin_ui/\$rel"; if [ -f "\$f" ]; then rsha=$(sha256sum "\$f" | awk '{print $1}'); if [ "\$rsha" = "\$sha" ]; then echo "OK  admin \$rel"; else echo "FAIL admin \$rel"; mism=$((mism+1)); fi; else echo "MISS admin \$rel"; mism=$((mism+1)); fi; done < /tmp/admin.manifest; if [ "\$mism" -ne 0 ]; then echo "admin manifest mismatches: \$mism"; fi; MISM_TOTAL=$((MISM_TOTAL+ mism))
fi

echo "manifest compare (downloads)"
DL_MAN_B64="%%DOWNLOADS_MANIFEST%%"
if [ -n "\$DL_MAN_B64" ]; then
  printf "%s" "\$DL_MAN_B64" | base64 -d > /tmp/downloads.manifest || true
  mism=0; while IFS=$'\t' read -r rel sha; do f="\$SITE_DIR/downloads/\$rel"; if [ -f "\$f" ]; then rsha=$(sha256sum "\$f" | awk '{print $1}'); if [ "\$rsha" = "\$sha" ]; then echo "OK  downloads \$rel"; else echo "FAIL downloads \$rel"; mism=$((mism+1)); fi; else echo "MISS downloads \$rel"; mism=$((mism+1)); fi; done < /tmp/downloads.manifest; if [ "\$mism" -ne 0 ]; then echo "downloads manifest mismatches: \$mism"; fi; MISM_TOTAL=$((MISM_TOTAL+ mism))
fi

echo "manifest compare (bin)"
BIN_MAN_B64="%%BIN_MANIFEST%%"
if [ -n "\$BIN_MAN_B64" ]; then
  printf "%s" "\$BIN_MAN_B64" | base64 -d > /tmp/bin.manifest || true
  mism=0; while IFS=$'\t' read -r rel sha; do case "\$rel" in api|admin) f="/opt/chillhub/\$rel";; *) f="\$DEPLOY_DIR/bin/\$rel";; esac; if [ -f "\$f" ]; then rsha=$(sha256sum "\$f" | awk '{print $1}'); if [ "\$rsha" = "\$sha" ]; then echo "OK  bin \$rel"; else echo "FAIL bin \$rel"; mism=$((mism+1)); fi; else echo "MISS bin \$rel"; mism=$((mism+1)); fi; done < /tmp/bin.manifest; if [ "\$mism" -ne 0 ]; then echo "bin manifest mismatches: \$mism"; fi; MISM_TOTAL=$((MISM_TOTAL+ mism))
fi

echo "manifest compare (systemd)"
SYS_MAN_B64="%%SYSTEMD_MANIFEST%%"
if [ -n "\$SYS_MAN_B64" ]; then
  printf "%s" "\$SYS_MAN_B64" | base64 -d > /tmp/systemd.manifest || true
  mism=0; while IFS=$'\t' read -r rel sha; do f="/etc/systemd/system/\$rel"; if [ -f "\$f" ]; then rsha=$(sha256sum "\$f" | awk '{print $1}'); if [ "\$rsha" = "\$sha" ]; then echo "OK  systemd \$rel"; else echo "FAIL systemd \$rel"; mism=$((mism+1)); fi; else echo "MISS systemd \$rel"; mism=$((mism+1)); fi; done < /tmp/systemd.manifest; if [ "\$mism" -ne 0 ]; then echo "systemd manifest mismatches: \$mism"; fi; MISM_TOTAL=$((MISM_TOTAL+ mism))
fi

# Cleanup temp manifests
rm -f /tmp/site.manifest /tmp/admin.manifest /tmp/bin.manifest /tmp/systemd.manifest /tmp/downloads.manifest || true

# Enforce failure on mismatches if requested
if [ -n "\$FAIL_ON_MISMATCH" ] && [ "\$MISM_TOTAL" -ne 0 ]; then
  echo "[deploy] Manifest mismatches detected (total blocks: \$MISM_TOTAL)"
  exit 1
fi

# Explicit SHA listings for all copied files (from manifests)
echo "sha listing (bin installed)"
for f in /opt/chillhub/api /opt/chillhub/admin; do if [ -f "\$f" ]; then sha256sum "\$f"; else echo "MISS \$f"; fi; done

echo "sha listing (admin_ui key file)"
if [ -f "\$LAUNCHER_DIR/admin_ui/admin.js" ]; then sha256sum "\$LAUNCHER_DIR/admin_ui/admin.js"; else echo "MISS \$LAUNCHER_DIR/admin_ui/admin.js"; fi

if [ -s /tmp/site.manifest ]; then
  echo "sha listing (site)"; while IFS=$'\t' read -r rel sha; do f="\$SITE_DIR/\$rel"; if [ -f "\$f" ]; then sha256sum "\$f"; else echo "MISS site \$rel"; fi; done < /tmp/site.manifest
fi
if [ -s /tmp/admin.manifest ]; then
  echo "sha listing (admin_ui)"; while IFS=$'\t' read -r rel sha; do f="\$LAUNCHER_DIR/admin_ui/\$rel"; if [ -f "\$f" ]; then sha256sum "\$f"; else echo "MISS admin \$rel"; fi; done < /tmp/admin.manifest
fi
if [ -s /tmp/downloads.manifest ]; then
  echo "sha listing (downloads)"; while IFS=$'\t' read -r rel sha; do f="\$SITE_DIR/downloads/\$rel"; if [ -f "\$f" ]; then sha256sum "\$f"; else echo "MISS downloads \$rel"; fi; done < /tmp/downloads.manifest
fi
if [ -s /tmp/bin.manifest ]; then
  echo "sha listing (bin)"; while IFS=$'\t' read -r rel sha; do case "\$rel" in api|admin) f="/opt/chillhub/\$rel";; *) f="\$DEPLOY_DIR/bin/\$rel";; esac; if [ -f "\$f" ]; then sha256sum "\$f"; else echo "MISS bin \$rel"; fi; done < /tmp/bin.manifest
fi
if [ -s /tmp/systemd.manifest ]; then
  echo "sha listing (systemd)"; while IFS=$'\t' read -r rel sha; do f="/etc/systemd/system/\$rel"; if [ -f "\$f" ]; then sha256sum "\$f"; else echo "MISS systemd \$rel"; fi; done < /tmp/systemd.manifest
fi
'@

Write-Section "Remote deploy"
Write-Info "Running remote deploy script via SSH"
# Pipe the here-string content to remote bash
# Inject sanitized parameter values into the remote script
function Convert-ToBashDqEscaped([string]$s){ if ($null -eq $s) { return "" } return ($s -replace '"','\\"') }
$injected = $remoteScript
$makeManifestB64 = {
  param([string]$Root)
  if (-not $Root -or -not (Test-Path -LiteralPath $Root)) { return "" }
  $lines = New-Object System.Collections.Generic.List[string]
  Get-ChildItem -LiteralPath $Root -Recurse -File | ForEach-Object {
    $full = $_.FullName
    $rel = $full.Substring($Root.Length).TrimStart('\','/')
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
$wrapperContent = @"
#!/bin/bash
set -o pipefail
rc=0
run_cmd="/bin/bash /tmp/chillhub-deploy.sh"
# Prefer line-buffered output for better streaming to Windows consoles
if command -v stdbuf >/dev/null 2>&1; then
  run_cmd="stdbuf -oL -eL $run_cmd"
fi
# Execute without timeout to avoid BusyBox/GNU differences
$run_cmd 2>&1 | sed -u 's/^/remote: /'
rc=${PIPESTATUS[0]}
rm -f /tmp/chillhub-deploy.sh "$0"
exit $rc
"@
$wrapperNormalized = ($wrapperContent -replace "`r`n","`n") -replace "`r",""
[System.IO.File]::WriteAllText($wrapperLocal, $wrapperNormalized, $utf8NoBom)
& $SCP @sshCommon $wrapperLocal "${Remote}:/tmp/run-remote.sh"
Remove-Item -Force $wrapperLocal -ErrorAction SilentlyContinue
& $SSH @sshCommon $Remote '/bin/bash' '/tmp/run-remote.sh'
if ($LASTEXITCODE -ne 0) {
  throw "Remote deploy failed (rc=$LASTEXITCODE)"
}

Write-Ok "Done"
