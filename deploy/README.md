# Deploy — справка

См. также:
- Основной обзор и матрица деплоя: [`README.md#карта-шагов-автодеплой-vs-вручную`](../README.md#карта-шагов-автодеплой-vs-вручную)
- Общая документация/спецификации API/Admin: [`Documentation.md`](../Documentation.md)

Содержание:
- [Nginx (prod) — полный пример конфига](#nginx-prod-—-полный-пример-конфига)

## Nginx (prod) — полный пример конфига

Полный пример актуального конфига для хоста `launcher.samoy.love`. Этот файл служит справкой к рабочему конфигу `deploy/launcher.conf` и может использоваться для сравнения/отладки.

<details>
<summary>Показать полный конфиг nginx</summary>

```nginx
# HTTP → HTTPS redirect
server {
    listen 80;
    listen [::]:80;
    server_name launcher.samoy.love;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl http2;
    server_name launcher.samoy.love;

    ssl_certificate     /etc/letsencrypt/live/launcher.samoy.love/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/launcher.samoy.love/privkey.pem;

    root /var/www/site;
    index index.html;

    # /assets with fallback to news assets
    location ~ ^/assets/(.*)$ {
        set $asset_tail $1;
        root /var/www/site;
        try_files /assets/$asset_tail @news_assets_fallback;
        add_header Cache-Control "no-cache";
    }
    location @news_assets_fallback {
        internal;
        root /var/www/launcher/news/assets;
        try_files /$asset_tail =404;
        add_header Cache-Control "no-cache";
    }

    # Public API
    location /api/ {
        proxy_pass http://127.0.0.1:55700;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host  $host;
        proxy_set_header X-Forwarded-Port  443;
    }

    # Auth gate
    location = /_auth {
        internal;
        proxy_pass http://127.0.0.1:55777/admin/api/auth/verify;
        proxy_pass_request_body off;
        proxy_set_header Content-Length "";
    }

    # Admin auth endpoints (no auth_request)
    location ^~ /admin/api/auth/ {
        proxy_pass http://127.0.0.1:55777;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host  $host;
        proxy_set_header X-Forwarded-Port  443;
    }

    # Protected admin API
    location /admin/api/ {
        auth_request /_auth;
        proxy_buffering off;
        proxy_request_buffering off;
        proxy_read_timeout 1h;
        proxy_send_timeout 1h;
        proxy_pass http://127.0.0.1:55777;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host  $host;
        proxy_set_header X-Forwarded-Port  443;
    }

    # Admin UI static
    location ^~ /admin/ui/ { alias /var/www/launcher/admin_ui/; add_header Cache-Control "no-store"; }
    location = /admin      { return 302 /admin/; }
    location = /admin/     { auth_request /_auth; root /var/www/launcher/admin_ui; try_files /admin.html =404; add_header Cache-Control "no-store"; }

    # Content buckets
    location /content/   { alias /var/www/launcher/content/;   add_header Cache-Control "no-cache"; }
    location /manifests/ { alias /var/www/launcher/manifests/; add_header Cache-Control "no-cache"; }
    location /news/      { alias /var/www/launcher/news/;      add_header Cache-Control "no-cache"; }
}
```

</details>

См. также рабочий конфиг: `deploy/launcher.conf`.

---

## Сжатие (Gzip/Brotli) — настройка сервера «в первый раз»

В боевом конфиге `deploy/launcher.conf` уже включён безопасный Gzip (внутри `server { ... }`). Этого достаточно для большинства клиентов. Brotli даёт +5–15% к экономии на текстовых файлах, но требует отдельного модуля. Ниже — как включить и проверить.

### 1) Gzip — уже включён

- Ничего ставить не нужно: модуль gzip встроен в nginx.
- В конфиг добавлены:
  - `gzip on; gzip_comp_level 5; gzip_min_length 1024; gzip_vary on; gzip_proxied any;`
  - `gzip_types text/css application/javascript application/json image/svg+xml font/* ...`
  - `gzip_static on;` — если рядом с файлом лежит precompress-версия `.gz`, nginx будет отдавать её.

Опционально можно сделать предсжатие тяжёлых ассетов в пайплайне/на сервере:

```bash
# Предсжать статические ассеты в /var/www/site
sudo bash -lc '
  find /var/www/site -type f \
    \( -name "*.css" -o -name "*.js" -o -name "*.svg" -o -name "*.json" -o -name "*.html" \) \
    -exec gzip -k -f -9 {} \;'
```

### 2) Brotli — опционально (если модуль доступен в вашей сборке nginx)

В репозиториях Debian/Ubuntu модуль может отсутствовать в стандартной сборке. Варианты:

- Установка модульного пакета (если доступно в вашей ОС):

```bash
sudo apt update
# В некоторых дистрибутивах пакет называется nginx-module-brotli или входит в nginx-extras
sudo apt install -y nginx-extras brotli
```

- Либо использовать сторонний репозиторий с модулями nginx (например, PPA от Ondřej Surý). Оцените риски и политику обновлений перед использованием.

После установки модуля подключите его (обычно через файл в `/etc/nginx/modules-enabled/`):

```bash
# Пример (пути зависят от пакета в вашей ОС)
echo 'load_module modules/ngx_http_brotli_filter_module.so;' | sudo tee /etc/nginx/modules-enabled/60-brotli-filter.conf
echo 'load_module modules/ngx_http_brotli_static_module.so;' | sudo tee /etc/nginx/modules-enabled/60-brotli-static.conf

sudo nginx -t && sudo systemctl reload nginx
```

Далее можно включить директивы Brotli в `server { ... }` (или `http { ... }`). Мы не включили их в `deploy/launcher.conf` по умолчанию, чтобы не ломать `nginx -t`, если модуль отсутствует. Если модуль точно есть, добавьте в ваш рабочий конфиг:

```nginx
# Brotli (включайте только если модуль загружается корректно)
brotli on;
brotli_comp_level 5;          # 4–6 — хороший баланс
brotli_static on;             # отдавать заранее сжатые .br при наличии
brotli_types
  text/plain text/css text/javascript application/javascript application/json 
  application/manifest+json application/xml image/svg+xml font/ttf font/otf font/collection;
```

Предсжатие статических файлов Brotli:

```bash
sudo bash -lc '
  find /var/www/site -type f \
    \( -name "*.css" -o -name "*.js" -o -name "*.svg" -o -name "*.json" -o -name "*.html" \) \
    -exec brotli -f -q 11 {} \;'
```

### 3) Проверка, что сжатие работает

```bash
# Gzip: смотрим, что сервер отдаёт gzip при запросе с Accept-Encoding
curl -sI -H 'Accept-Encoding: gzip' https://launcher.samoy.love/styles.css | grep -iE 'content-encoding|cache-control'

# Brotli (если включён):
curl -sI -H 'Accept-Encoding: br' https://launcher.samoy.love/styles.css | grep -iE 'content-encoding|cache-control'

# Проверка фактической экономии
curl -so /dev/null -H 'Accept-Encoding: gzip' -w '%{size_download}\n' https://launcher.samoy.love/styles.css
curl -so /dev/null -H 'Accept-Encoding: identity' -w '%{size_download}\n' https://launcher.samoy.love/styles.css
```

Если `Content-Encoding: gzip`/`br` присутствует — всё ок. Для HTML в нашем конфиге включено `no-store`, но сжатие для HTML nginx всё равно применяет (если не запрещать отдельно).
