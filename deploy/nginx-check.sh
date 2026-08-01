#!/usr/bin/env sh
# Validate deploy/launcher.conf with a REAL nginx binary, in Docker.
#
# NOTE for Git Bash / MSYS on Windows: it rewrites arguments that look like
# absolute POSIX paths into Windows paths, so container-side paths such as
# /check/run.sh arrive as C:/Program Files/Git/check/run.sh and the run fails
# with "can't open". Disabling the conversion here keeps the script runnable
# from Git Bash without the caller having to know this.
MSYS_NO_PATHCONV=1
MSYS2_ARG_CONV_EXCL='*'
export MSYS_NO_PATHCONV MSYS2_ARG_CONV_EXCL
#
# Why this exists
# ---------------
# launcher.conf used to be checked only by a third-party config parser
# (crossplane), because no nginx binary was available on the dev machine. A
# parser accepts things nginx rejects (and vice versa): unknown directives,
# module-gated directives, `http2 on;` on nginx < 1.25, regex quirks, wrong
# directive contexts. This script runs `nginx -t` for real.
#
# What it does
# ------------
# launcher.conf is a *fragment* meant for /etc/nginx/sites-available, so it is
# only valid inside an `http { ... }` block. The script therefore builds a
# throwaway wrapper nginx.conf that provides `events {}` + `http { include ... }`
# and includes the fragment unmodified (mounted read-only — the production
# config is never edited to make the test pass).
#
# It also creates the two things the fragment references but which do not exist
# inside a bare container:
#   * a self-signed certificate at the real Let's Encrypt paths, so
#     ssl_certificate / ssl_certificate_key can actually be loaded;
#   * /var/www/site and /var/www/launcher/... document roots (nginx -t does not
#     require them, but their absence makes error output noisier).
# Log files under /var/log/nginx are opened by `nginx -t`, and that directory
# already exists in the official image.
#
# Which version to check against
# ------------------------------
# The default is PRODUCTION's version, not the newest one. Production runs
# nginx 1.24.0 (Ubuntu), and 1.24 rejects directives that a 1.27+ image happily
# accepts (`http2 on;` is the one that bit us) — checking only on `nginx:alpine`
# yields a false green and a failed reload on the server.
#
# Usage
# -----
#   sh deploy/nginx-check.sh                       # production version (1.24)
#   NGINX_IMAGE=nginx:alpine sh deploy/nginx-check.sh   # newest, forward check
#
# Run BOTH before touching this config: 1.24 is what must not break today, the
# newest tag tells you whether anything you rely on has been removed.
#
# Exit code is nginx's own: 0 = config OK, non-zero = errors (printed verbatim).
set -eu

NGINX_IMAGE="${NGINX_IMAGE:-nginx:1.24-alpine}"

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
conf="$script_dir/launcher.conf"

if [ ! -f "$conf" ]; then
    echo "nginx-check: $conf not found" >&2
    exit 2
fi
if ! command -v docker >/dev/null 2>&1; then
    echo "nginx-check: docker is required but was not found in PATH" >&2
    exit 2
fi

