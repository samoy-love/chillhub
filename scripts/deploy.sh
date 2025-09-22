#!/usr/bin/env bash
# ChillHub deploy script for Ubuntu + nginx
# - Pull latest repo
# - Build Go servers
# - Sync static artifacts (landing, admin_ui, content)
# - Install binaries and restart services
# - Test and reload nginx
#
# Usage:
#   bash ./scripts/deploy.sh [--branch <name>] [--no-build] [--repo-dir <path>] \
#                           [--jwt-secret <val>] [--admin-user <val>] \
#                           [--admin-pass-bcrypt <val>] [--cookie-domain <val>] [--cookie-secure <true|false>]
#
# Requirements: git, rsync, go, systemd, nginx
set -euo pipefail

BRANCH="main"
NO_BUILD=0
EXPLICIT_REPO_DIR=""
# Auth/systemd settings (optional; if set will be written as a systemd drop-in for chillhub-admin)
JWT_SECRET=""
ADMIN_USER=""
ADMIN_PASS_BCRYPT=""
ADMIN_PASS=""
COOKIE_DOMAIN="launcher.samoy.love"
COOKIE_SECURE="true"
# External installers directory (defaults to sibling of REPO_DIR)
DOWNLOADS_DIR=""

# Preflight: ensure required commands exist
need_cmd(){ command -v "$1" >/dev/null 2>&1 || { echo "[deploy][error] Missing required command: $1" >&2; exit 1; }; }
for c in git rsync sudo nginx systemctl curl; do need_cmd "$c"; done

# Parse args
while [[ $# -gt 0 ]]; do
  case "$1" in
    --branch)
      BRANCH="${2:-main}"; shift 2;;
    --no-build)
      NO_BUILD=1; shift 1;;
    --repo-dir)
      EXPLICIT_REPO_DIR="${2:-}"; shift 2;;
    --jwt-secret)
      JWT_SECRET="${2:-}"; shift 2;;
    --admin-user)
      ADMIN_USER="${2:-}"; shift 2;;
    --admin-pass-bcrypt)
      ADMIN_PASS_BCRYPT="${2:-}"; shift 2;;
    --admin-pass)
      ADMIN_PASS="${2:-}"; shift 2;;
    --cookie-domain)
      COOKIE_DOMAIN="${2:-}"; shift 2;;
    --cookie-secure)
      COOKIE_SECURE="${2:-true}"; shift 2;;
    --downloads-dir)
      DOWNLOADS_DIR="${2:-}"; shift 2;;
    *)
      echo "[deploy][warn] Unknown arg: $1"; shift 1;;
  esac
done

# Determine repository directory
if [[ -n "$EXPLICIT_REPO_DIR" ]]; then
  REPO_DIR="$EXPLICIT_REPO_DIR"
else
  # Try current git root
  if GIT_ROOT=$(git rev-parse --show-toplevel 2>/dev/null); then
    REPO_DIR="$GIT_ROOT"
  else
    # If script is started via sudo, $HOME becomes /root. Prefer original user's home when available.
    if [[ -n "${SUDO_USER:-}" && -d "/home/${SUDO_USER}" ]]; then
      REPO_DIR="/home/${SUDO_USER}/Launcher-Project"
    else
      REPO_DIR="$HOME/Launcher-Project"
    fi
  fi
fi
SITE_ROOT="/var/www/site"
LAUNCHER_ROOT="/var/www/launcher"
API_BIN="/opt/chillhub/api"
ADMIN_BIN="/opt/chillhub/admin"
SERVICES=(chillhub-api.service chillhub-admin.service)

# Secrets persistence (outside repo)
SECRET_DIR="/etc/chillhub"
SECRET_FILE="$SECRET_DIR/admin.env"

# If downloads dir not provided, default to sibling of REPO_DIR
if [[ -z "$DOWNLOADS_DIR" ]]; then
  PARENT_DIR="$(dirname \"$REPO_DIR\")"
  DOWNLOADS_DIR="$PARENT_DIR/downloads"
fi

# Load persisted bcrypt if present
if [[ -z "$ADMIN_PASS_BCRYPT" && -f "$SECRET_FILE" ]]; then
  # shellcheck disable=SC1090
  source "$SECRET_FILE" || true
fi

# If neither plain nor bcrypt provided and no persisted secret, prompt user to set password (no echo)
if [[ -z "$ADMIN_PASS" && -z "$ADMIN_PASS_BCRYPT" ]]; then
  echo "[deploy] Admin credentials are not set. You'll be prompted to set a password (username=admin)." >&2
  read -r -s -p "Enter admin password: " PW1; echo >&2
  read -r -s -p "Confirm admin password: " PW2; echo >&2
  if [[ -z "$PW1" || "$PW1" != "$PW2" ]]; then
    echo "[deploy][error] Passwords do not match or empty. Aborting." >&2
    exit 1
  fi
  if command -v go >/dev/null 2>&1; then
    TMPGO=$(mktemp -t bcrypt-XXXXXX.go)
    cat >"$TMPGO" <<'EOF'
