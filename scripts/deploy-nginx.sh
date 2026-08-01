#!/usr/bin/env bash
#
# Разворачивает nginx-конфиг ChillHub на сервере. Запускается НА СЕРВЕРЕ
# (из пайплайна по SSH или руками), работает идемпотентно.
#
#   sudo ./deploy-nginx.sh /path/to/launcher.conf
#
# Почему отдельный скрипт, а не три строки в пайплайне:
#
#   1. На этом сервере в одном файле /etc/nginx/sites-available/launcher.conf
#      исторически жили ДВА проекта — launcher.samoy.love и чужой
#      metro.samoy.love. Пайплайн копировал наш конфиг поверх этого файла,
#      то есть каждый деплой сносил соседний сайт. Теперь наш конфиг лежит
#      в собственном файле (см. SITE_NAME) и чужих не касается вообще.
#
#   2. Перед reload обязателен `nginx -t`. Если конфиг битый, nginx не
#      перечитает его и продолжит работать на старом — но мы всё равно
#      откатываем файл, чтобы следующий чужой reload не подорвался на нашей
#      ошибке.
#
# Ничего, кроме своего файла и своего симлинка, скрипт не трогает.
set -euo pipefail

SITE_NAME="${SITE_NAME:-chillhub-launcher.conf}"
AVAILABLE_DIR="/etc/nginx/sites-available"
ENABLED_DIR="/etc/nginx/sites-enabled"
BACKUP_DIR="/etc/nginx/chillhub-backups"

SRC="${1:-}"
if [ -z "$SRC" ] || [ ! -f "$SRC" ]; then
  echo "usage: $0 <path-to-launcher.conf>" >&2
  exit 2
fi

if [ "$(id -u)" -ne 0 ]; then
  echo "[error] нужен root (sudo)" >&2
  exit 2
fi

DST="$AVAILABLE_DIR/$SITE_NAME"
LINK="$ENABLED_DIR/$SITE_NAME"
TS="$(date -u +%Y%m%d-%H%M%S)"

mkdir -p "$BACKUP_DIR"

# Проверяем версию nginx: `http2 on;` появился только в 1.25, на 1.24 это
# неизвестная директива и `nginx -t` упадёт. Предупреждаем заранее и понятно,
# иначе разбираться придётся по невнятной ошибке в конце.
NGINX_VER="$(nginx -v 2>&1 | sed 's/.*nginx\///;s/ .*//')"
if grep -qE '^\s*http2\s+on\s*;' "$SRC"; then
  MAJOR="${NGINX_VER%%.*}"
  MINOR="$(echo "$NGINX_VER" | cut -d. -f2)"
  if [ "$MAJOR" -lt 1 ] || { [ "$MAJOR" -eq 1 ] && [ "$MINOR" -lt 25 ]; }; then
    echo "[error] конфиг использует 'http2 on;', а здесь nginx $NGINX_VER (нужен >= 1.25)." >&2
    echo "        Используйте 'listen 443 ssl http2;' либо обновите nginx." >&2
    exit 1
  fi
fi

RESTORE=""
if [ -f "$DST" ]; then
  RESTORE="$BACKUP_DIR/$SITE_NAME.$TS"
  cp -a "$DST" "$RESTORE"
  echo "[backup] $RESTORE"
fi

install -m 0644 -o root -g root "$SRC" "$DST"
echo "[install] $DST"

if [ ! -L "$LINK" ]; then
  ln -sfn "$DST" "$LINK"
  echo "[enable] $LINK -> $DST"
fi

# Показываем, какие ещё сайты включены: если мы вдруг что-то заденем, это
# будет видно в логе деплоя, а не обнаружится жалобой соседнего проекта.
echo "[sites-enabled] $(ls -1 "$ENABLED_DIR" | tr '\n' ' ')"

if ! nginx -t; then
  echo "[error] nginx -t не прошёл — откатываемся, reload НЕ делаем" >&2
  if [ -n "$RESTORE" ]; then
    install -m 0644 -o root -g root "$RESTORE" "$DST"
    echo "[rollback] восстановлен предыдущий $DST" >&2
  else
    rm -f "$DST" "$LINK"
    echo "[rollback] удалён только что добавленный сайт" >&2
  fi
  nginx -t >/dev/null 2>&1 || echo "[warn] конфигурация nginx битая и ДО нашего деплоя" >&2
  exit 1
fi

systemctl reload nginx
echo "[ok] nginx reloaded, версия $NGINX_VER, сайт $SITE_NAME"
