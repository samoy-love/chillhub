param(
    [Parameter(Mandatory=$true)] [string]$Host,
    [Parameter(Mandatory=$true)] [string]$User,
    [Parameter(Mandatory=$true)] [string]$KeyPath,
    [string]$Branch = "main",
    [string]$DownloadsDir = "",
    [string]$JwtSecret = "",
    [string]$AdminUser = "admin",
    [string]$AdminPasswordBcrypt = "",
    [string]$AdminPasswordPlain = "",
    [string]$CookieDomain = "launcher.samoy.love",
    [string]$CookieSecure = "true"
)

$ErrorActionPreference = "Stop"

function Write-Info($msg) { Write-Host "[deploy] $msg" }
function Test-CommandAvailable {
  param([Parameter(Mandatory=$true)][string]$Name)
  $cmd = Get-Command $Name -ErrorAction SilentlyContinue
  if (-not $cmd) { throw "Required command not found: $Name" }
}

# Preflight: required tools on Windows host
Test-CommandAvailable go
Test-CommandAvailable ssh
Test-CommandAvailable scp

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

# Build Go servers for linux/amd64
Write-Info "Building Go servers (linux/amd64)"
$env:GOOS = "linux"
$env:GOARCH = "amd64"
$env:CGO_ENABLED = "0"
Push-Location (Join-Path $RepoRoot "server")
try {
  go mod tidy
  go build -o (Join-Path $BinDir "api")   ./cmd/api
  go build -o (Join-Path $BinDir "admin") ./cmd/admin
} finally { Pop-Location }

# Prepare bundle (copy landing, admin_ui, systemd, nginx config)
Write-Info "Preparing deploy bundle"
Copy-Item -Recurse -Force (Join-Path $RepoRoot "landing" "*") $SiteDir
Copy-Item -Recurse -Force (Join-Path $RepoRoot "server" "admin_ui" "*") $AdminUIDir
if (Test-Path (Join-Path $RepoRoot "deploy" "systemd")) {
  Copy-Item -Recurse -Force (Join-Path $RepoRoot "deploy" "systemd" "*") $SystemdDir
}
Copy-Item -Force (Join-Path $RepoRoot "deploy" "launcher.conf") (Join-Path $DeployDir "launcher.conf")

# Upload bundle via scp (ensure remote dirs exist, avoid wildcard expansion issues)
Write-Info "Preparing remote directory structure"
$sshCommon = @("-i", $KeyPath, "-o", "StrictHostKeyChecking=no")
$Remote = "${User}@${Host}"
& ssh @sshCommon $Remote "mkdir -p deploy/site deploy/launcher_admin_ui deploy/bin deploy/systemd deploy/deploy" | Out-Null

Write-Info ("Uploading bundle to {0}:~/deploy" -f $Remote)
$scpCommon = @("-i", $KeyPath, "-o", "StrictHostKeyChecking=no")
& scp @scpCommon -r "$SiteDir/."               "${Remote}:deploy/site/"
& scp @scpCommon -r "$AdminUIDir/."            "${Remote}:deploy/launcher_admin_ui/"
& scp @scpCommon -r "$BinDir/."                "${Remote}:deploy/bin/"
if (Test-Path $SystemdDir) { & scp @scpCommon -r "$SystemdDir/." "${Remote}:deploy/systemd/" }
& scp @scpCommon        (Join-Path $DeployDir "launcher.conf") "${Remote}:deploy/deploy/launcher.conf"

# Remote deploy script (executed on server)
$remoteScript = @'
set -eux
DEPLOY_DIR="\$HOME/deploy"
SITE_DIR="/var/www/site"
LAUNCHER_DIR="/var/www/launcher"
OPT_DIR="/opt/chillhub"
NGINX_SITE_AVAILABLE="/etc/nginx/sites-available/launcher.conf"
NGINX_SITE_ENABLED="/etc/nginx/sites-enabled/launcher.conf"

sudo mkdir -p "\$SITE_DIR" "\$LAUNCHER_DIR/admin_ui" "\$OPT_DIR"

# Sync landing site
sudo rsync -a --delete "\$DEPLOY_DIR/site/" "\$SITE_DIR/"
# Sync Admin UI static only
sudo rsync -a --delete "\$DEPLOY_DIR/launcher_admin_ui/" "\$LAUNCHER_DIR/admin_ui/"

# Install binaries
sudo install -d -m 0755 "\$OPT_DIR"
sudo install -m 0755 "\$DEPLOY_DIR/bin/api" "\$OPT_DIR/api"
sudo install -m 0755 "\$DEPLOY_DIR/bin/admin" "\$OPT_DIR/admin"

# Systemd units (optional)
if [ -d "\$DEPLOY_DIR/systemd" ]; then
  sudo install -m 0644 "\$DEPLOY_DIR/systemd/chillhub-api.service" /etc/systemd/system/chillhub-api.service || true
  sudo install -m 0644 "\$DEPLOY_DIR/systemd/chillhub-admin.service" /etc/systemd/system/chillhub-admin.service || true
  sudo systemctl daemon-reload || true
  sudo systemctl enable chillhub-api.service || true
  sudo systemctl enable chillhub-admin.service || true
fi