package main
import (
  "fmt"
  "golang.org/x/crypto/bcrypt"
  "os"
)
func main(){
  p := os.Getenv("PW")
  if p == "" { fmt.Println(""); return }
  h, err := bcrypt.GenerateFromPassword([]byte(p), 12)
  if err != nil { fmt.Println(""); return }
  fmt.Print(string(h))
}
EOF
    ADMIN_PASS_BCRYPT=$(PW="$PW1" go run "$TMPGO" 2>/dev/null || true)
    rm -f "$TMPGO" || true
    if [[ -z "$ADMIN_PASS_BCRYPT" ]]; then
      echo "[deploy][error] Failed to derive bcrypt hash." >&2
      exit 1
    fi
    # Persist to /etc/chillhub/admin.env
    sudo mkdir -p "$SECRET_DIR"
    {
      echo "ADMIN_USERNAME=admin"
      echo "ADMIN_PASSWORD_BCRYPT=$ADMIN_PASS_BCRYPT"
    } | sudo tee "$SECRET_FILE" >/dev/null
    sudo chmod 0600 "$SECRET_FILE" || true
    echo "[deploy] Admin credentials stored in $SECRET_FILE (bcrypt only)."
  else
    echo "[deploy][error] Go is required to derive bcrypt on the server. Install Go or provide --admin-pass-bcrypt." >&2
    exit 1
  fi
fi

log(){ echo "[deploy] $*"; }
run(){ echo "> $*"; eval "$*"; }

log "Ensuring target directories exist"
sudo mkdir -p "$SITE_ROOT" "$LAUNCHER_ROOT" "$LAUNCHER_ROOT/content" "$LAUNCHER_ROOT/manifests" "$LAUNCHER_ROOT/news" "$LAUNCHER_ROOT/admin_ui" /opt/chillhub
sudo mkdir -p "$SITE_ROOT/downloads"


log "Updating repository: $REPO_DIR (branch: $BRANCH)"
if [[ ! -d "$REPO_DIR/.git" ]]; then
  run "git clone git@github.com:tr0llex/Launcher-Project.git \"$REPO_DIR\""
fi
run "git -C \"$REPO_DIR\" fetch --all --prune"
run "git -C \"$REPO_DIR\" checkout $BRANCH"
run "git -C \"$REPO_DIR\" config core.filemode false || true"
run "git -C \"$REPO_DIR\" pull --ff-only"

# Generate secrets if not provided
if [[ -z "$JWT_SECRET" ]]; then
  if command -v openssl >/dev/null 2>&1; then
    JWT_SECRET=$(openssl rand -base64 48 | tr -d '\n' || true)
  fi
  if [[ -z "$JWT_SECRET" ]]; then
    JWT_SECRET=$(head -c 48 /dev/urandom | base64 | tr -d '\n' || true)
  fi
  if [[ -z "$JWT_SECRET" ]]; then
    echo "[deploy][warn] Could not auto-generate JWT_SECRET; please provide --jwt-secret"
  else
    echo "[deploy] Auto-generated JWT_SECRET (48 bytes base64)"
  fi
fi

# Ensure admin username defaults to 'admin' if not provided
if [[ -z "$ADMIN_USER" ]]; then
  ADMIN_USER="admin"
fi

# Secrets persistence (outside repo)
SECRET_DIR="/etc/chillhub"
SECRET_FILE="$SECRET_DIR/admin.env"

# If plain password provided but bcrypt not, derive bcrypt via a short Go snippet (cost=12)
if [[ -n "$ADMIN_PASS" && -z "$ADMIN_PASS_BCRYPT" ]]; then
  if command -v go >/dev/null 2>&1; then
    TMPGO=$(mktemp -t bcrypt-XXXXXX.go)
    cat >"$TMPGO" <<'EOF'
package main
import (
  "fmt"
  "golang.org/x/crypto/bcrypt"
  "os"
)
func main(){
  p := os.Getenv("PW")
  if p == "" { fmt.Println(""); return }
  h, err := bcrypt.GenerateFromPassword([]byte(p), 12)
  if err != nil { fmt.Println(""); return }
  fmt.Print(string(h))
}
EOF
    ADMIN_PASS_BCRYPT=$(PW="$ADMIN_PASS" go run "$TMPGO" 2>/dev/null || true)
    rm -f "$TMPGO" || true
    if [[ -n "$ADMIN_PASS_BCRYPT" ]]; then
      echo "[deploy] Derived ADMIN_PASSWORD_BCRYPT via Go"
    else
      echo "[deploy][warn] Failed to derive bcrypt hash; please provide --admin-pass-bcrypt"
    fi
  else
    echo "[deploy][warn] Go is not available to derive bcrypt; provide --admin-pass-bcrypt"
  fi
