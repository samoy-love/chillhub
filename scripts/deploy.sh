#!/usr/bin/env bash
# ChillHub deploy script for Ubuntu + nginx
# - Pull latest repo
# - Build Go servers
# - Sync static artifacts (landing, admin_ui, content)
# - Install binaries and restart services
# - Test and reload nginx
#
# Usage:
#   sudo bash ./scripts/deploy.sh [--branch <name>] [--no-build]
#
# Requirements: git, rsync, go, systemd, nginx
set -euo pipefail

BRANCH="${2:-main}"
if [[ "${1:-}" == "--branch" ]]; then
  BRANCH="${2:-main}"
  shift 2
fi
NO_BUILD=0
if [[ "${1:-}" == "--no-build" ]]; then
  NO_BUILD=1
  shift 1
fi

REPO_DIR="$HOME/Launcher-Project"
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
run "sudo rsync -a --delete \"$REPO_DIR/content/manifests/\" \"$LAUNCHER_ROOT/manifests/\""
run "sudo rsync -a --delete \"$REPO_DIR/content/content/\"   \"$LAUNCHER_ROOT/content/\""
run "sudo rsync -a --delete \"$REPO_DIR/content/news/\"      \"$LAUNCHER_ROOT/news/\""
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

log "Smoke checks"
run "curl -I https://launcher.samoy.love/admin/ || true"
run "curl -I https://launcher.samoy.love/admin/ui/admin.js || true"
run "curl -I https://launcher.samoy.love/admin/api/health || true"
run "curl -fsSL https://launcher.samoy.love/manifests/launcher/latest.json || true"

log "Done"
