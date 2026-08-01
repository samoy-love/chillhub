#!/usr/bin/env bash
# =============================================================================
# ChillHub: резервное копирование серверного контента.
#
# ЗАЧЕМ ЭТО СУЩЕСТВУЕТ (И3)
# -------------------------
# До появления этого скрипта на проде резервировался ТОЛЬКО конфиг nginx
# (/etc/nginx/chillhub-backups). Каталоги
#
#     /var/www/launcher/content    сборки лаунчера и игр (гигабайты)
#     /var/www/launcher/manifests  манифесты версий, latest.json
#     /var/www/launcher/news       новости, обложки, ассеты
#
# не копировались НИКУДА. В репозитории их тоже нет: content/** в .gitignore,
# а всё это загружается операторами через админку. То есть единственная копия
# всего опубликованного контента лежала на одном диске одной машины. Любое
# «rm -rf не туда», сбой диска или ошибка в обработчике удаления версии —
# и восстанавливать нечего и неоткуда.
#
# КАК УСТРОЕНО
# ------------
# Снимки делаются rsync-ом с --link-dest на предыдущий снимок. Неизменившиеся
# файлы не копируются, а становятся ЖЁСТКИМИ ССЫЛКАМИ на файл из прошлого
# снимка. Для нашего профиля данных это принципиально: content/ — это
# многогигабайтные архивы сборок, которые после публикации не меняются
# НИКОГДА. Ежедневный tar.gz такого дерева забил бы диск за неделю и жёг бы
# CPU впустую; при hardlink-снимках каждый следующий снимок стоит ровно
# столько, сколько появилось нового.
#
# Побочный, но важный эффект: каждый снимок — это ОБЫЧНОЕ ДЕРЕВО ФАЙЛОВ, а не
# архив. Восстановление не требует ни распаковки, ни этого скрипта, ни знания
# его формата (см. процедуру ниже).
#
# ПРОЦЕДУРА ВОССТАНОВЛЕНИЯ
# ------------------------
# 0. Посмотреть, что есть:
#        sudo ls -l /var/backups/chillhub
#        sudo cat /var/backups/chillhub/latest/BACKUP-INFO.txt
#
# 1. ОСТАНОВИТЬ пишущий сервис, иначе админка будет писать в каталог во время
#    восстановления и результат будет несогласованным:
#        sudo systemctl stop chillhub-admin.service
#
# 2. Восстановить нужный каталог из выбранного снимка. --delete приводит
#    каталог ровно к состоянию снимка (лишние файлы удаляются); без него —
#    только дописывает недостающее. Начните БЕЗ --delete и с --dry-run:
#        SNAP=/var/backups/chillhub/2026-08-01-030000
#        sudo rsync -aH --dry-run "$SNAP/manifests/" /var/www/launcher/manifests/
#        sudo rsync -aH           "$SNAP/manifests/" /var/www/launcher/manifests/
#
#    Точечное восстановление одного файла — обычный cp, снимок это просто дерево:
#        sudo cp -a "$SNAP/manifests/launcher/latest.json" \
#                   /var/www/launcher/manifests/launcher/
#
# 3. Вернуть владельца (сервис и nginx работают под www-data):
#        sudo chown -R www-data:www-data /var/www/launcher/manifests
#
# 4. Запустить сервис и проверить:
#        sudo systemctl start chillhub-admin.service
#        curl -fsS https://launcher.samoy.love/manifests/launcher/latest.json
#
# ВАЖНО ПРО ГРАНИЦЫ ЭТОЙ ЗАЩИТЫ
# -----------------------------
# Снимки лежат на ТОМ ЖЕ ХОСТЕ и на том же диске. Это защита от логических
# потерь (удалили не то, сломали обработчик, испортили манифест), а НЕ от
# отказа диска и не от компрометации хоста. Полноценный офсайт — отдельная
# задача; смотрите переменную BACKUP_REMOTE ниже как точку подключения.
#
# ЗАПУСК
# ------
#   sudo /usr/local/sbin/chillhub-backup-content.sh          # обычный прогон
#   sudo BACKUP_KEEP=30 /usr/local/sbin/chillhub-backup-content.sh
#   sudo BACKUP_DRY_RUN=1 /usr/local/sbin/chillhub-backup-content.sh
#
# По расписанию запускается chillhub-backup.timer (см. deploy/systemd/).
# =============================================================================
set -euo pipefail

SRC_ROOT="${SRC_ROOT:-/var/www/launcher}"
# Что именно резервируем. Только каталоги, которыми управляет бэкенд/админка:
# admin_ui и tmp раскладываются деплоем и восстанавливаются из репозитория.
SRC_DIRS="${SRC_DIRS:-content manifests news}"
BACKUP_ROOT="${BACKUP_ROOT:-/var/backups/chillhub}"
# Сколько снимков хранить. 14 при ежедневном запуске — две недели, этого
# достаточно, чтобы заметить порчу данных, которую видно не сразу.
BACKUP_KEEP="${BACKUP_KEEP:-14}"
# Не начинать снимок, если свободного места меньше этого порога (МиБ).
# Забить диск бэкапами на хосте, где живут ещё три сайта, — это отказ всех
# четырёх, то есть лекарство хуже болезни.
BACKUP_MIN_FREE_MB="${BACKUP_MIN_FREE_MB:-2048}"
BACKUP_DRY_RUN="${BACKUP_DRY_RUN:-}"

