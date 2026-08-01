#!/usr/bin/env sh
# Validate deploy/launcher.conf with a REAL nginx binary, in Docker.
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
