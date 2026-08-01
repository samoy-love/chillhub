#!/usr/bin/env bash
# =============================================================================
# ChillHub: откат бинарей api/admin на предыдущую версию.
#
# ЗАЧЕМ ЭТО СУЩЕСТВУЕТ (И13)
# --------------------------
# Отката не было нигде, кроме конфига nginx: тот бэкапится перед установкой и
# восстанавливается, если `nginx -t` не прошёл. Бинари же ставились командой
# `install` ПОВЕРХ старых, без сохранения предыдущих. Единственным способом
# вернуться к работающей версии была новая выкатка из git — то есть в момент,
# когда прод лежит, нужно было дождаться сборки, а если ломающий коммит уже в
# main, то ещё и сначала сделать revert.
#
# Теперь каждый путь деплоя перед установкой сохраняет текущие бинари, а этот
# скрипт возвращает их обратно за секунды.
#
# ЧТО СОХРАНЯЕТСЯ
# ---------------
#   /opt/chillhub/api, admin                  — текущие (работающие) бинари
#   /opt/chillhub/rollback/{api,admin}.previous — предыдущие, для отката
#   /opt/chillhub/rollback/{api,admin}.<utc>    — история, ротация ниже
#
# ИСПОЛЬЗОВАНИЕ
# -------------
#   sudo /usr/local/sbin/chillhub-rollback-binaries.sh            # откат обоих
#   sudo /usr/local/sbin/chillhub-rollback-binaries.sh api        # только api
#   sudo /usr/local/sbin/chillhub-rollback-binaries.sh --list     # что доступно
#
# Откат ДВУНАПРАВЛЕННЫЙ: перед восстановлением текущий бинарь сохраняется как
# .previous. Значит повторный запуск возвращает то, что было до отката, — если
# откатились не туда, обратный путь есть, и он такой же быстрый.
#
# Скрипт не трогает ни контент, ни манифесты, ни конфиг nginx: откат бинаря и
# откат данных — разные операции с разными рисками. Для контента есть
# chillhub-backup-content.sh.
# =============================================================================
set -euo pipefail

OPT_DIR="${OPT_DIR:-/opt/chillhub}"
ROLLBACK_DIR="${ROLLBACK_DIR:-$OPT_DIR/rollback}"
# Сколько исторических копий каждого бинаря хранить.
ROLLBACK_KEEP="${ROLLBACK_KEEP:-5}"

log(){ echo "[rollback] $*"; }
err(){ echo "[rollback][error] $*" >&2; }

declare -A UNIT=( [api]=chillhub-api.service [admin]=chillhub-admin.service )
declare -A HEALTH=(
  [api]="http://127.0.0.1:55700/api/maintenance"
  [admin]="http://127.0.0.1:55777/admin/api/health"
)

usage(){
  sed -n '2,40p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

list_available(){
  log "каталог откатов: $ROLLBACK_DIR"
  if [[ ! -d "$ROLLBACK_DIR" ]]; then
    log "(пусто — ни одного сохранённого бинаря; сделайте хотя бы один деплой)"
    return 0
  fi
  local f
  for f in "$ROLLBACK_DIR"/*; do
    [[ -e "$f" ]] || continue
    printf '  %-40s %10s  %s\n' "$(basename "$f")" \
      "$(stat -c '%s' "$f" 2>/dev/null || echo '?')" \
      "$(date -u -d "@$(stat -c '%Y' "$f" 2>/dev/null || echo 0)" '+%Y-%m-%d %H:%M:%S UTC' 2>/dev/null || echo '?')"
  done
}

# Ждём, пока сервис реально ответит. Та же логика, что в деплое (И12): у юнитов
# Type=simple, поэтому systemctl is-active говорит лишь «процесс запущен», а не
# «сервис работает». Откатываться вслепую особенно нельзя — это делается тогда,
# когда уже что-то сломано.
wait_healthy(){
  local name="$1" url="$2" tries="${3:-30}" i code=""
  for ((i = 1; i <= tries; i++)); do
    code=$(curl -s --max-time 3 -o /dev/null -w "%{http_code}" "$url" 2>/dev/null || true)
    if [[ "$code" == "200" ]]; then
      log "$name отвечает 200 (попытка $i)"
      return 0
    fi
    sleep 1
  done
  err "$name НЕ ответил 200 на $url за ${tries} с (последний код: ${code:-нет ответа})"
  return 1
}

rollback_one(){
  local name="$1"
  local live="$OPT_DIR/$name"
  local prev="$ROLLBACK_DIR/$name.previous"

  if [[ ! -f "$prev" ]]; then
    err "нет сохранённой предыдущей версии: $prev"
    err "откатывать нечего — вероятно, с момента установки этого механизма не было ни одного деплоя."
    return 1
  fi

  local stamp; stamp="$(date -u +%Y-%m-%d-%H%M%S)"
  if [[ -f "$live" ]]; then
    # Текущий бинарь становится новым .previous — откат обратим.
    cp -a "$live" "$ROLLBACK_DIR/$name.$stamp"
    cp -a "$live" "$ROLLBACK_DIR/$name.previous.new"
  fi

  log "$name: восстанавливаю $prev -> $live"
  install -m 0755 -o root -g root "$prev" "$live"

  if [[ -f "$ROLLBACK_DIR/$name.previous.new" ]]; then
    mv -f "$ROLLBACK_DIR/$name.previous.new" "$prev"
  fi

  log "$name: перезапускаю ${UNIT[$name]}"
  systemctl restart "${UNIT[$name]}"
  wait_healthy "$name" "${HEALTH[$name]}" 30
}

rotate(){
  local name="$1" total drop i
  mapfile -t hist < <(find "$ROLLBACK_DIR" -maxdepth 1 -type f -name "$name.20*" -printf '%f\n' | sort)
  total=${#hist[@]}
  if (( total > ROLLBACK_KEEP )); then
    drop=$(( total - ROLLBACK_KEEP ))
    for ((i = 0; i < drop; i++)); do
      rm -f "$ROLLBACK_DIR/${hist[$i]}"
    done
    log "$name: ротация истории, удалено $drop (храним $ROLLBACK_KEEP)"
  fi
}

targets=()
case "${1:-}" in
  -h|--help)  usage 0 ;;
  --list)     list_available; exit 0 ;;
  "")         targets=(api admin) ;;
  api|admin)  targets=("$1") ;;
  *)          err "неизвестный аргумент: $1"; usage 2 ;;
esac

if [[ $EUID -ne 0 ]]; then
  err "нужны права root (бинари в $OPT_DIR и перезапуск сервисов)."
  exit 1
fi
mkdir -p "$ROLLBACK_DIR"
chmod 0700 "$ROLLBACK_DIR"

fail=0
for t in "${targets[@]}"; do
  rollback_one "$t" || fail=1
  rotate "$t"
done

if [[ $fail -ne 0 ]]; then
  err "откат завершился с ошибками — смотрите вывод выше и journalctl."
  exit 1
fi
log "откат завершён, сервисы отвечают."