log(){ echo "[backup] $*"; }
err(){ echo "[backup][error] $*" >&2; }

for c in rsync find df stat; do
  command -v "$c" >/dev/null 2>&1 || { err "не найдена обязательная команда: $c"; exit 1; }
done

if [[ ! -d "$SRC_ROOT" ]]; then
  err "исходный каталог не найден: $SRC_ROOT"
  exit 1
fi

mkdir -p "$BACKUP_ROOT"
# 0700: внутри лежит полная копия контента, включая ещё не опубликованные
# сборки и новости. Читать это всем локальным пользователям незачем.
chmod 0700 "$BACKUP_ROOT"

# --- Проверка свободного места -----------------------------------------------
free_mb=$(df -Pm "$BACKUP_ROOT" | awk 'NR==2 {print $4}')
if [[ -n "$free_mb" && "$free_mb" -lt "$BACKUP_MIN_FREE_MB" ]]; then
  err "свободно ${free_mb} МиБ, требуется минимум ${BACKUP_MIN_FREE_MB} МиБ — снимок НЕ делается."
  err "освободите место или уменьшите BACKUP_KEEP (сейчас $BACKUP_KEEP)."
  exit 1
fi

STAMP="$(date -u +%Y-%m-%d-%H%M%S)"
DEST="$BACKUP_ROOT/$STAMP"
LINK_DEST=""
if [[ -d "$BACKUP_ROOT/latest" ]]; then
  # readlink -f, потому что --link-dest не принимает относительный симлинк
  LINK_DEST="$(readlink -f "$BACKUP_ROOT/latest")"
fi

RSYNC_OPTS=(-a -H --delete --numeric-ids)
[[ -n "$BACKUP_DRY_RUN" ]] && RSYNC_OPTS+=(--dry-run)
if [[ -n "$LINK_DEST" && -d "$LINK_DEST" ]]; then
  log "инкрементальный снимок поверх $LINK_DEST (неизменившиеся файлы станут жёсткими ссылками)"
else
  log "предыдущего снимка нет — первый снимок будет полным"
fi

log "снимок -> $DEST"
mkdir -p "$DEST"

failed=0
for d in $SRC_DIRS; do
  src="$SRC_ROOT/$d"
  if [[ ! -d "$src" ]]; then
    log "пропуск: $src не существует"
    continue
  fi
  opts=("${RSYNC_OPTS[@]}")
  if [[ -n "$LINK_DEST" && -d "$LINK_DEST/$d" ]]; then
    opts+=(--link-dest="$LINK_DEST/$d")
  fi
  log "rsync $d"
  if ! rsync "${opts[@]}" "$src/" "$DEST/$d/"; then
    err "rsync для $d завершился с ошибкой"
    failed=1
  fi
done

if [[ -n "$BACKUP_DRY_RUN" ]]; then
  log "BACKUP_DRY_RUN — снимок не фиксируется, каталог $DEST удаляется"
  rm -rf "$DEST"
  exit $failed
fi

if [[ $failed -ne 0 ]]; then
  # Неполный снимок опаснее отсутствующего: он выглядит как валидная точка
  # восстановления. Помечаем его явно и валим прогон.
  err "снимок НЕПОЛНЫЙ — помечаю как FAILED и выхожу с ошибкой"
  mv "$DEST" "$DEST.FAILED"
  exit 1
fi

# --- Метаданные снимка -------------------------------------------------------
{
  echo "ChillHub content snapshot"
  echo "created_utc: $STAMP"
  echo "source:      $SRC_ROOT"
  echo "dirs:        $SRC_DIRS"
  echo "host:        $(hostname 2>/dev/null || echo unknown)"
  echo "link_dest:   ${LINK_DEST:-none (full snapshot)}"
  echo
  echo "files per dir:"
  for d in $SRC_DIRS; do
    if [[ -d "$DEST/$d" ]]; then
      printf '  %-12s %s\n' "$d" "$(find "$DEST/$d" -type f | wc -l | tr -d ' ')"
    fi
  done
  echo
  echo "Восстановление описано в шапке /usr/local/sbin/chillhub-backup-content.sh"
} > "$DEST/BACKUP-INFO.txt"

ln -sfn "$DEST" "$BACKUP_ROOT/latest"
log "снимок готов: $DEST"

# --- Ротация -----------------------------------------------------------------
# Удаляем самые старые снимки сверх BACKUP_KEEP. Сортировка по имени работает
# правильно, потому что имя — это отметка времени в формате YYYY-MM-DD-HHMMSS.
# Жёсткие ссылки означают, что удаление старого снимка НЕ повреждает новые:
# данные исчезают только когда исчезает последняя ссылка на них.
mapfile -t snaps < <(find "$BACKUP_ROOT" -mindepth 1 -maxdepth 1 -type d -name '20*' -printf '%f\n' | sort)
total=${#snaps[@]}
if (( total > BACKUP_KEEP )); then
  drop=$(( total - BACKUP_KEEP ))
  log "ротация: снимков $total, храним $BACKUP_KEEP, удаляем $drop самых старых"
  for ((i = 0; i < drop; i++)); do
    victim="$BACKUP_ROOT/${snaps[$i]}"
    log "удаляю $victim"
    rm -rf "$victim"
  done
else
  log "ротация не требуется: снимков $total, лимит $BACKUP_KEEP"
fi

log "готово. занято под бэкапы: $(du -sh "$BACKUP_ROOT" 2>/dev/null | awk '{print $1}')"