fi

if [[ $NO_BUILD -eq 0 ]]; then
  log "Building Go servers"
  BUILD_DIR=$(mktemp -d -t chillhub-build-XXXXXXXX)
  run "cd \"$REPO_DIR/server\" && go mod tidy && go build -o \"$BUILD_DIR/api\"   ./cmd/api"
  run "cd \"$REPO_DIR/server\" && go mod tidy && go build -o \"$BUILD_DIR/admin\" ./cmd/admin"
  log "Installing binaries"
  run "sudo install -m 0755 \"$BUILD_DIR/api\"   \"$API_BIN\""
  run "sudo install -m 0755 \"$BUILD_DIR/admin\" \"$ADMIN_BIN\""
  run "rm -rf \"$BUILD_DIR\" || true"
fi

log "Sync landing to $SITE_ROOT"
# Keep /downloads separate from repo; sync landing excluding downloads
run "sudo rsync -a --delete --exclude 'downloads/' \"$REPO_DIR/landing/\" \"$SITE_ROOT/\""

# Sync external downloads (next to REPO_DIR) into site downloads
if [[ -d "$DOWNLOADS_DIR" ]]; then
  log "Sync external downloads from $DOWNLOADS_DIR to $SITE_ROOT/downloads"
  run "sudo rsync -a \"$DOWNLOADS_DIR/\" \"$SITE_ROOT/downloads/\""
else
  log "External downloads directory not found: $DOWNLOADS_DIR (skip)"
fi

log "Sync static only (landing, admin_ui). Do not touch server content dirs."
# DO NOT SYNC manifests/, content/ and news/ at all per policy — content is managed by backend/Admin UI
# Admin UI can be fully replaced
run "sudo rsync -a --delete \"$REPO_DIR/server/admin_ui/\"   \"$LAUNCHER_ROOT/admin_ui/\""

log "Install systemd unit files (if present)"
if [[ -f "$REPO_DIR/deploy/systemd/chillhub-api.service" ]]; then
  run "sudo install -m 0644 \"$REPO_DIR/deploy/systemd/chillhub-api.service\" /etc/systemd/system/chillhub-api.service"
fi
if [[ -f "$REPO_DIR/deploy/systemd/chillhub-admin.service" ]]; then
  run "sudo install -m 0644 \"$REPO_DIR/deploy/systemd/chillhub-admin.service\" /etc/systemd/system/chillhub-admin.service"
fi

# Configure auth env via systemd drop-in using EnvironmentFile to avoid overwrites
ADMIN_DROPIN_DIR="/etc/systemd/system/chillhub-admin.service.d"
log "Writing/refreshing systemd drop-in for admin auth env"
run "sudo mkdir -p \"$ADMIN_DROPIN_DIR\""
TMPD=$(mktemp)
{
  echo "[Service]"
  echo "EnvironmentFile=$SECRET_FILE"
  [[ -n "$COOKIE_DOMAIN" ]] && echo "Environment=COOKIE_DOMAIN=$COOKIE_DOMAIN"
  [[ -n "$COOKIE_SECURE" ]] && echo "Environment=COOKIE_SECURE=$COOKIE_SECURE"
  [[ -n "$JWT_SECRET" ]] && echo "Environment=JWT_SECRET=$JWT_SECRET"
  # ADMIN_USERNAME/PASSWORD_BCRYPT come from $SECRET_FILE; explicit CLI values can override by rewriting the file
} > "$TMPD"
run "sudo install -m 0644 \"$TMPD\" \"$ADMIN_DROPIN_DIR/override.conf\""
rm -f "$TMPD" || true

log "Install nginx site config and reload"
run "sudo install -m 0644 \"$REPO_DIR/deploy/launcher.conf\" /etc/nginx/sites-available/launcher.conf"
run "sudo ln -sf /etc/nginx/sites-available/launcher.conf /etc/nginx/sites-enabled/launcher.conf"
run "sudo nginx -t"
run "sudo systemctl reload nginx"

log "Reload systemd and restart services"
run "sudo systemctl daemon-reload"
for s in "${SERVICES[@]}"; do
  run "sudo systemctl restart \"$s\""
  run "sudo systemctl status \"$s\" --no-pager -n 3 || true"
done

# Create news/assets/ping.txt for diagnostics if missing (does not overwrite user files)
PING_PATH="/var/www/launcher/news/assets/ping.txt"
if [[ ! -f "$PING_PATH" ]]; then
  echo "ok" | sudo tee "$PING_PATH" >/dev/null || true
