#!/usr/bin/env bash
# ChillHub deploy script for Ubuntu + nginx
# - Pull latest repo
# - Build Go servers
# - Sync static artifacts (landing, admin_ui)
# - Install binaries and restart services
# - Test and reload nginx
# - (New) Manifest-based integrity verification (site/admin_ui/bin/systemd)
#
# Usage:
#   bash ./scripts/deploy.sh \
#     [--branch <name>] [--no-build] [--repo-dir <path>] \
#     [--jwt-secret <val>] [--admin-user <val>] \
#     [--admin-pass-bcrypt <val>] [--admin-pass <val>] \
#     [--cookie-domain <val>] [--cookie-secure <true|false>] \
#     [--downloads-dir <path>] \
#     [--site-base-url <https://host>] [--fail-on-mismatch] [--strict]
#
# Requirements: git, rsync, go (optional for bcrypt), systemd, nginx, sha256sum, file
set -euo pipefail

## Colors and printers (placed early so all logs are colorized)
NC='\033[0m'; RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[0;33m'; CYAN='\033[0;36m'; MAGENTA='\033[0;35m'; GRAY='\033[0;90m'
if [[ -n "${NO_COLOR:-}" || ! -t 1 ]]; then NC=''; RED=''; GREEN=''; YELLOW=''; CYAN=''; MAGENTA=''; GRAY=''; fi
log(){ echo -e "${CYAN}[deploy]${NC} $*"; }
ok(){ echo -e "${GREEN}[ok]${NC} $*"; }
warn(){ echo -e "${YELLOW}[warn]${NC} $*"; }
err(){ echo -e "${RED}[error]${NC} $*"; }
diag(){ echo -e "${GRAY}[diag]${NC} $*"; }
section(){ local msg="${1:-}"; local line="------------------------------------------------------------"; echo -e "${GRAY}$line${NC}"; echo -e "${MAGENTA}  $msg${NC}"; echo -e "${GRAY}$line${NC}"; }
run(){ echo -e "${GRAY}> $*${NC}"; eval "$*"; }

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
# New: test URL base and integrity controls
SITE_BASE_URL="https://launcher.samoy.love"
FAIL_ON_MISMATCH=0
STRICT_MODE=0
# Optional controls
NO_NGINX_RELOAD=0
ARCH="auto"   # auto|amd64|arm64

# Preflight: ensure required commands exist
need_cmd(){ command -v "$1" >/dev/null 2>&1 || { err "Missing required command: $1"; exit 1; }; }
for c in git rsync sudo nginx systemctl curl sha256sum; do need_cmd "$c"; done

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
    --site-base-url)
      SITE_BASE_URL="${2:-https://launcher.samoy.love}"; shift 2;;
    --fail-on-mismatch)
      FAIL_ON_MISMATCH=1; shift 1;;
    --strict)
      STRICT_MODE=1; shift 1;;
    --no-nginx-reload)
      NO_NGINX_RELOAD=1; shift 1;;
    --arch)
      ARCH="${2:-auto}"; shift 2;;
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

# If downloads dir not provided, default to sibling of REPO_DIR.
#
# Здесь было: PARENT_DIR="$(dirname \"$REPO_DIR\")".
# Внутри $( ) НЕ нужно экранировать кавычки — обратный слэш не съедается, и
# dirname получал аргумент вида "/home/user/Launcher-Project" ВМЕСТЕ с кавычками.
# Результат — путь с ведущей кавычкой, DOWNLOADS_DIR вида '"/home/user/downloads',
# и проверка [[ -d "$DOWNLOADS_DIR" ]] ниже была ложной ВСЕГДА.
#
# Последствие тянулось молча: внешний каталог downloads/ не синхронизировался
# ни разу, установщик на лендинге не обновлялся, а в лог печаталось бодрое
# "not found (skip)" — то есть отказ выглядел как штатная ветка.
if [[ -z "$DOWNLOADS_DIR" ]]; then
  PARENT_DIR="$(dirname "$REPO_DIR")"
  DOWNLOADS_DIR="$PARENT_DIR/downloads"
  DOWNLOADS_DIR_IS_DEFAULT=1
else
  DOWNLOADS_DIR_IS_DEFAULT=0
fi

