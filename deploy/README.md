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
