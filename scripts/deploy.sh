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
SYNC_MANIFESTS=0
EXPLICIT_REPO_DIR=""

# Parse args
while [[ $# -gt 0 ]]; do
  case "$1" in
    --branch)
      BRANCH="${2:-main}"; shift 2;;
    --no-build)
      NO_BUILD=1; shift 1;;
    --sync-manifests)
      SYNC_MANIFESTS=1; shift 1;;
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

# Safety: backup manifests before touching anything
TS=$(date +%Y%m%d-%H%M%S)
if [[ -d "$LAUNCHER_ROOT/manifests" ]]; then
  # Create lightweight tar.gz backup if directory not empty
  if [[ -n $(find "$LAUNCHER_ROOT/manifests" -mindepth 1 -print -quit 2>/dev/null) ]]; then
    BACKUP_DIR="/var/backups"; sudo mkdir -p "$BACKUP_DIR"
    BK_FILE="$BACKUP_DIR/chillhub-manifests-$TS.tar.gz"
    echo "[deploy] Backing up manifests to $BK_FILE"
    sudo tar -C "$LAUNCHER_ROOT" -czf "$BK_FILE" manifests || true
  fi
fi

log "Updating repository: $REPO_DIR (branch: $BRANCH)"
if [[ ! -d "$REPO_DIR/.git" ]]; then
  run "git clone git@github.com:tr0llex/Launcher-Project.git \"$REPO_DIR\""
fi
run "git -C \"$REPO_DIR\" fetch --all --prune"
run "git -C \"$REPO_DIR\" checkout $BRANCH"
run "git -C \"$REPO_DIR\" pull --ff-only"

if [[ $NO_BUILD -eq 0 ]]; then
  log "Building Go servers"
  run "cd \"$REPO_DIR/server\" && go build -o ../api   ./cmd/api"
  run "cd \"$REPO_DIR/server\" && go build -o ../admin ./cmd/admin"
  log "Installing binaries"
  run "sudo install -m 0755 \"$REPO_DIR/api\"   \"$API_BIN\""
  run "sudo install -m 0755 \"$REPO_DIR/admin\" \"$ADMIN_BIN\""
fi

log "Sync landing to $SITE_ROOT"
run "sudo rsync -a --delete \"$REPO_DIR/landing/\" \"$SITE_ROOT/\""

log "Sync content and Admin UI to $LAUNCHER_ROOT"
# IMPORTANT: Preserve server-generated artifacts (uploaded content, news assets, latest.json)
# Non-destructive sync:
#  - manifests: SKIPPED by default (use --sync-manifests to seed only)
#  - content:   seed-only (never overwrite existing server files)
#  - news:      seed-only (никогда не затираем существующие)
if [[ $SYNC_MANIFESTS -eq 1 ]]; then
  echo "[deploy] Seeding manifests from repo (non-destructive)"
  run "sudo rsync -a --ignore-existing \"$REPO_DIR/content/manifests/\" \"$LAUNCHER_ROOT/manifests/\""
else
  echo "[deploy] Skipping manifests sync (use --sync-manifests to seed)"
fi
run "sudo rsync -a --ignore-existing \"$REPO_DIR/content/content/\"   \"$LAUNCHER_ROOT/content/\""
run "sudo rsync -a --ignore-existing \"$REPO_DIR/content/news/\"      \"$LAUNCHER_ROOT/news/\""
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

# Optionally seed latest.json if it is missing but version manifests exist
MANI_DIR="$LAUNCHER_ROOT/manifests/launcher"
if [[ -d "$MANI_DIR" && ! -f "$MANI_DIR/latest.json" ]]; then
  # collect version files except latest.json, strip .json
  mapfile -t _vers < <(find "$MANI_DIR" -maxdepth 1 -type f -name '*.json' ! -name 'latest.json' -printf '%f\n' 2>/dev/null | sed 's/\.json$//' | sort -V)
  if [[ ${#_vers[@]} -gt 0 ]]; then
    BEST_VER="${_vers[-1]}"
    echo "[deploy] Seeding latest.json -> $BEST_VER"
    echo "{ \"version\": \"$BEST_VER\" }" | sudo tee "$MANI_DIR/latest.json" >/dev/null || true
  fi
fi

log "Smoke checks"
run "curl -I https://launcher.samoy.love/admin/ || true"
run "curl -I https://launcher.samoy.love/admin/ui/admin.js || true"
run "curl -I https://launcher.samoy.love/admin/api/health || true"
run "curl -I https://launcher.samoy.love/admin/api/games || true"
if ! curl -fsSL https://launcher.samoy.love/manifests/launcher/latest.json >/dev/null; then
  echo "[deploy][warn] latest.json is 404. If this is a fresh server or you previously deleted manifests, create it via Admin UI (upload launcher build) or place it manually under $LAUNCHER_ROOT/manifests/launcher/latest.json."
fi
run "curl -fsSL https://launcher.samoy.love/assets/ping.txt || true"

log "Done"
    