# Load persisted bcrypt if present.
#
# ВНИМАНИЕ на имена: в файле переменная называется ADMIN_PASSWORD_BCRYPT (так её
# читает сервер), а в скрипте — ADMIN_PASS_BCRYPT. Раньше здесь был просто
# `source`, и подстановка молча не срабатывала: скрипт считал, что учётных данных
# нет, и уходил в интерактивный запрос пароля — то есть неинтерактивный деплой
# по SSH обрывался на сервере, где всё уже настроено.
#
# Файл НЕ подключается через `source`. Bcrypt-хэш выглядит как $2y$12$... —
# при подстановке bash раскрывает $2 и $12 как позиционные параметры, а под
# `set -u` это просто обрывает скрипт с «unbound variable». systemd читает тот
# же файл буквально, поэтому расхождение всплывало только в деплое.
# Заодно `source` исполнял бы содержимое файла с секретами как код.
read_secret(){
  local key="$1" line
  [[ -f "$SECRET_FILE" ]] || return 0
  line=$(sudo grep -m1 "^${key}=" "$SECRET_FILE" 2>/dev/null) || return 0
  line="${line#*=}"
  # Снимаем окружающие кавычки, если они есть (systemd их тоже снимает).
  [[ "$line" == \"*\" ]] && line="${line:1:${#line}-2}"
  [[ "$line" == \'*\' ]] && line="${line:1:${#line}-2}"
  printf '%s' "$line"
}

if [[ -z "$ADMIN_PASS_BCRYPT" ]]; then
  ADMIN_PASS_BCRYPT="$(read_secret ADMIN_PASSWORD_BCRYPT)"
fi

# If neither plain nor bcrypt provided and no persisted secret, prompt user to set password (no echo)
if [[ -z "$ADMIN_PASS" && -z "$ADMIN_PASS_BCRYPT" ]]; then
  warn "Admin credentials are not set. You'll be prompted to set a password (username=admin)."
  read -r -s -p "Enter admin password: " PW1; echo >&2
  read -r -s -p "Confirm admin password: " PW2; echo >&2
  if [[ -z "$PW1" || "$PW1" != "$PW2" ]]; then
    err "Passwords do not match or empty. Aborting."
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
      err "Failed to derive bcrypt hash."
      exit 1
    fi
    # Persist to /etc/chillhub/admin.env
    sudo mkdir -p "$SECRET_DIR"
    {
      echo "ADMIN_USERNAME=admin"
      echo "ADMIN_PASSWORD_BCRYPT=$ADMIN_PASS_BCRYPT"
    } | sudo tee "$SECRET_FILE" >/dev/null
    sudo chmod 0600 "$SECRET_FILE" || true
    ok "Admin credentials stored in $SECRET_FILE (bcrypt only)."
  else
    err "Go is required to derive bcrypt on the server. Install Go or provide --admin-pass-bcrypt."
    exit 1
  fi
fi

## Colors were moved earlier

section "Preflight: директории и доступ"
log "Ensuring target directories exist"
if ! sudo -n true 2>/dev/null; then
  err "sudo требует пароль; настройте NOPASSWD для текущего пользователя или запускайте с подходящими привилегиями"
  exit 1
fi
sudo mkdir -p "$SITE_ROOT" "$LAUNCHER_ROOT" "$LAUNCHER_ROOT/content" "$LAUNCHER_ROOT/manifests" "$LAUNCHER_ROOT/news" "$LAUNCHER_ROOT/admin_ui" /opt/chillhub
sudo mkdir -p "$SITE_ROOT/downloads"


section "Git: обновление репозитория ($BRANCH)"

# Скрипт запускается через sudo, но обращаться к GitHub нужно НЕ от root: ключ
# развёрнут у владельца репозитория, а у root его нет. Раньше деплой падал на
# первом же fetch с «Permission denied (publickey)», хотя тот же fetch от
# владельца проходил. Поэтому все git-команды выполняем от владельца каталога.
REPO_OWNER="$(stat -c '%U' "$REPO_DIR" 2>/dev/null || echo root)"
git_as_owner(){
  if [[ "$(id -un)" == "$REPO_OWNER" ]]; then
    run "git -C \"$REPO_DIR\" $*"
  else
    run "sudo -u \"$REPO_OWNER\" git -C \"$REPO_DIR\" $*"
  fi
}

if [[ ! -d "$REPO_DIR/.git" ]]; then
  run "sudo -u \"$REPO_OWNER\" git clone git@github.com:tr0llex/Launcher-Project.git \"$REPO_DIR\""
fi
git_as_owner "fetch --all --prune"
git_as_owner "checkout $BRANCH"
git_as_owner "config core.filemode false || true"
git_as_owner "pull --ff-only"

# Generate secrets if not provided
if [[ -z "$JWT_SECRET" ]]; then
  if command -v openssl >/dev/null 2>&1; then
    JWT_SECRET=$(openssl rand -base64 48 | tr -d '\n' || true)
  fi
  if [[ -z "$JWT_SECRET" ]]; then
    JWT_SECRET=$(head -c 48 /dev/urandom | base64 | tr -d '\n' || true)
  fi
  if [[ -z "$JWT_SECRET" ]]; then
    warn "Could not auto-generate JWT_SECRET; please provide --jwt-secret"
  else
    ok "Auto-generated JWT_SECRET (48 bytes base64)"
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
      ok "Derived ADMIN_PASSWORD_BCRYPT via Go"
    else
      warn "Failed to derive bcrypt hash; please provide --admin-pass-bcrypt"
    fi
  else
    warn "Go is not available to derive bcrypt; provide --admin-pass-bcrypt"
  fi
fi

if [[ $NO_BUILD -eq 0 ]]; then
  section "Build: Go servers"
  BUILD_DIR=$(mktemp -d -t chillhub-build-XXXXXXXX)
  if [[ "$ARCH" == "amd64" ]]; then
    run "cd \"$REPO_DIR/server\" && go mod tidy && GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -o \"$BUILD_DIR/api\"   ./cmd/api"
    run "cd \"$REPO_DIR/server\" && go mod tidy && GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -o \"$BUILD_DIR/admin\" ./cmd/admin"
  elif [[ "$ARCH" == "arm64" ]]; then
    run "cd \"$REPO_DIR/server\" && go mod tidy && GOOS=linux GOARCH=arm64 CGO_ENABLED=0 go build -o \"$BUILD_DIR/api\"   ./cmd/api"
    run "cd \"$REPO_DIR/server\" && go mod tidy && GOOS=linux GOARCH=arm64 CGO_ENABLED=0 go build -o \"$BUILD_DIR/admin\" ./cmd/admin"
  else
    run "cd \"$REPO_DIR/server\" && go mod tidy && go build -o \"$BUILD_DIR/api\"   ./cmd/api"
    run "cd \"$REPO_DIR/server\" && go mod tidy && go build -o \"$BUILD_DIR/admin\" ./cmd/admin"
  fi
  # Compute shas BEFORE install for later comparison
  BIN_SHA_API=$(sha256sum "$BUILD_DIR/api" | awk '{print $1}')
  BIN_SHA_ADMIN=$(sha256sum "$BUILD_DIR/admin" | awk '{print $1}')
  log "Installing binaries"
  run "sudo install -m 0755 \"$BUILD_DIR/api\"   \"$API_BIN\""
  run "sudo install -m 0755 \"$BUILD_DIR/admin\" \"$ADMIN_BIN\""
  # After install, compare installed shas to built
  if [[ -f "$API_BIN" ]]; then INS_SHA_API=$(sha256sum "$API_BIN" | awk '{print $1}'); else INS_SHA_API=""; fi
  if [[ -f "$ADMIN_BIN" ]]; then INS_SHA_ADMIN=$(sha256sum "$ADMIN_BIN" | awk '{print $1}'); else INS_SHA_ADMIN=""; fi
  if [[ -n "$INS_SHA_API" ]]; then
    if [[ "$INS_SHA_API" == "$BIN_SHA_API" ]]; then ok "[manifest] bin api OK ($INS_SHA_API)"; else err "[manifest] bin api FAIL expected=$BIN_SHA_API got=$INS_SHA_API"; fi
  else
    warn "[manifest] bin api MISS $API_BIN"
  fi
  if [[ -n "$INS_SHA_ADMIN" ]]; then
    if [[ "$INS_SHA_ADMIN" == "$BIN_SHA_ADMIN" ]]; then ok "[manifest] bin admin OK ($INS_SHA_ADMIN)"; else err "[manifest] bin admin FAIL expected=$BIN_SHA_ADMIN got=$INS_SHA_ADMIN"; fi
  else
    warn "[manifest] bin admin MISS $ADMIN_BIN"
  fi
  run "rm -rf \"$BUILD_DIR\" || true"
  ok "Binaries installed"
else
  section "Build: пропущен (--no-build)"
  :
fi

section "Sync: статика"
log "Sync landing to $SITE_ROOT"
# Keep /downloads separate from repo; sync landing excluding downloads
run "sudo rsync -a --delete --exclude 'downloads/' \"$REPO_DIR/landing/\" \"$SITE_ROOT/\""

# Sync external downloads (next to REPO_DIR) into site downloads.
#
# Пропуск синхронизации ОБЯЗАН быть заметен. Раньше это была строка log-уровня
# "not found (skip)" в общем потоке — и когда каталог не находился из-за бага с
# кавычками (см. DOWNLOADS_DIR выше), деплой годами выглядел успешным, а
# /downloads/ChillHub-Setup.exe на лендинге оставался старым или отсутствовал.
if [[ -d "$DOWNLOADS_DIR" ]]; then
  log "Sync external downloads from $DOWNLOADS_DIR to $SITE_ROOT/downloads"
  run "sudo rsync -a \"$DOWNLOADS_DIR/\" \"$SITE_ROOT/downloads/\""
  ok "External downloads synced: $(find "$DOWNLOADS_DIR" -type f 2>/dev/null | wc -l | tr -d ' ') file(s) from $DOWNLOADS_DIR"
else
  section "ВНИМАНИЕ: внешний каталог downloads НЕ синхронизирован"
  warn "Каталог не найден: $DOWNLOADS_DIR"
  if [[ "$DOWNLOADS_DIR_IS_DEFAULT" == "1" ]]; then
    warn "Путь выбран по умолчанию как сосед репозитория ($REPO_DIR). Если каталог лежит в другом месте — укажите --downloads-dir <path>."
  else
    warn "Путь задан явно через --downloads-dir. Проверьте, что он существует на ЭТОМ хосте."
  fi
  warn "Пока каталог не найден, $SITE_ROOT/downloads/ не обновляется: кнопка скачивания на лендинге отдаёт старый файл или 404."
fi

log "Sync static only (landing, admin_ui). Do not touch server content dirs."
# DO NOT SYNC manifests/, content/ and news/ at all per policy — content is managed by backend/Admin UI
# Admin UI can be fully replaced
run "sudo rsync -a --delete \"$REPO_DIR/server/admin_ui/\"   \"$LAUNCHER_ROOT/admin_ui/\""

# И9: ДИАГНОСТИЧЕСКИЙ ping.txt СОЗДАЁТСЯ ДО СНИМКА PRE, А НЕ ПОСЛЕ.
#
# Раньше он создавался ниже, между снимками PRE и POST, и тут же попадал под
# guard, который ищет изменения в news/ и валит деплой. То есть ПЕРВЫЙ деплой
# на чистый хост падал ВСЕГДА — уже после перезапуска сервисов, то есть в
# состоянии «наполовину выкачено»: сервисы новые, а шаг помечен как провал.
#
# Теперь файл создаётся до снятия PRE, поэтому попадает в оба снимка одинаково
# и guard его не видит как изменение. Сам файл нужен смоук-тесту
# (/assets/ping.txt) как признак того, что раздача новостных ассетов жива.
PING_PATH="$LAUNCHER_ROOT/news/assets/ping.txt"
if [[ ! -f "$PING_PATH" ]]; then
  log "Creating diagnostic $PING_PATH (first deploy on this host)"
  run "sudo mkdir -p \"$(dirname "$PING_PATH")\""
  echo "ok" | sudo tee "$PING_PATH" >/dev/null || true
fi

# Guard snapshot (PRE): capture sample hashes and counts for server-managed dirs to detect unintended changes
TMP_PRE_DIR="/tmp/chillhub-pre"; TMP_POST_DIR="/tmp/chillhub-post"; run "sudo mkdir -p \"$TMP_PRE_DIR\" \"$TMP_POST_DIR\""
sample_hash(){ local d="$1"; if [[ ! -d "$d" ]]; then echo "missing"; return; fi; local list; list=$(LC_ALL=C find "$d" -type f -printf '%P\n' | sort | awk 'NR<=50'); if [[ -z "$list" ]]; then echo "empty"; else while IFS= read -r f; do sha256sum "$d/$f" | awk '{print $1"  "$2}'; done <<< "$list" | sha256sum | awk '{print $1}'; fi; }
for d in content manifests news; do dir="$LAUNCHER_ROOT/$d"; if [[ -d "$dir" ]]; then find "$dir" -type f 2>/dev/null | wc -l | awk '{print $1}' | sudo tee "$TMP_PRE_DIR/${d}.count" >/dev/null; sample_hash "$dir" | sudo tee "$TMP_PRE_DIR/${d}.hash" >/dev/null; else echo "-1" | sudo tee "$TMP_PRE_DIR/${d}.count" >/dev/null; echo "missing" | sudo tee "$TMP_PRE_DIR/${d}.hash" >/dev/null; fi; done

# Ensure admin content root subdirs exist (do not touch content/manifests/news ownership)
run "sudo mkdir -p \"$LAUNCHER_ROOT/tmp\" \"$LAUNCHER_ROOT/content\" \"$LAUNCHER_ROOT/manifests\" \"$LAUNCHER_ROOT/news\""
# Restrict ownership changes to admin_ui and tmp only
log "Ensure ownership for admin_ui and tmp only (preserving content/manifests/news)"
run "sudo chown -R www-data:www-data \"$LAUNCHER_ROOT/admin_ui\" \"$LAUNCHER_ROOT/tmp\""

section "Systemd: юниты и drop-in"
log "Install systemd unit files (if present)"
if [[ -f "$REPO_DIR/deploy/systemd/chillhub-api.service" ]]; then
  run "sudo install -m 0644 \"$REPO_DIR/deploy/systemd/chillhub-api.service\" /etc/systemd/system/chillhub-api.service"
fi
if [[ -f "$REPO_DIR/deploy/systemd/chillhub-admin.service" ]]; then
  run "sudo install -m 0644 \"$REPO_DIR/deploy/systemd/chillhub-admin.service\" /etc/systemd/system/chillhub-admin.service"
fi

# И3: резервное копирование контента (скрипт + таймер).
#
# /var/www/launcher/{content,manifests,news} не лежат в репозитории
# (content/** в .gitignore) и до сих пор не резервировались никуда — копия
# всего опубликованного контента существовала ровно одна. Процедура
# восстановления описана в шапке самого скрипта.
if [[ -f "$REPO_DIR/deploy/backup-content.sh" ]]; then
  log "Install content backup script and timer"
  run "sudo install -m 0755 -o root -g root \"$REPO_DIR/deploy/backup-content.sh\" /usr/local/sbin/chillhub-backup-content.sh"
  run "sudo install -d -m 0700 -o root -g root /var/backups/chillhub"
  if [[ -f "$REPO_DIR/deploy/systemd/chillhub-backup.service" ]]; then
    run "sudo install -m 0644 \"$REPO_DIR/deploy/systemd/chillhub-backup.service\" /etc/systemd/system/chillhub-backup.service"
  fi
  if [[ -f "$REPO_DIR/deploy/systemd/chillhub-backup.timer" ]]; then
    run "sudo install -m 0644 \"$REPO_DIR/deploy/systemd/chillhub-backup.timer\" /etc/systemd/system/chillhub-backup.timer"
    BACKUP_TIMER_INSTALLED=1
  fi
fi

# Configure auth env via systemd drop-in using EnvironmentFile to avoid overwrites
ADMIN_DROPIN_DIR="/etc/systemd/system/chillhub-admin.service.d"
log "Writing/refreshing systemd drop-in for admin auth env"
run "sudo mkdir -p \"$ADMIN_DROPIN_DIR\""
TMPD=$(mktemp)
{
  echo "[Service]"
  echo "EnvironmentFile=$SECRET_FILE"
  [[ -n "$COOKIE_DOMAIN" ]] && echo "Environment=\"COOKIE_DOMAIN=$COOKIE_DOMAIN\""
  [[ -n "$COOKIE_SECURE" ]] && echo "Environment=\"COOKIE_SECURE=$COOKIE_SECURE\""
  # Секреты (JWT_SECRET, ADMIN_USERNAME, ADMIN_PASSWORD_BCRYPT) сюда НЕ пишем —
  # они приходят из $SECRET_FILE с правами 0600. Раньше JWT_SECRET подставлялся
  # прямо в этот файл, а он ставился с правами 0644: секрет подписи админских
  # сессий мог прочитать любой локальный пользователь.
} > "$TMPD"
# 0600, а не 0644: даже без секретов внутри файл описывает конфигурацию доступа,
# и читать его всем незачем.
run "sudo install -m 0600 \"$TMPD\" \"$ADMIN_DROPIN_DIR/override.conf\""
rm -f "$TMPD" || true

section "Nginx: конфиг и перезапуск"
log "Install nginx site config and reload"
# ВАЖНО: раскладываем в СВОЙ файл chillhub-launcher.conf, а не в общий launcher.conf.
# В общем файле исторически жили два проекта (наш и metro.samoy.love), и запись
# поверх него сносила чужой сайт. Сейчас на хосте три независимых конфига, и
# писать в старое имя — значит либо затереть соседа, либо создать второй vhost
# с тем же server_name (conflicting server name, выигрывает случайный).
# Логика установки с бэкапом, nginx -t и откатом вынесена в scripts/deploy-nginx.sh,
# чтобы все пути деплоя вели себя одинаково.
run "sudo install -m 0755 \"$REPO_DIR/scripts/deploy-nginx.sh\" /tmp/chillhub-deploy-nginx.sh"
run "sudo install -m 0644 \"$REPO_DIR/deploy/launcher.conf\" /tmp/chillhub-launcher.conf"
if [[ $NO_NGINX_RELOAD -eq 0 ]]; then
  run "sudo /tmp/chillhub-deploy-nginx.sh /tmp/chillhub-launcher.conf"
  # Check redirect rule presence
  if sudo grep -n "error_page 401 =302 /admin/ui/login.html" /etc/nginx/sites-available/chillhub-launcher.conf >/dev/null 2>&1; then
    ok "[nginx] redirect rule present"
  else
    warn "[nginx] redirect rule NOT present"
  fi
else
  warn "nginx reload skipped (--no-nginx-reload)"
fi

section "Systemd: перезапуск сервисов"
log "Reload systemd and restart services"
run "sudo systemctl daemon-reload"

# И3: таймер бэкапа включаем ЯВНО. Установленный, но не включённый таймер —
# это ровно то же отсутствие бэкапов, только с ложным ощущением, что они есть.
if [[ "${BACKUP_TIMER_INSTALLED:-0}" == "1" ]]; then
  run "sudo systemctl enable --now chillhub-backup.timer"
  if systemctl is-enabled chillhub-backup.timer >/dev/null 2>&1; then
    ok "[backup] таймер включён: $(systemctl list-timers chillhub-backup.timer --no-pager --no-legend 2>/dev/null | head -n1 || echo 'следующий запуск см. systemctl list-timers')"
  else
    warn "[backup] таймер НЕ включён — бэкапов контента не будет. Проверьте: systemctl status chillhub-backup.timer"
  fi
fi
for s in "${SERVICES[@]}"; do
  run "sudo systemctl restart \"$s\""
  run "sudo systemctl status \"$s\" --no-pager -n 3 || true"
done

# И12: ПРОВЕРЯЕМ, ЧТО СЕРВИС ОТВЕЧАЕТ, А НЕ ЧТО SYSTEMD ЕГО ЗАПУСТИЛ.
#
# Здесь стоял `systemctl is-active` сразу после restart. У обоих юнитов
# Type=simple, а это значит, что systemd считает сервис активным В МОМЕНТ
# ЗАПУСКА ПРОЦЕССА — не дожидаясь, пока тот прочитает конфиг, откроет порт или
# вообще останется жив. Процесс, падающий через 200 мс на кривом JWT_SECRET
# или занятом порте, успевал отрапортовать «active», и деплой шёл дальше как
# ни в чём не бывало. Проверка отвечала на вопрос «systemd попытался?», а не
# «сервис работает?».
#
# Опрашиваем сервисы по HTTP на loopback, с ретраями:
#   * admin — /admin/api/health (специально заведён для проб);
#   * api   — /api/maintenance (у публичного API health-эндпоинта нет, а этот
#             GET отвечает всегда и не требует авторизации).
# 127.0.0.1, а не публичный URL: на этом шаге проверяется сам сервис, а не
# nginx с сертификатом — их отказы надо различать. Публичные проверки идут
# ниже, в блоке смоук-тестов.
wait_healthy(){
  local name="$1" url="$2" tries="${3:-30}" i code=""
  for ((i=1; i<=tries; i++)); do
    code=$(curl -s --max-time 3 -o /dev/null -w "%{http_code}" "$url" 2>/dev/null || true)
    if [[ "$code" == "200" ]]; then
      ok "[health] $name отвечает 200 на $url (попытка $i из $tries)"
      return 0
    fi
    sleep 1
  done
  err "[health] $name НЕ ответил 200 на $url за ${tries} с (последний код: ${code:-нет ответа})"
  return 1
}

HEALTH_FAIL=0
wait_healthy "chillhub-admin" "http://127.0.0.1:55777/admin/api/health" 30 || HEALTH_FAIL=1
wait_healthy "chillhub-api"   "http://127.0.0.1:55700/api/maintenance"  30 || HEALTH_FAIL=1

if [[ $HEALTH_FAIL -ne 0 ]]; then
  err "Сервисы не поднялись. Диагностика:"
  for s in "${SERVICES[@]}"; do
    echo "---- systemctl status $s ----"; sudo systemctl status "$s" --no-pager -n 20 || true
    echo "---- journalctl -u $s (последние 50) ----"; sudo journalctl -u "$s" -n 50 --no-pager || true
  done
  exit 1
fi
ok "Services are up and answering"

# Integrity verification (manifests)
section "Integrity: манифесты и сравнение"
MISM_TOTAL=0
# И6: КОНВЕЙЕР ЗАМЕНЁН НА ПОДСТАНОВКУ ПРОЦЕССА — ЭТО НЕ КОСМЕТИКА.
#
# Здесь было `find ... | while read ...; do ... mism=$((mism+1)) ... done`.
# Правая часть конвейера в bash выполняется в ПОДОБОЛОЧКЕ, поэтому все
# инкременты mism и n жили в дочернем процессе и умирали вместе с ним. После
# цикла обе переменные снова были равны нулю.
#
# Следствия, обе молчаливые:
#   * всегда печаталось «all 0 files match» — даже когда файлы реально
#     расходились, и даже когда сверять было нечего;
#   * MISM_TOTAL всегда оставался нулём, поэтому --fail-on-mismatch НЕ
#     срабатывал НИ РАЗУ. Защита существовала только на бумаге, а деплой с
#     битой выкаткой считался успешным.
#
# `done < <(find ...)` оставляет цикл в текущей оболочке: перенаправление ввода
# подоболочку не создаёт.
compare_trees(){
  local label="$1" src="$2" dst="$3"; local mism=0; local n=0;
  local f rel sha_src sha_dst
  if [[ ! -d "$src" ]]; then warn "[manifest] $label: source dir not found: $src"; return 0; fi
  while IFS= read -r -d '' f; do
    rel="${f#"$src/"}"; sha_src=$(sha256sum "$f" | awk '{print $1}')
    if [[ -f "$dst/$rel" ]]; then
      sha_dst=$(sha256sum "$dst/$rel" | awk '{print $1}')
      if [[ "$sha_src" == "$sha_dst" ]]; then echo -e "${GREEN}[manifest] OK  ${NC}$label $rel"; else echo -e "${RED}[manifest] FAIL${NC} $label $rel expected=$sha_src got=$sha_dst"; mism=$((mism+1)); fi
    else
      echo -e "${YELLOW}[manifest] MISS${NC} $label $rel"; mism=$((mism+1))
    fi
    n=$((n+1))
  done < <(find "$src" -type f -print0)
  if [[ $mism -ne 0 ]]; then
    warn "[manifest] $label mismatches: $mism (проверено файлов: $n)"
  elif [[ $n -eq 0 ]]; then
    # «0 файлов совпало» — это не успех, а пустое дерево: раньше такой случай
    # был неотличим от нормального прогона.
    warn "[manifest] $label: в $src нет ни одного файла — сверять нечего"
  else
    ok "[manifest] $label: all $n files match"
  fi
  MISM_TOTAL=$((MISM_TOTAL + mism))
}

# site
compare_trees "site" "$REPO_DIR/landing" "$SITE_ROOT"
# admin_ui
compare_trees "admin" "$REPO_DIR/server/admin_ui" "$LAUNCHER_ROOT/admin_ui"
# bin (only if built now) — детальная проверка уже выполнена сразу после установки выше.
# systemd
if [[ -d "$REPO_DIR/deploy/systemd" ]]; then
  compare_trees "systemd" "$REPO_DIR/deploy/systemd" "/etc/systemd/system"
fi

if [[ $FAIL_ON_MISMATCH -ne 0 && $MISM_TOTAL -ne 0 ]]; then
  err "Manifest mismatches detected (total sections with mism: $MISM_TOTAL)"; exit 1
fi

# И9: ping.txt теперь создаётся ВЫШЕ, до снимка PRE. Здесь его создавать было
# нельзя: он попадал между PRE и POST и сам же валил guard.

section "HTTP: автотесты ($SITE_BASE_URL)"

echo "[guard] Проверка, что серверные директории не изменены: $LAUNCHER_ROOT/content, /manifests, /news"
FAIL_GUARD=0
# POST snapshot and recent changes
sample_hash(){ local d="$1"; if [[ ! -d "$d" ]]; then echo "missing"; return; fi; local list; list=$(LC_ALL=C find "$d" -type f -printf '%P\n' | sort | awk 'NR<=50'); if [[ -z "$list" ]]; then echo "empty"; else while IFS= read -r f; do sha256sum "$d/$f" | awk '{print $1"  "$2}'; done <<< "$list" | sha256sum | awk '{print $1}'; fi; }
for d in content manifests news; do
  dir="$LAUNCHER_ROOT/$d"
  if [[ -d "$dir" ]]; then
    cnt=$(find "$dir" -type f 2>/dev/null | wc -l | awk '{print $1}')
    echo "[guard] $dir files=$cnt"
    printf "%s" "$cnt" | sudo tee "$TMP_POST_DIR/${d}.count" >/dev/null
    sample_hash "$dir" | sudo tee "$TMP_POST_DIR/${d}.hash" >/dev/null
    echo "[guard] recent (<=5min) changes in $dir:"
    # И9: диагностический ping.txt исключён. Его создаёт сам деплой (выше, до
    # снимка PRE), поэтому на первом деплое он по определению «изменён за
    # последние 5 минут» — и guard ловил бы собственный след деплоя, а не
    # чужую запись. Настоящую защиту даёт сравнение PRE/POST ниже.
    recent=$(sudo find "$dir" -type f -mmin -5 ! -path "$PING_PATH" -printf '%TY-%Tm-%Td %TH:%TM %p\n' 2>/dev/null | head -n 50 || true)
    if [[ -n "$recent" ]]; then echo "$recent"; echo "[guard][FAIL] Recent changes detected in $dir"; FAIL_GUARD=1; else echo "(none)"; fi
  else
    echo "[guard] $dir (missing)"
    echo "-1" | sudo tee "$TMP_POST_DIR/${d}.count" >/dev/null; echo "missing" | sudo tee "$TMP_POST_DIR/${d}.hash" >/dev/null
  fi
done
# Compare PRE vs POST
for d in content manifests news; do
  PRE_C=$(sudo cat "$TMP_PRE_DIR/${d}.count" 2>/dev/null || true)
  PRE_H=$(sudo cat "$TMP_PRE_DIR/${d}.hash" 2>/dev/null || true)
  POST_C=$(sudo cat "$TMP_POST_DIR/${d}.count" 2>/dev/null || true)
  POST_H=$(sudo cat "$TMP_POST_DIR/${d}.hash" 2>/dev/null || true)
  if [[ "$PRE_C" != "$POST_C" || "$PRE_H" != "$POST_H" ]]; then
    echo "[guard][FAIL] Snapshot diff for $d (count $PRE_C->$POST_C, hash $PRE_H->$POST_H)"
    FAIL_GUARD=1
  else
    echo "[guard] Snapshot OK for $d"
  fi
done
if [[ $FAIL_GUARD -ne 0 ]]; then
  section "Guard: обнаружены изменения в управляемых сервером директориях"
  err "Guard failed: content/manifests/news changed during deploy. Aborting."
  exit 1
fi

FAIL=0
RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[0;33m'; NC='\033[0m'

# TLS IS VERIFIED — do not put `-k` back. These probes hit the public
# $SITE_BASE_URL over a real Let's Encrypt certificate; with -k an expired cert
# produced a fully green smoke-test run while no browser could open the site
# (and with HSTS the visitor cannot click through). See the same note in
# .github/workflows/deploy.yml.
http_code(){ curl -s --max-time 5 -o /dev/null -w "%{http_code}" "$1"; }
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

# 1) Admin UI (login is public; /admin/ is protected without cookies)
#
# Защищённый ответ — это И 401, И 302: nginx-конфиг содержит
# `error_page 401 =302 /admin/ui/login.html`, то есть отказ авторизации
# намеренно превращается в редирект на форму входа. Тест знал только про 401 и
# поэтому валил весь деплой ровно тогда, когда защита начинала работать как
# задумано.
must_200 "$SITE_BASE_URL/admin/ui/login.html" "Admin UI login"
code=$(http_code "$SITE_BASE_URL/admin/")
if [[ "$code" == "200" ]]; then
  if [[ $STRICT_MODE -eq 1 ]]; then echo -e "[test] ${RED}FAIL${NC} /admin/ returned 200 (expected 401/302)"; FAIL=1; else echo -e "[test] ${YELLOW}WARN${NC} /admin/ returned 200 (maybe already authorized)"; fi
elif [[ "$code" == "401" || "$code" == "302" ]]; then
  echo -e "[test] ${GREEN}PASS${NC} /admin/ protected ($code without cookies)"
else
  echo -e "[test] ${RED}FAIL${NC} /admin/ unexpected code -> $code"; FAIL=1
fi
must_200 "$SITE_BASE_URL/admin/ui/login.js" "Admin UI login script (public)"
# admin.js is behind auth_request together with the rest of the admin shell
# (deploy/launcher.conf). A 200 here would mean the gate is gone.
code=$(http_code "$SITE_BASE_URL/admin/ui/admin.js")
if [[ "$code" == "401" || "$code" == "302" ]]; then
  echo -e "[test] ${GREEN}PASS${NC} /admin/ui/admin.js gated ($code)"
else
  echo -e "[test] ${RED}FAIL${NC} /admin/ui/admin.js should be gated -> $code"; FAIL=1
fi

# 2) Admin API (health is public; protected endpoints are not tested without auth)
must_200 "$SITE_BASE_URL/admin/api/health" "Admin API /admin/api/health"

# 3) Landing site
must_200 "$SITE_BASE_URL/" "Landing /"
must_200 "$SITE_BASE_URL/styles.css" "Landing static /styles.css"

# 4) Manifests
MANI_DIR="$LAUNCHER_ROOT/manifests/launcher"
if [[ -f "$MANI_DIR/latest.json" ]]; then
  must_200 "$SITE_BASE_URL/manifests/launcher/latest.json" "Manifest latest.json"
else
  echo -e "[test] ${YELLOW}SKIP${NC} Manifest latest.json: not present on disk"
fi

# 5) News assets
soft_200_if_exists "/var/www/launcher/news/assets/ping.txt" "$SITE_BASE_URL/assets/ping.txt" "News asset ping.txt"

if [[ $FAIL -ne 0 ]]; then
  section "Diagnostics: сбор логов"
  echo -e "[deploy] ${RED}One or more tests FAILED. Collecting diagnostics...${NC}"
  echo "---- NGINX TEST ----"; sudo nginx -t || true; echo

  echo "---- NGINX ERROR LOG (last 150 lines) ----"; sudo tail -n 150 /var/log/nginx/error.log || true; echo

  echo "---- NGINX SERVER BLOCK (launcher.samoy.love) ----"; sudo nginx -T 2>/dev/null | sed -n '/server_name launcher.samoy.love/,/}/p' || true; echo

  echo "---- SYSTEMD STATUS (api) ----"; sudo systemctl status chillhub-api.service --no-pager -n 50 || true; echo

  echo "---- SYSTEMD STATUS (admin) ----"; sudo systemctl status chillhub-admin.service --no-pager -n 50 || true; echo

  echo "---- JOURNALCTL (api last 150) ----"; sudo journalctl -u chillhub-api.service -e -n 150 || true; echo

  echo "---- JOURNALCTL (admin last 150) ----"; sudo journalctl -u chillhub-admin.service -e -n 150 || true; echo

  echo "---- FS LISTINGS ----"
  echo "[ls] /var/www/site"; sudo ls -la /var/www/site || true; echo

  echo "[ls] /var/www/launcher/admin_ui"; sudo ls -la /var/www/launcher/admin_ui || true; echo

  echo "[ls] /var/www/launcher/news (top)"; sudo ls -la /var/www/launcher/news || true; echo

  echo "[find] /var/www/launcher/news/assets (up to depth 2)"; sudo find /var/www/launcher/news/assets -maxdepth 2 -type f -printf '%p\n' 2>/dev/null | head -n 200 || true; echo

  echo "[ls] $MANI_DIR (manifests)"; sudo ls -la "$MANI_DIR" || true; echo

  echo "[cat] latest.json"; [[ -f "$MANI_DIR/latest.json" ]] && sudo cat "$MANI_DIR/latest.json" || echo "(no latest.json)"; echo

  echo -e "[deploy] ${RED}Diagnostics complete. Please review the logs above.${NC}"
  exit 1
else
  ok "All tests PASSED."
fi

section "Done"
ok "Deployment completed"
    