# =============================================================================
# СТАТИЧЕСКИЕ ПРОВЕРКИ (И8) — И ЧЕСТНАЯ ГРАНИЦА ТОГО, ЧТО ЗДЕСЬ ПРОВЕРЯЕТСЯ
# =============================================================================
# ЧЕГО ЭТОТ СКРИПТ ПРИНЦИПИАЛЬНО НЕ ЛОВИТ
# ---------------------------------------
# `nginx -t` ниже гоняет ТОЛЬКО этот файл, в пустом контейнере, без соседей. На
# проде launcher.conf лежит рядом с конфигами ещё трёх сайтов (metro.samoy.love,
# snakes.samoy.love, samoy.love), и целый класс отказов возникает лишь от их
# СОСЕДСТВА:
#
#   * конфликт server_name — два vhost'а заявляют одно имя на одном порту;
#     nginx оставляет первый и пишет «conflicting server name», причём какой из
#     них окажется первым, зависит от порядка обхода sites-enabled;
#   * два default_server на одном listen — это уже не предупреждение, а ошибка,
#     и она валит перезагрузку nginx ЦЕЛИКОМ, то есть кладёт все четыре сайта;
#   * коллизии имён в общих для http{} пространствах: log_format,
#     limit_req_zone, limit_conn_zone, proxy_cache_path, upstream, map, geo,
#     shared-зоны SSL. Имя, уже занятое соседом, — тоже ошибка уровня http.
#
# Воспроизвести это здесь нельзя, не втащив в проверку чужие конфиги (которых
# нет в этом репозитории и которые меняются без нашего участия). Поэтому:
# ЗЕЛЁНЫЙ РЕЗУЛЬТАТ ЭТОГО СКРИПТА НЕ ГАРАНТИРУЕТ УСПЕШНЫЙ `nginx -t` НА ПРОДЕ.
# Финальная проверка — `sudo nginx -t` на самом хосте, и она есть в обоих путях
# деплоя (scripts/deploy-nginx.sh делает её с откатом).
#
# ЧТО МЫ ВСЁ-ТАКИ ПРОВЕРЯЕМ ЗАРАНЕЕ
# ---------------------------------
# Явные признаки, видимые внутри одного файла:
#   1) наличие default_server — почти наверняка конфликт с соседями;
#   2) дубли server_name на одном и том же listen внутри нашего файла;
#   3) объявления в общих для http{} пространствах имён — печатаем списком,
#      чтобы перед выкаткой их можно было сверить с соседями глазами.
static_checks() {
    _f="$1"
    _fail=0

    echo "nginx-check: статические проверки (см. заметку И8 в этом скрипте)"

    # 1) default_server
    if grep -nE '^[^#]*listen[^;#]*default_server' "$_f" >/dev/null 2>&1; then
        echo "nginx-check: ОШИБКА — найден default_server:" >&2
        grep -nE '^[^#]*listen[^;#]*default_server' "$_f" >&2
        echo "nginx-check:   На хосте четыре сайта. Второй default_server на том же listen — ошибка" >&2
        echo "nginx-check:   уровня http, из-за неё `nginx -t` падает и не перезагружается НИ ОДИН сайт." >&2
        _fail=1
    else
        echo "nginx-check:   [ok] default_server не объявлен"
    fi

    # 2) дубли server_name на одном listen внутри файла.
    #    Пара (порт, имя) собирается по каждому блоку server{}; одно и то же
    #    имя на :80 и на :443 — это норма, а вот дважды на одном порту — нет.
    _dups=$(awk '
        /^[[:space:]]*#/ { next }
        /^[[:space:]]*server[[:space:]]*\{/ { inblk=1; nports=0; nnames=0; delete ports; delete names; next }
        inblk && /listen[[:space:]]/ {
            line=$0; sub(/.*listen[[:space:]]+/, "", line); sub(/;.*/, "", line);
            split(line, a, /[[:space:]]+/);
            p=a[1]; sub(/.*:/, "", p);
            if (p ~ /^[0-9]+$/) { ports[++nports]=p }
        }
        inblk && /server_name[[:space:]]/ {
            line=$0; sub(/.*server_name[[:space:]]+/, "", line); sub(/;.*/, "", line);
            n=split(line, b, /[[:space:]]+/);
            for (i=1; i<=n; i++) if (b[i] != "") names[++nnames]=b[i]
        }
        inblk && /^[[:space:]]*\}/ {
            # Пары внутри ОДНОГО блока дедуплицируем: `listen 80;` вместе с
            # `listen [::]:80;` — это один и тот же порт, записанный дважды
            # (IPv4 и IPv6), а не два разных слушателя с одним именем.
            delete seen;
            for (i=1; i<=nports; i++) for (j=1; j<=nnames; j++) {
                key = ports[i] "|" names[j];
                if (!(key in seen)) { seen[key]=1; print key }
            }
            inblk=0
        }
    ' "$_f" | sort | uniq -d)
    if [ -n "$_dups" ]; then
        echo "nginx-check: ОШИБКА — один и тот же server_name дважды на одном порту:" >&2
        printf '%s\n' "$_dups" | sed 's/^/nginx-check:   порт /; s/|/ имя /' >&2
        _fail=1
    else
        echo "nginx-check:   [ok] дублей server_name на одном порту внутри файла нет"
    fi

    # 3) общие для http{} пространства имён — информационно.
    _shared=$(grep -nE '^[^#]*(log_format|limit_req_zone|limit_conn_zone|proxy_cache_path|upstream|[^_]map[[:space:]]|geo[[:space:]])' "$_f" 2>/dev/null || true)
    _zones=$(grep -oE 'shared:[A-Za-z0-9_]+' "$_f" 2>/dev/null | sort -u || true)
    if [ -n "$_shared" ] || [ -n "$_zones" ]; then
        echo "nginx-check:   имена в ОБЩИХ для http{} пространствах — сверьте с соседними сайтами вручную:"
        [ -n "$_shared" ] && printf '%s\n' "$_shared" | sed 's/^/nginx-check:     /'
        [ -n "$_zones" ]  && printf '%s\n' "$_zones"  | sed 's/^/nginx-check:     зона /'
        echo "nginx-check:     (проверить занятость: sudo nginx -T | grep -n '<имя>')"
    else
        echo "nginx-check:   [ok] объявлений в общих пространствах имён http{} нет"
    fi

    return $_fail
}

if ! static_checks "$conf"; then
    echo "nginx-check: статические проверки НЕ пройдены (см. выше). Docker-проверка не запускалась." >&2
    exit 1
fi
echo

work="$script_dir/.nginx-check.tmp"
rm -rf "$work"
mkdir -p "$work"
# shellcheck disable=SC2064
trap "rm -rf '$work'" EXIT INT TERM

# Minimal surrounding context. Deliberately close to a stock Debian/Ubuntu
# nginx.conf: the same mime.types, the same sites-enabled include pattern.
cat > "$work/nginx.conf" <<'WRAPPER'
worker_processes 1;
error_log stderr warn;
pid /tmp/nginx-check.pid;

events {
    worker_connections 1024;
}

http {
    include       /etc/nginx/mime.types;
    default_type  application/octet-stream;
    sendfile      on;

    include /etc/nginx/sites-enabled/*.conf;
}
WRAPPER

# Runs inside the container.
cat > "$work/run.sh" <<'INNER'
set -eu
# The official nginx images ship libssl but not the openssl CLI.
if ! command -v openssl >/dev/null 2>&1; then
    if command -v apk >/dev/null 2>&1; then
        apk add --no-cache openssl >/dev/null 2>&1
    elif command -v apt-get >/dev/null 2>&1; then
        apt-get update >/dev/null 2>&1 && apt-get install -y --no-install-recommends openssl >/dev/null 2>&1
    fi
fi
if ! command -v openssl >/dev/null 2>&1; then
    echo "nginx-check: openssl is unavailable inside $NGINX_IMAGE and could not be installed" >&2
    exit 2
fi
live=/etc/letsencrypt/live/launcher.samoy.love
mkdir -p "$live"
# Placeholder certificate ONLY for the syntax check; nothing here touches the
# production config or the real certificates.
openssl req -x509 -newkey rsa:2048 -nodes -days 1 \
    -subj "/CN=launcher.samoy.love" \
    -keyout "$live/privkey.pem" -out "$live/fullchain.pem" >/dev/null 2>&1
# chain.pem нужен для ssl_trusted_certificate (OCSP stapling, см. И4 в
# launcher.conf). nginx открывает этот файл ещё на этапе `nginx -t`, так что
# без него проверка падала бы на несуществующем пути, а не на конфиге.
# На проде chain.pem кладёт certbot; здесь достаточно того же самоподписанного
# сертификата — проверяется синтаксис и доступность файла, а не доверие.
#
# ОЖИДАЕМОЕ ПРЕДУПРЕЖДЕНИЕ, НЕ ОШИБКА:
#   [warn] "ssl_stapling" ignored, no OCSP responder URL in the certificate
# У самоподписанного сертификата нет URL OCSP-респондера, поэтому nginx
# отключает сшивку. У настоящего сертификата Let's Encrypt такой URL есть, и
# stapling работает. Гнаться за «чистым» выводом здесь нельзя: единственный
# способ убрать warn — выпилить ssl_stapling из боевого конфига.
cp "$live/fullchain.pem" "$live/chain.pem"
mkdir -p /var/www/site /var/www/launcher/content /var/www/launcher/manifests \
         /var/www/launcher/news/assets /var/www/launcher/admin_ui /var/log/nginx
nginx -v
nginx -T -c /check/nginx.conf >/dev/null
nginx -t -c /check/nginx.conf
INNER

echo "nginx-check: image=$NGINX_IMAGE config=$conf"
docker run --rm \
    -e NGINX_IMAGE="$NGINX_IMAGE" \
    -v "$conf:/etc/nginx/sites-enabled/launcher.conf:ro" \
    -v "$work:/check:ro" \
    "$NGINX_IMAGE" sh /check/run.sh