# Optional: write admin auth env drop-in (from parameters)
ADMIN_DROPIN_DIR="/etc/systemd/system/chillhub-admin.service.d"
JWT_SECRET="$JwtSecret"
ADMIN_USER="$AdminUser"
ADMIN_PASS_BCRYPT="$AdminPasswordBcrypt"
ADMIN_PASS_PLAIN="$AdminPasswordPlain"
COOKIE_DOMAIN="$CookieDomain"
COOKIE_SECURE="$CookieSecure"
if [ -n "\$ADMIN_PASS_PLAIN" ] && [ -z "\$ADMIN_PASS_BCRYPT" ]; then
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
    '}' > "\$TMPGO"
  ADMIN_PASS_BCRYPT=$(PW="\$ADMIN_PASS_PLAIN" go run "\$TMPGO" 2>/dev/null || true)
  rm -f "\$TMPGO" || true
fi

if [ -n "\$ADMIN_USER\$ADMIN_PASS_BCRYPT\$JWT_SECRET\$COOKIE_DOMAIN\$COOKIE_SECURE" ]; then
  sudo mkdir -p "\$ADMIN_DROPIN_DIR"
  TMPD=$(mktemp)
  {
    echo "[Service]"
    [ -n "\$COOKIE_DOMAIN" ] && echo "Environment=COOKIE_DOMAIN=\$COOKIE_DOMAIN"
    [ -n "\$COOKIE_SECURE" ] && echo "Environment=COOKIE_SECURE=\$COOKIE_SECURE"
    [ -n "\$JWT_SECRET" ] && echo "Environment=JWT_SECRET=\$JWT_SECRET"
    [ -n "\$ADMIN_USER" ] && echo "Environment=ADMIN_USERNAME=\$ADMIN_USER"
    [ -n "\$ADMIN_PASS_BCRYPT" ] && echo "Environment=ADMIN_PASSWORD_BCRYPT=\$ADMIN_PASS_BCRYPT"
  } > "\$TMPD"
  sudo install -m 0644 "\$TMPD" "\$ADMIN_DROPIN_DIR/override.conf"
  rm -f "\$TMPD" || true
fi

# Nginx site config
sudo install -m 0644 "\$DEPLOY_DIR/deploy/launcher.conf" "\$NGINX_SITE_AVAILABLE"
sudo ln -sf "\$NGINX_SITE_AVAILABLE" "\$NGINX_SITE_ENABLED"
sudo nginx -t
sudo systemctl reload nginx

# Reload services
sudo systemctl daemon-reload || true
sudo systemctl restart chillhub-api.service || true
sudo systemctl restart chillhub-admin.service || true

# Optional: sync external downloads directory
DOWNLOADS_DIR="$DownloadsDir"
if [ -n "\$DOWNLOADS_DIR" ] && [ -d "\$DOWNLOADS_DIR" ]; then
  sudo mkdir -p "\$SITE_DIR/downloads"
  sudo rsync -a "\$DOWNLOADS_DIR/" "\$SITE_DIR/downloads/"
fi

# Smoke tests
FAIL=0
http_code() { curl -ks -o /dev/null -w "%{http_code}" "\$1"; }
must_200() { url="\$1"; name="\$2"; code=$(http_code "\$url"); if [ "\$code" = "200" ]; then echo "[test] PASS \$name (\$url)"; else echo "[test] FAIL \$name (\$url) -> \$code"; FAIL=1; fi; }

must_200 "https://launcher.samoy.love/admin/ui/login.html" "Admin UI login"
code=$(http_code "https://launcher.samoy.love/admin/")
if [ "\$code" = "200" ]; then echo "[test] WARN /admin/ returned 200 (maybe authorized)"; elif [ "\$code" = "401" ]; then echo "[test] PASS /admin/ protected (401)"; else echo "[test] FAIL /admin/ -> \$code"; FAIL=1; fi
must_200 "https://launcher.samoy.love/admin/ui/admin.js" "Admin UI static admin.js"
must_200 "https://launcher.samoy.love/admin/api/health" "Admin API health"
must_200 "https://launcher.samoy.love/" "Landing root"
must_200 "https://launcher.samoy.love/styles.css" "Landing styles"

if curl -ksf "https://launcher.samoy.love/manifests/launcher/latest.json" >/dev/null; then
  echo "[test] PASS manifests/launcher/latest.json"
else
  echo "[test] WARN manifests/launcher/latest.json not present"
fi
if curl -ksf "https://launcher.samoy.love/assets/ping.txt" >/dev/null; then
  echo "[test] PASS assets/ping.txt"
else
  echo "[test] WARN assets/ping.txt not present"
fi

if [ \$FAIL -ne 0 ]; then
  echo "[deploy] One or more tests FAILED. Collecting diagnostics..."
  echo "---- NGINX TEST ----"; sudo nginx -t || true
  echo "---- NGINX ERROR LOG (last 150) ----"; sudo tail -n 150 /var/log/nginx/error.log || true
  echo "---- SYSTEMD STATUS (api) ----"; sudo systemctl status chillhub-api.service --no-pager -n 30 || true
  echo "---- SYSTEMD STATUS (admin) ----"; sudo systemctl status chillhub-admin.service --no-pager -n 30 || true
  echo "---- JOURNALCTL (api last 150) ----"; sudo journalctl -u chillhub-api.service -e -n 150 || true
  echo "---- JOURNALCTL (admin last 150) ----"; sudo journalctl -u chillhub-admin.service -e -n 150 || true
  exit 1
fi
'@

Write-Info "Running remote deploy script via SSH"
$sshCommon = @("-i", $KeyPath, "-o", "StrictHostKeyChecking=no")
$Remote = "${User}@${Host}"
# Pipe the here-string content to remote bash
[System.Text.Encoding]::UTF8.GetBytes($remoteScript) |
  & ssh @sshCommon $Remote "bash -lc 'cat > /tmp/deploy-$$.sh && bash /tmp/deploy-$$.sh'"

Write-Info "Done"
