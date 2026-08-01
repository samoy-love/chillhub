# Deploy — справка

См. также:
- Основной обзор и матрица деплоя: [`README.md#карта-шагов-автодеплой-vs-вручную`](../README.md#карта-шагов-автодеплой-vs-вручную)
- Общая документация/спецификации API/Admin: [`Documentation.md`](../Documentation.md)

Содержание:
- [Nginx (prod) — полный пример конфига](#nginx-prod--полный-пример-конфига)
- [Сжатие при раздаче (gzip)](#сжатие-при-раздаче-gzip)
- [Переменные окружения сервисов](#переменные-окружения-сервисов)

## Nginx (prod) — полный пример конфига

Полный пример актуального конфига для хоста `launcher.samoy.love`. Этот файл служит справкой к рабочему конфигу `deploy/launcher.conf` и может использоваться для сравнения/отладки.

> **Внимание:** блок ниже — упрощённая иллюстрация, а не копия боевого конфига. В нём нет security-заголовков, HSTS, CSP, правил кеширования, `client_max_body_size 30g`, таймаутов 6h, директив сжатия, а также публичных локаций `= /feedback/submit`, `= /metrics/report` и `= /admin/api/health` (они объявлены ДО защищённого `location /admin/api/`, иначе `auth_request` закроет их для лаунчера); `listen 443 ssl http2` здесь совпадает с боевым конфигом — на проде nginx 1.24, где отдельной директивы `http2 on;` ещё нет (см. раздел «Версия nginx: прод 1.24»). Единственный источник правды — `deploy/launcher.conf`.

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

## Сжатие при раздаче (gzip)

Короткий вывод: **gzip включён для текста/JSON и намеренно выключен для `/content/`.** zstd и brotli на сегодня не нужны — не потому что «лень собирать модуль», а потому что клиент их не запрашивает (см. ниже).

### 1) Что именно сделано в `deploy/launcher.conf`

На уровне `server { ... }`:

```nginx
gzip on;
gzip_comp_level 5;
gzip_min_length 1024;
gzip_vary on;
gzip_proxied any;
gzip_types application/json application/manifest+json application/javascript
           text/javascript application/x-javascript text/css text/plain
           text/xml application/xml application/rss+xml application/atom+xml
           image/svg+xml text/markdown application/wasm;
gzip_disable "msie6";
```

и ровно одно исключение — в `location ^~ /content/`:

```nginx
gzip off;
```

`text/html` в `gzip_types` не указан сознательно: nginx сжимает его всегда, а явное перечисление в части версий считается ошибкой конфигурации.

### 2) Замеры на реальных данных репозитория

Мерялось локально утилитой `gzip -N` на файлах из `content/manifests/` (это ровно те байты, которые nginx отдаёт из `/manifests/`).

| файл | сырой размер, B | L1 | L4 | **L5 (наш)** | L6 | L9 |
|---|---:|---:|---:|---:|---:|---:|
| `drive-beyond-horizons/1.0.2.json` (2809 файлов) | 905 549 | 290 999 | 274 767 | **272 404** | 272 152 | 268 824 |
| `lethal-company/1.0.7.json` | 905 542 | 290 981 | 274 757 | **272 399** | 272 133 | 268 814 |
| `repo/1.0.0.json` | 89 236 | 30 966 | 28 950 | **28 640** | 28 607 | 28 291 |
| `launcher/1.1.7.json` | 8 207 | 3 228 | 3 102 | **3 052** | 3 050 | 3 034 |
| `_registry/games.json` | 542 | — | — | **не сжимается** (< `gzip_min_length`) | — | — |

Коэффициенты на уровне 5: **3.32x** для крупного манифеста (905 549 → 272 404 B, экономия ≈ 618 KiB на один запрос), **3.12x** для среднего, **2.69x** для мелкого.

Почему уровень 5, а не 9: переход L1→L5 даёт −6.4% к сжатому размеру, а L5→L9 — всего −1.3% при примерно +35% процессорного времени (замер: 10 проходов по манифесту 905 KB — 0.355 s на L5 против 0.470 s на L9). Уровень 5 — колено кривой для этих данных.

Почему это важно: манифест качается при **каждой** проверке обновления, для каждой игры, каждым клиентом. Это самый частый крупный текстовый ответ на сервере.

### 3) Почему `/content/` — `gzip off`

Разбор состава реальной сборки (`drive-beyond-horizons/1.0.2`, 2809 файлов, 10.6 GiB) по данным самого манифеста:

| расширение | объём | доля |
|---|---:|---:|
| `.lethalbundle` | 4 522 MiB | 41.6% |
| `.assetbundle` | 3 515 MiB | 32.4% |
| без расширения | 1 012 MiB | 9.3% |
| `.ress` | 1 001 MiB | 9.2% |
| `.dll` | 234 MiB | 2.2% |
| `.ogg` | 202 MiB | 1.9% |
| `.assets` / `.mp4` / `.resource` / `.png` | 346 MiB | 3.2% |

Больше 74% байт — Unity-бандлы, внутри уже сжатые (LZ4/LZMA); плюс `.ogg`, `.mp4`, `.png` — тоже готовые сжатые форматы. gzip поверх них даёт около нуля, но стоит процессора на каждом гигабайте.

Кроме бесполезности есть два конкретных вреда:

1. **Ломается `sendfile`/`directio`.** В `location ^~ /content/` настроены `sendfile on; tcp_nopush on; directio 8m;`. Любой фильтр тела (а gzip — фильтр) выключает zero-copy путь: nginx начинает гонять гигабайты через userspace.
2. **Ломается докачка по `Range`.** Клиент возобновляет прерванные загрузки, выставляя `Range` (`launcher/ChillHub/Core/Sync/SimpleSyncService.cs`, `req.Headers.Range = new RangeHeaderValue(existing, null)`). nginx не умеет отдавать диапазон из потока, сжимаемого на лету, — сервер ответил бы `200 OK` целиком, и клиент (там есть явная ветка «если пришёл 200 несмотря на Range — перезаписываем файл заново») скачал бы многогигабайтный файл с нуля.

То есть `gzip off;` в `/content/` — это не косметика, а несущая директива. Не убирайте её.

### 4) Совместимость с клиентом — проверено

`launcher/ChillHub/Core/Net/HttpClientProvider.cs` (файл не менялся):

- `AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate` — распаковка выполняется прозрачно в `HttpClientHandler`;
- `http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate")` — заголовок уходит на каждом запросе.

Значит:

- манифесты, `/api/*` и `/news/*.json` клиент получает уже сжатыми и распаковывает сам, без изменений в коде лаунчера;
- **клиент не объявляет `br` и не объявляет `zstd`.** Даже если поставить соответствующий модуль в nginx, лаунчеру он не даст ничего: без `Accept-Encoding: br`/`zstd` nginx обязан отдать gzip или identity. Выигрыш достался бы только браузерам на лендинге и в админке.

Проверка того, что и клиент, и сжатие ведут себя правильно (сравнение размеров одного и того же манифеста):

```bash
URL=https://launcher.samoy.love/manifests/drive-beyond-horizons/1.0.2.json

# заголовки: ожидаем Content-Encoding: gzip и Vary: Accept-Encoding
curl -sI -H 'Accept-Encoding: gzip' "$URL" | grep -iE 'content-encoding|vary|cache-control'

# фактическая экономия
curl -so /dev/null -H 'Accept-Encoding: gzip'     -w 'gzip:     %{size_download}\n' "$URL"
curl -so /dev/null -H 'Accept-Encoding: identity' -w 'identity: %{size_download}\n' "$URL"

# КОНТРОЛЬ: на /content/ сжатия быть НЕ должно (ожидаем пустой вывод по content-encoding)
curl -sI -H 'Accept-Encoding: gzip' https://launcher.samoy.love/content/<game>/<hash> | grep -i content-encoding

# КОНТРОЛЬ: докачка по Range на /content/ жива (ожидаем 206 Partial Content)
curl -sI -H 'Accept-Encoding: gzip' -H 'Range: bytes=0-1023' \
     https://launcher.samoy.love/content/<game>/<hash> | head -1
```

### 5) Нужен ли zstd или brotli — оценка, а не догадка

Сравнение на том же манифесте `drive-beyond-horizons/1.0.2.json` (905 549 B):

| кодек | размер, B | коэффициент | выигрыш к нашему gzip -5 |
|---|---:|---:|---:|
| gzip -5 (текущий) | 272 404 | 3.32x | — |
| gzip -9 | 268 824 | 3.37x | −3 580 B (−1.3%) |
| zstd -3 | 253 424 | 3.57x | −18 980 B (−7.0%) |
| zstd -9 | 244 919 | 3.70x | −27 485 B (−10.1%) |
| zstd -19 | 226 129 | 4.00x | −46 275 B (−17.0%) |
| brotli -q 5 | 257 320 | 3.52x | −15 084 B (−5.5%) |
| brotli -q 11 | 218 592 | 4.14x | −53 812 B (−19.8%) |

На `repo/1.0.0.json` (89 236 B) картина та же: gzip -5 = 28 640, zstd -19 = 24 490, brotli -q 11 = 23 356.

Вывод: **zstd/brotli объективно лучше gzip на 5–20% сжатого объёма, но внедрять их сейчас не стоит.** Причины, по убыванию веса:

1. **Клиент их не запросит** (см. п. 4). Основной потребитель манифестов — лаунчер, а он умеет только gzip/deflate. Модуль работал бы вхолостую для 100% трафика манифестов.
2. **Ни zstd, ни brotli не входят в стоковую сборку nginx.** `zstd-nginx-module` и `ngx_brotli` — сторонние модули; в Debian/Ubuntu их нет в пакете `nginx`.
3. Экономия измеряется десятками килобайт на запрос, тогда как gzip уже снял основные ~618 KiB из 884 KiB.

Что потребуется от владельца, если внедрять всё-таки решим (для полноты; **сначала нужно расширить клиент**):

- добавить в лаунчер `DecompressionMethods.Brotli` и `br` в `Accept-Encoding` (для zstd в .NET готовой поддержки нет вовсе — пришлось бы тащить стороннюю библиотеку и распаковывать вручную);
- на сервере: либо перейти на сборку/репозиторий с модулем (`nginx-extras`, PPA Ondřej Surý), либо собрать nginx из исходников с `--add-module=/path/to/ngx_brotli` — это ручной пересбор при каждом обновлении nginx и потеря автообновлений безопасности из пакетов;
- подключить модуль (`load_module ...;` в `/etc/nginx/modules-enabled/`), после чего **обязательно** `sudo nginx -t && sudo systemctl reload nginx`. Если модуль не загрузится, `nginx -t` упадёт и reload не пройдёт — то есть это ещё и риск для деплоя.

Оценка: соотношение «риск + ручной пересбор + правка клиента» против «−5…20% от 272 KB» — не в пользу внедрения. Пересмотреть стоит, если манифесты вырастут на порядок или появится веб-клиент.

### 6) Опция без CPU: предсжатые манифесты + `gzip_static`

Альтернатива, которая убирает процессорную стоимость целиком: сжимать манифест один раз при публикации сборки и класть рядом `<version>.json.gz`, а в nginx включить `gzip_static on;` в `location ^~ /manifests/`. Тогда nginx отдаёт готовый файл, можно позволить себе `gzip -9` (268 824 B вместо 272 404 B) и не тратить CPU вообще.

Не включено по умолчанию по двум причинам:

1. требуется модуль `ngx_http_gzip_static_module` — он есть в пакетах Debian/Ubuntu, но это надо подтвердить на конкретном хосте:
   ```bash
   nginx -V 2>&1 | tr ' ' '\n' | grep -i gzip_static   # ожидаем --with-http_gzip_static_module
   ```
2. требуется изменение на стороне публикации сборок (админ-бэкенд должен писать `.gz` рядом с манифестом), иначе директива просто ничего не делает.

Разовое предсжатие уже опубликованных манифестов, если решите попробовать:

```bash
sudo bash -lc 'find /var/www/launcher/manifests -type f -name "*.json" -exec gzip -k -f -9 {} \;'
```

Важно: при таком подходе `.gz` надо перегенерировать при **каждом** изменении `.json`, иначе nginx отдаст устаревшее содержимое.

### 7) Почему сжатие делается в nginx, а не в Go

`/api/*` проксируется на Go (`server/cmd/api`), но компрессию там включать не нужно, и она сознательно не добавлена:

- nginx уже терминирует TLS, знает MIME-тип ответа и корректно ведёт `Vary: Accept-Encoding`;
- одно место конфигурации вместо двух: правило «текст жмём, `/content/` не жмём» существует в одном файле и проверяется одним `nginx -t`;
- в Go пришлось бы городить middleware поверх `httpx` с собственным разбором `Accept-Encoding`, ломая `http.FileServer` (dev-режим раздаёт `/content/` тем же процессом — сжатие там повторило бы ровно ту проблему с `Range`, от которой мы уходим в проде);
- двойное сжатие (Go отдаёт gzip → nginx не сжимает повторно, но и не может изменить уровень) лишает возможности управлять компромиссом из конфига.

Поэтому в `server/internal/httpx` и `server/cmd/api` по этой задаче не менялось ничего.

### 8) Проверка конфига

```bash
# Авторитетная проверка — только на сервере:
sudo nginx -t && sudo systemctl reload nginx
```

#### Раскладка конфигов на сервере

На хосте живёт не только лаунчер, поэтому наш конфиг — **отдельный файл, а не
общий**:

```
/etc/nginx/sites-available/chillhub-launcher.conf   <- копия deploy/launcher.conf
/etc/nginx/sites-enabled/chillhub-launcher.conf     -> симлинк на неё
```

Правила:

- `deploy/launcher.conf` описывает **только** `launcher.samoy.love`, включая
  собственный блок `:80 → :443`. Чужие домены (например `metro.samoy.love`)
  живут в своих файлах в `sites-available` со своими симлинками и своими
  сертификатами — мы их не трогаем и не перечисляем в наших `server_name`;
- деплой обязан перезаписывать **ровно** `chillhub-launcher.conf`. Пока файл
  назывался `launcher.conf` и был общим на два сайта, релиз лаунчера сносил
  чужой проект. Отдельное имя делает это невозможным;
- включение один раз, руками:

```bash
sudo ln -s ../sites-available/chillhub-launcher.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

- если в `sites-enabled` ещё висит старый общий `launcher.conf` — удалить
  симлинк **только** после того, как чужой сайт вынесен в свой файл, иначе
  `metro.samoy.love` отвалится.

#### Версия nginx: прод 1.24, и это важно

На проде **nginx 1.24.0 (Ubuntu)**. Отдельная директива `http2 on;` появилась
только в 1.25 — на 1.24 это неизвестная директива, `nginx -t` падает, и
`systemctl reload nginx` не применяет конфиг вообще. Поэтому в блоке `443`
стоит совместимая форма `listen 443 ssl http2;`.

Она валидна и на 1.24, и на 1.25+ (на свежих версиях выводится одно
предупреждение о депрекации — это нормально и деплою не мешает). Менять на пару
`listen 443 ssl;` + `http2 on;` можно **только** после апгрейда прода до ≥ 1.25,
предварительно прогнав проверку на новой версии.

#### Локальная проверка настоящим `nginx -t` (Docker) — основной способ

```bash
# 1) обязательная проверка — на версии прода:
sh deploy/nginx-check.sh
# 2) проверка «на будущее» — на свежем nginx:
NGINX_IMAGE=nginx:alpine sh deploy/nginx-check.sh
```

Скрипт `deploy/nginx-check.sh` поднимает контейнер с официальным образом nginx и
гоняет там **настоящие** `nginx -T` и `nginx -t`. Что он делает за вас:

- `deploy/launcher.conf` — фрагмент для `sites-available`, он валиден только
  внутри `http { ... }`. Скрипт генерирует одноразовую обёртку
  (`events {}` + `http { include /etc/nginx/sites-enabled/*.conf; }`) и
  подключает конфиг **без изменений**, монтируя его read-only;
- внутри контейнера нет сертификатов Let's Encrypt, поэтому генерируется
  самоподписанный сертификат ровно по тем путям, что указаны в конфиге
  (`/etc/letsencrypt/live/launcher.samoy.love/…`). Боевой конфиг ради теста
  никогда не правится;
- создаются каталоги `/var/www/site`, `/var/www/launcher/…`, чтобы вывод был
  чистым.

Код возврата — родной от nginx: `0` = конфиг в порядке.

Требования: установленный Docker. Образ по умолчанию — `nginx:1.24-alpine`,
то есть версия прода. Результаты последней проверки:

- `nginx/1.24.0` — ok, без ошибок и предупреждений;
- `nginx/1.31.3` (`nginx:alpine`) — ok, одно ожидаемое предупреждение
  `the "listen ... http2" directive is deprecated` (см. раздел про версию выше).

**В CI** этот же скрипт готов запускаться одной строкой (`sh deploy/nginx-check.sh`)
на любом раннере с Docker, но в текущие workflow он ещё не подключён. На сервере
конфиг всё равно проверяется: `.github/workflows/deploy.yml` и `scripts/deploy.sh`
делают бэкап, устанавливают файл, выполняют `nginx -t` и откатываются при ошибке.

#### Чем это лучше прежней проверки `crossplane`

Раньше локально гонялся только парсер `crossplane`. Он проверяет синтаксис,
контексты и число аргументов, но **не** исполняет регулярные выражения, не знает
о модулях и не заменяет `nginx -t`. Именно из-за этого в конфиге долго жила
незамеченная ошибка: локации для «хешированных» ассетов были записаны как
`[a-f0-9]\{8,\}`. Такое `nginx -t` принимает, но PCRE трактует `\{` как
**литеральную** фигурную скобку — то есть паттерн требовал в имени файла
подстроку `{8,}` и не совпадал никогда. Все хешированные ассеты проваливались в
следующую локацию и получали `must-revalidate` вместо годового `immutable`.
Починка — заключить регулярку в двойные кавычки и убрать экранирование:
`location ~* "^/.+\.[a-f0-9]{8,}\.(?:css|js|…)$"`. Проверено живым nginx.

Авторитетной проверкой на боевом хосте по-прежнему остаётся `sudo nginx -t`.

---

## Переменные окружения сервисов

Полный список того, что читают Go‑бинарники (`grep -rn os.Getenv server/`).
Задаются в юнитах `deploy/systemd/*.service` через `Environment=` либо, что
предпочтительнее для секретов, через `EnvironmentFile=` (`/etc/chillhub/admin.env`,
права 600).

**Оба сервиса**

| Переменная | По умолчанию | Назначение |
|---|---|---|
| `CONTENT_ROOT` | автоопределение | корень контента; в проде `/var/www/launcher` |

**Public API (`chillhub-api.service`)**

| Переменная | По умолчанию | Назначение |
|---|---|---|
| `API_RATE_LIMIT` | `600` | запросов на адрес клиента за окно; `0` или отрицательное — лимит выключен |
| `API_RATE_WINDOW` | `1m` | окно лимита (формат `time.ParseDuration`) |

Лимит навешен только на JSON‑эндпоинты (`/api/*`, `/news/*.json`), но не на
dev‑раздачу `/content/` и `/manifests/`: установка игры — это тысячи запросов файлов
в несколько потоков, и общий лимит обрывал бы её на середине.

**Admin API (`chillhub-admin.service`)**

| Переменная | По умолчанию | Назначение |
|---|---|---|
| `ADMIN_USERNAME` | — | логин администратора |
| `ADMIN_PASSWORD_BCRYPT` | — | bcrypt‑хэш пароля |
| `ADMIN_PASSWORD_PLAIN` | — | пароль в открытом виде; если задан, сервер сам считает bcrypt и перекрывает `ADMIN_PASSWORD_BCRYPT`. Для dev — на проде используйте bcrypt |
| `JWT_SECRET` | — | секрет подписи токенов сессии |
| `JWT_ACCESS_TTL` | `24h` | срок жизни access‑токена |
| `JWT_REFRESH_TTL` | `720h` (30 суток) | срок жизни refresh‑токена |
| `COOKIE_DOMAIN` | — | домен cookie сессии |
| `COOKIE_SECURE` | `true` | флаг `Secure` у cookie |
| `ADMIN_CORS_ORIGIN` | CORS выключен | список origin через запятую. По умолчанию CORS ОТКЛЮЧЁН: админка отдаётся с того же origin, а авторизация на cookie; `*` вместе с cookie использовать нельзя |
| `FFMPEG_PATH` | ищется в `PATH` | путь к `ffmpeg` для конвертации анимированных изображений; без него анимации сохраняются как есть |

Rate‑лимиты публичных эндпоинтов админ‑процесса заданы в коде и переменными не
настраиваются: `POST /feedback/submit` — 5 запросов в минуту на адрес клиента,
`POST /metrics/report` — 30 в минуту (`server/cmd/admin/main.go`).