fi

log "Autotests"

FAIL=0
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[0;33m'; NC='\033[0m'

http_code(){ curl -ks -o /dev/null -w "%{http_code}" "$1"; }
must_200(){ local url="$1"; local name="$2"; local code; code=$(http_code "$url");
  if [[ "$code" == "200" ]]; then
    echo -e "[test] ${GREEN}PASS${NC} $name ($url)"
  else
    echo -e "[test] ${RED}FAIL${NC} $name ($url) -> $code"; FAIL=1
  fi
}
soft_200_if_exists(){ local path="$1"; local url="$2"; local name="$3";
  if [[ -f "$path" ]]; then
    must_200 "$url" "$name"
  else
    echo -e "[test] ${YELLOW}SKIP${NC} $name: $path not found"
  fi
}

# 1) Admin UI (login is public; /admin/ is protected and should be 401 without cookies)
must_200 "https://launcher.samoy.love/admin/ui/login.html" "Admin UI login"
code=$(http_code "https://launcher.samoy.love/admin/")
if [[ "$code" == "200" ]]; then
  echo -e "[test] ${YELLOW}WARN${NC} /admin/ returned 200 (maybe already authorized)"
elif [[ "$code" == "401" ]]; then
  echo -e "[test] ${GREEN}PASS${NC} /admin/ protected (401 without cookies)"
else
  echo -e "[test] ${RED}FAIL${NC} /admin/ unexpected code -> $code"; FAIL=1
fi
must_200 "https://launcher.samoy.love/admin/ui/admin.js" "Admin UI static /admin/ui/admin.js"

# 2) Admin API (health is public; protected endpoints are not tested without auth)
must_200 "https://launcher.samoy.love/admin/api/health" "Admin API /admin/api/health"

# 3) Landing site
must_200 "https://launcher.samoy.love/" "Landing /"
must_200 "https://launcher.samoy.love/styles.css" "Landing static /styles.css"

# 4) Manifests
MANI_DIR="$LAUNCHER_ROOT/manifests/launcher"
if [[ -f "$MANI_DIR/latest.json" ]]; then
  must_200 "https://launcher.samoy.love/manifests/launcher/latest.json" "Manifest latest.json"
else
  echo -e "[test] ${YELLOW}SKIP${NC} Manifest latest.json: not present on disk"
fi

# 5) News assets
soft_200_if_exists "/var/www/launcher/news/assets/ping.txt" "https://launcher.samoy.love/assets/ping.txt" "News asset ping.txt"

if [[ $FAIL -ne 0 ]]; then
  echo -e "[deploy] ${RED}One or more tests FAILED. Collecting diagnostics...${NC}"
  echo "---- NGINX TEST ----"; sudo nginx -t || true
  echo "---- NGINX ERROR LOG (last 150 lines) ----"; sudo tail -n 150 /var/log/nginx/error.log || true
  echo "---- NGINX SERVER BLOCK (launcher.samoy.love) ----"; sudo nginx -T 2>/dev/null | sed -n '/server_name launcher.samoy.love/,/}/p' || true
  echo "---- SYSTEMD STATUS (api) ----"; sudo systemctl status chillhub-api.service --no-pager -n 50 || true
  echo "---- SYSTEMD STATUS (admin) ----"; sudo systemctl status chillhub-admin.service --no-pager -n 50 || true
  echo "---- JOURNALCTL (api last 150) ----"; sudo journalctl -u chillhub-api.service -e -n 150 || true
  echo "---- JOURNALCTL (admin last 150) ----"; sudo journalctl -u chillhub-admin.service -e -n 150 || true
  echo "---- FS LISTINGS ----"
  echo "[ls] /var/www/site"; sudo ls -la /var/www/site || true
  echo "[ls] /var/www/launcher/admin_ui"; sudo ls -la /var/www/launcher/admin_ui || true
  echo "[ls] /var/www/launcher/news (top)"; sudo ls -la /var/www/launcher/news || true
  echo "[find] /var/www/launcher/news/assets (up to depth 2)"; sudo find /var/www/launcher/news/assets -maxdepth 2 -type f -printf '%p\n' 2>/dev/null | head -n 200 || true
  echo "[ls] $MANI_DIR (manifests)"; sudo ls -la "$MANI_DIR" || true
  echo "[cat] latest.json"; [[ -f "$MANI_DIR/latest.json" ]] && sudo cat "$MANI_DIR/latest.json" || echo "(no latest.json)"
  echo -e "[deploy] ${RED}Diagnostics complete. Please review the logs above.${NC}"
  exit 1
else
  echo -e "[deploy] ${GREEN}All tests PASSED.${NC}"
fi

log "Done"
    