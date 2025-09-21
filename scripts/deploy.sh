#!/usr/bin/env bash
# ChillHub deploy script for Ubuntu + nginx
# - Pull latest repo
# - Build Go servers
# - Sync static artifacts (landing, admin_ui, content)
# - Install binaries and restart services
# - Test and reload nginx
#
# Usage:
#   bash ./scripts/deploy.sh [--branch <name>] [--no-build] [--repo-dir <path>]
#
# Requirements: git, rsync, go, systemd, nginx
set -euo pipefail

BRANCH="main"
NO_BUILD=0
EXPLICIT_REPO_DIR=""

# Parse args
while [[ $# -gt 0 ]]; do
  case "$1" in
    --branch)
      BRANCH="${2:-main}"; shift 2;;
    --no-build)
      NO_BUILD=1; shift 1;;
    --repo-dir)
      EXPLICIT_REPO_DIR="${2:-}"; shift 2;;
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

log(){ echo "[deploy] $*"; }
run(){ echo "> $*"; eval "$*"; }

log "Ensuring target directories exist"
sudo mkdir -p "$SITE_ROOT" "$LAUNCHER_ROOT" "$LAUNCHER_ROOT/content" "$LAUNCHER_ROOT/manifests" "$LAUNCHER_ROOT/news" "$LAUNCHER_ROOT/admin_ui" /opt/chillhub


log "Updating repository: $REPO_DIR (branch: $BRANCH)"
if [[ ! -d "$REPO_DIR/.git" ]]; then
  run "git clone git@github.com:tr0llex/Launcher-Project.git \"$REPO_DIR\""
fi
run "git -C \"$REPO_DIR\" fetch --all --prune"
run "git -C \"$REPO_DIR\" checkout $BRANCH"
run "git -C \"$REPO_DIR\" config core.filemode false || true"
run "git -C \"$REPO_DIR\" pull --ff-only"

if [[ $NO_BUILD -eq 0 ]]; then
  log "Building Go servers"
  BUILD_DIR=$(mktemp -d -t chillhub-build-XXXXXXXX)
  run "cd \"$REPO_DIR/server\" && go build -o \"$BUILD_DIR/api\"   ./cmd/api"
  run "cd \"$REPO_DIR/server\" && go build -o \"$BUILD_DIR/admin\" ./cmd/admin"
  log "Installing binaries"
  run "sudo install -m 0755 \"$BUILD_DIR/api\"   \"$API_BIN\""
  run "sudo install -m 0755 \"$BUILD_DIR/admin\" \"$ADMIN_BIN\""
  run "rm -rf \"$BUILD_DIR\" || true"
fi

log "Sync landing to $SITE_ROOT"
run "sudo rsync -a --delete \"$REPO_DIR/landing/\" \"$SITE_ROOT/\""

log "Sync static only (landing, admin_ui). Do not touch server content dirs."
# DO NOT SYNC manifests/, content/ and news/ at all per policy — content is managed by backend/Admin UI
# Admin UI can be fully replaced
run "sudo rsync -a --delete \"$REPO_DIR/server/admin_ui/\"   \"$LAUNCHER_ROOT/admin_ui/\""

log "Install nginx site config and reload"
run "sudo install -m 0644 \"$REPO_DIR/deploy/launcher.conf\" /etc/nginx/sites-available/launcher.conf"
run "sudo ln -sf /etc/nginx/sites-available/launcher.conf /etc/nginx/sites-enabled/launcher.conf"
run "sudo nginx -t"
run "sudo systemctl reload nginx"

log "Restart services"
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

# 1) Admin UI
must_200 "https://launcher.samoy.love/admin/" "Admin UI /admin/"
must_200 "https://launcher.samoy.love/admin/ui/admin.js" "Admin UI static /admin/ui/admin.js"

# 2) Admin API
must_200 "https://launcher.samoy.love/admin/api/health" "Admin API /admin/api/health"
must_200 "https://launcher.samoy.love/admin/api/games" "Admin API /admin/api/games"

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
    