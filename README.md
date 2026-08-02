# ChillHub — README

[![CI](https://github.com/tr0llex/Launcher-Project/actions/workflows/lint.yml/badge.svg)](https://github.com/tr0llex/Launcher-Project/actions/workflows/lint.yml)
[![codecov](https://codecov.io/gh/tr0llex/Launcher-Project/branch/main/graph/badge.svg)](https://codecov.io/gh/tr0llex/Launcher-Project)


Кроссплатформенный проект лаунчера и серверной части для распределения игр, автообновлений и новостей.

## Оглавление
- [Обзор](#обзор)
- [Структура проекта](#структура-проекта)
- [Доменная схема (prod)](#доменная-схема-prod)
- [Локальная разработка (Windows 11)](#локальная-разработка-windows-11)
  - [Зависимости](#зависимости)
  - [Клонирование](#клонирование)
  - [Контент (минимум)](#контент-минимум)
  - [Запуск](#запуск)
  - [Admin UI: контент](#admin-ui-контент)
- [Деплой на сервер (Ubuntu + nginx)](#деплой-на-сервер-ubuntu--nginx)
  - [Карта шагов: автодеплой vs вручную](#карта-шагов-автодеплой-vs-вручную)
  - [Подготовка (1 раз)](#подготовка-1-раз)
  - [Автодеплой (Makefile)](#автодеплой-makefile)
  - [Раскладка артефактов (ручная)](#раскладка-артефактов-ручная)
  - [systemd (1 раз)](#systemd-1-раз)
  - [Обновления](#обновления)
  - [Частые ошибки (troubleshooting)](#частые-ошибки-troubleshooting)
  - [Просмотр логов (systemd)](#просмотр-логов-systemd)

 

## Обзор
- ChillHub — лаунчер для Windows 10–11 для кооператива и моддинга.
- Обновления устанавливаются как дифф: скачиваются только изменившиеся файлы, конечная папка игры у пользователя становится точной копией серверной версии.
- Новости лаунчера и игр — Markdown + изображения/ассеты. Показываются в клиенте.
- Сервер на Golang + nginx:
  - Public API — выдаёт список игр, версии, манифесты, новости (JSON/статик).
  - Admin API — приём ZIP‑сборок, активация `latest`, редактирование реестра игр, управление новостями и ассетами, режим технических работ, сводка метрик, инбокс обратной связи.
- Админ‑панель (web) даёт UI для всех административных операций (загрузка версий, правка реестра, новости/ассеты, техработы, метрики, обращения).
- Манифест проверяется структурно до загрузки файлов: отвергаются выход за пределы каталога, дубликаты путей и записи без хешей. Криптографической подписи манифестов в проекте нет.
- Режим технических работ включается с сервера: клиент показывает баннер и блокирует установку/обновление/запуск (по параметрам), а по окончании окна возвращается к нормальной работе сам, без перезапуска.

## Почему ChillHub

ChillHub решает все «боли» ручной установки игр из архивов и при каждом обновлении модпаков экономит время.

- **Автообновления по диффу** — скачиваются только изменения. Без «перекачек» гигабайтов при каждом патче.
- **Гарантированная консистентность** — локальная папка игры становится точной копией серверной сборки. Никаких «мусорных» файлов и конфликтов модов.
- **Один клик — и в игру** — «Обновить» и «Играть» без поиска ссылок, распаковок и ручного копирования.
- **Надёжные докачки** — поддержка HTTP Range, возобновление `.part`, контроль хешей (Blake3/SHA‑256), удаление лишнего, создание пустых папок из манифеста.
- **Быстрые релизы модпаков** — админка принимает ZIP и моментально публикует версию и `latest`.
- **Новости и ассеты** — всё в одном месте: заметки по патчам, обложки, гайды.
- **Без «магии»** — простая архитектура: Golang + nginx, прозрачные манифесты, понятные пути.

### В сравнении с ручной установкой

| Задача | Ручные архивы | ChillHub |
|---|---|---|
| Обновить до новой версии | ✖ Снова скачать весь архив, распаковать, заменить файлы, молиться, чтобы ничего не сломалось | ✔ Скачиваются только изменения. Конечная папка = эталонная версия |
| Конфликты модов/мусор | ✖ Часто остаются старые файлы и «хвосты» | ✔ Лишние файлы удаляются, пустые каталоги создаются по манифесту |
| Скорость | ✖ Медленно, ручная рутина | ✔ Быстро: многопоточная загрузка, возобновление, Range |
| Контроль целостности | ✖ Нет | ✔ Хеш‑проверки (Blake3/SHA‑256) |
| Коммуникация | ✖ Дискорд посты, гугл‑доки | ✔ Новости в клиенте с обложками и Markdown |
| UX | ✖ «Разархивируй сюда», «удали там» | ✔ «Обновить → Играть» |

> Цель ChillHub — чтобы вы играли, а не админили файлы.

## Структура проекта
- Подробнее: спецификации API и Admin описаны в `Documentation.md` — разделы §7 (Public API) и §8 (Admin UI/API).

Быстрые ссылки на спецификации:
- Public API (§7): [`Documentation.md#7-public-api-servercmdapi`](Documentation.md#7-public-api-servercmdapi)
  - Эндпоинты:
    - [`GET /api/games`](Documentation.md#api-get-games)
    - [`GET /api/games/{gameId}`](Documentation.md#api-get-game-by-id)
    - [`GET /api/games/{gameId}/versions/latest`](Documentation.md#api-get-latest)
    - [`GET /api/games/{gameId}/builds`](Documentation.md#api-get-builds)
    - [`GET /api/maintenance`](Documentation.md#api-maintenance)
    - [`GET /news/index.json`](Documentation.md#api-news-index)
    - [`GET /news/games/{gameId}/index.json`](Documentation.md#api-news-game-index)
- Admin UI/API (§8): [`Игры`](Documentation.md#81-игры), [`Лаунчер`](Documentation.md#82-лаунчер), [`Новости`](Documentation.md#83-новости), [`Режим техработ`](Documentation.md#85-режим-технических-работ), [`Метрики`](Documentation.md#86-метрики-лаунчера), [`Обратная связь`](Documentation.md#87-обратная-связь-feedback)

- `server/` — Go: `cmd/api` (public) и `cmd/admin` (admin), плюс статика для dev.
  Обработчики админки живут в `server/internal/adminapi/*`, общая инфраструктура — в
  `server/internal/{adminutil,httpx,maintenance,metrics,ratelimit}`; в `cmd/admin`
  осталась только сборка роутера (см. [`Documentation.md#32-разбиение-серверного-кода-по-пакетам`](Documentation.md#32-разбиение-серверного-кода-по-пакетам)).
- `launcher/` — C# WPF лаунчер (тесты — `launcher/tests/ChillHub.Tests`).
- `updater/` — отдельный exe самообновления и общие preserve‑правила (`updater/UpdatePreserve.cs`).
- `landing/` — статический лендинг (отдаётся на корне домена в проде).
- `content/` — манифесты, файлы версий, новости и их ассеты, состояние режима техработ, метрики, инбокс обратной связи.
- `deploy/` — конфиг nginx (`deploy/launcher.conf`, на сервере — `chillhub-launcher.conf`), проверка конфига `deploy/nginx-check.sh`, systemd юниты.
- `scripts/` — вспомогательные скрипты (локальный запуск, сборка инсталлятора, деплой, утилиты).

## Доменная схема (prod)
- `https://launcher.samoy.love`
  - `/` — лендинг из `landing/`.
  - `/api/*` → Public API (`127.0.0.1:55700`).
  - `/admin/api/*` → Admin API (`127.0.0.1:55777`) 1:1 (без переписывания пути).
  - `/admin/`, `/admin/ui/*` — админ‑UI статика.
  - `/feedback/submit`, `/metrics/report` — публичные POST‑эндпоинты лаунчера, проксируются на Admin API (`127.0.0.1:55777`) без авторизации.
  - `/content/*`, `/manifests/*`, `/news/*` — статика контента.
  - `/assets/*` — единая точка ассетов: сперва ищется в `site/assets`, затем фоллбэк в `launcher/news/assets`. 

На хосте живут и другие проекты, поэтому наш nginx‑конфиг — отдельный файл
`/etc/nginx/sites-available/chillhub-launcher.conf` (копия `deploy/launcher.conf`),
описывающий только `launcher.samoy.love`.

Dev‑порты (локально): Public API `:55700`, Admin API `:55777`.

---

## Локальная разработка (Windows 11)

### Зависимости
- Go 1.22+ (добавьте в PATH)
- .NET 8 SDK
- Git
- Visual Studio 2022 (для WPF) или Visual Studio Code
- (Опционально) PowerShell 7 — подойдёт и Windows PowerShell

### Клонирование
```powershell
mkdir C:\Work; Set-Location C:\Work
git clone <repo_url> "Launcher Project"
Set-Location "Launcher Project"
```

### Контент (минимум)
- Примеры уже есть в `content/`.
- Для своей игры:
  - Манифесты: `content/manifests/<gameId>/{version}.json` и `latest.json`.
  - Файлы версии: `content/content/<gameId>/<version>/files/...`.
  - Новости: `content/news/` и `content/news/games/<gameId>/`.

### Запуск
См. раздел «Скрипты: dev» ниже для актуальных флагов и примеров запуска `scripts/run-dev.ps1`.

Проверка:
- API: http://localhost:55700/api/games
- Admin UI: http://localhost:55777/admin

### Admin UI: контент
- Откройте `http://localhost:55777/admin`.
- Загрузите ZIP сборки игры/лаунчера (вкладка «Игры»/«Лаунчер»), при необходимости активируйте `latest`.
- Отредактируйте реестр игр (вкладка «Игры (редактирование)»).
- Создайте/отредактируйте новости, загрузите/сожмите изображения (вкладка «Новости», «Ассеты»).

### Admin login
- Первый запуск (локально): при старте `./scripts/run-dev.ps1` скрипт запросит пароль для пользователя `admin` и сохранит bcrypt‑хэш вне репозитория — в `%LOCALAPPDATA%\ChillHub\admin.secret.json`. На следующих запусках используется сохранённый хэш.
- Хранение: `%LOCALAPPDATA%/ChillHub/admin.secret.json` (только bcrypt‑хэш, без открытого пароля).
- Ротация: удалите `admin.secret.json` и перезапустите скрипт (будет повторный запрос), либо заранее задайте переменную среды `ADMIN_PASSWORD_BCRYPT`.
- Override: можно задать `ADMIN_PASSWORD_BCRYPT` и/или `ADMIN_USERNAME` в окружении перед запуском (по умолчанию логин всегда `admin`).
- Сервер: при деплое `scripts/deploy.sh` запросит пароль (если не задан через флаги/секреты), сохранит bcrypt в `/etc/chillhub/admin.env` и подключит через systemd `EnvironmentFile`. Ротация — изменить `/etc/chillhub/admin.env` и выполнить `systemctl daemon-reload && systemctl restart chillhub-admin`.
 - Быстрый сброс (локально):
   - Одноразово перед запуском: `./scripts/run-dev.ps1 -ResetAdminAuth ...` — сгенерирует новый случайный пароль (покажет в консоли), пересчитает `ADMIN_PASSWORD_BCRYPT`, создаст новый `JWT_SECRET` и запустит процессы с этими значениями.
   - Во время работы: в интерактивном окне нажмите `p` или русскую `з`, чтобы сбросить пароль/`JWT_SECRET` и автоматически перезапустить процессы.

---

## Скрипты: dev

Подробная документация по скриптам вынесена в `scripts/README.md`.

- Кратко: используйте `scripts/run-dev.ps1` для локального запуска API, Admin и WPF‑клиента. Примеры, флаги и подсказки см. в `scripts/README.md`.
- Управление во время работы: `r/к` — перезапуск всех процессов; `p/з` — сброс пароля админа и `JWT_SECRET` с автоперезапуском; `q/й` — завершение.

### Проверки перед пушем

```bash
cd server && go test ./...                              # серверные пакеты
dotnet test launcher/tests/ChillHub.Tests               # клиент: план/дифф, хеши, проверка манифеста
dotnet run --project updater/tests/ManifestPreserveCheck # петля самообновления
sh deploy/nginx-check.sh                                # настоящий nginx -t в Docker
make lint                                               # web/go/dotnet линтеры
```

## Деплой на сервер (Ubuntu + nginx)

Предполагаем VPS с Ubuntu, пользователь `ubuntu`, домен `launcher.samoy.love` указывает A‑записью на IP сервера.

### Карта шагов: автодеплой vs вручную

Краткий обзор шагов деплоя. Детали API/Admin — см. `Documentation.md` §7/§8.

| Шаг | Makefile (`make deploy`) | Makefile (`make deploy-nobuild`) | Вручную (основные команды/ссылки) |
|---|---|---|---|
| 1. Обновить репозиторий | Да | Да | `git fetch && git checkout BRANCH && git pull` (в `~/Launcher-Project`) — см. [Подготовка](#подготовка-1-раз) |
| 2. Собрать Go‑бинарии (`api`, `admin`) | Да | Нет | `cd server && go build -o ../api ./cmd/api && go build -o ../admin ./cmd/admin` — см. [Раскладка артефактов (ручная)](#раскладка-артефактов-ручная) |
| 3. Установить nginx‑конфиг и перезагрузить | Да | Да | Скопировать `deploy/launcher.conf` в `/etc/nginx/sites-available/chillhub-launcher.conf`, линк в `sites-enabled`, затем `nginx -t && systemctl reload nginx` — см. [Подготовка](#подготовка-1-раз) |
| 4. Синхронизировать лендинг (`landing/`) | Да | Да | `rsync -a --delete ./landing/ /var/www/site/` — см. [Раскладка артефактов (ручная)](#раскладка-артефактов-ручная) |
| 5. Синхронизировать Admin UI (`server/admin_ui/`) | Да | Да | `rsync -a --delete ./server/admin_ui/ /var/www/launcher/admin_ui/` — см. [Раскладка артефактов (ручная)](#раскладка-артефактов-ручная) |
| 6. Контент: `manifests/`, `content/`, `news/` | Не трогает | Не трогает | Управляются через Admin UI или вручную — см. [Раскладка артефактов (ручная)](#раскладка-артефактов-ручная) |
| 7. Разложить бинарии в `/opt/chillhub` | Да | Да | `install -m 0755 ./api /opt/chillhub/api && install -m 0755 ./admin /opt/chillhub/admin` — см. [Раскладка артефактов (ручная)](#раскладка-артефактов-ручная) |
| 8. Перезапустить сервисы | Да | Да | `systemctl restart chillhub-api.service chillhub-admin.service` — см. [Обновления](#обновления) и [systemd](#systemd-1-раз) |
| 9. Смоук‑тесты (API/UI/статик) | Да | Да | Проверить вручную: см. список ниже |

#### Смоук‑чеки
- API (см. подробности в `Documentation.md` §7):
  - [`GET /api/games`](Documentation.md#api-get-games)
  - [`GET /api/games/{gameId}`](Documentation.md#api-get-game-by-id)
  - [`GET /api/games/{gameId}/versions/latest`](Documentation.md#api-get-latest)
  - [`GET /api/games/{gameId}/builds`](Documentation.md#api-get-builds)
  - [`GET /news/index.json`](Documentation.md#api-news-index)
  - [`GET /news/games/{gameId}/index.json`](Documentation.md#api-news-game-index)
- Admin UI: открыть `https://launcher.samoy.love/admin` (должен отдать UI), доступ к API — `https://launcher.samoy.love/admin/api/...` (авторизация настроена).
- Статика: корень `https://launcher.samoy.love/` (лендинг), ассеты `/assets/...` (есть фоллбэк на новости), папки `/manifests/`, `/content/`, `/news/` доступны (кроме приватного в проде).

Примечания:
- Параметры секретов и админ‑учётки для автодеплоя задаются через `EXTRA_ARGS` (см. ниже «Автодеплой (Makefile)»).
- Вручную «Раскладка артефактов» детализирована ниже; автодеплой делает то же самое для статических каталогов, не затрагивая пользовательский контент.

### Подготовка (1 раз)
```bash
sudo apt update && sudo apt install -y nginx rsync
sudo apt install -y certbot python3-certbot-nginx
sudo apt install -y ffmpeg

sudo mkdir -p /var/www/site
sudo mkdir -p /var/www/launcher/{content,manifests,news,admin_ui}
sudo mkdir -p /opt/chillhub

# Клонируйте репозиторий (в домашнюю директорию пользователя)
git clone https://github.com/tr0llex/Launcher-Project.git ~/Launcher-Project || true

# Сертификаты (после настройки DNS A-записей)
sudo certbot --nginx -d launcher.samoy.love

# Установите nginx-конфиг из репозитория.
# ВАЖНО: наш конфиг — отдельный файл chillhub-launcher.conf, а не общий launcher.conf:
# на хосте живут и другие сайты, и общий файл релиз лаунчера затирал вместе с ними.
sudo install -m 0644 ~/Launcher-Project/deploy/launcher.conf /etc/nginx/sites-available/chillhub-launcher.conf
sudo ln -sf /etc/nginx/sites-available/chillhub-launcher.conf /etc/nginx/sites-enabled/chillhub-launcher.conf
sudo nginx -t && sudo systemctl reload nginx

# Firewall
sudo ufw allow OpenSSH && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp
sudo ufw deny 55700/tcp && sudo ufw deny 55777/tcp
sudo ufw enable
```

Примечание по обработке изображений:

- Для конвертации и сжатия анимированных изображений (GIF/WEBP → WEBP, ресайз до min‑side 1080) Admin‑сервер использует системную утилиту `ffmpeg` по пути из `PATH` или из переменной окружения `FFMPEG_PATH`.
- Если `ffmpeg` отсутствует, сервер сохранит исходный файл без конвертации (с сохранением оригинального расширения) и добавит отметку в логах. Статичные изображения (PNG/JPEG) обрабатываются встроенными средствами Go и не требуют внешних утилит.

[↑ к матрице](#карта-шагов-автодеплой-vs-вручную)

### Автодеплой (Makefile)
```bash
# Полный деплой ветки main (pull, build, статика, nginx reload, рестарт сервисов, автотесты)
make deploy BRANCH=main

# Деплой без пересборки Go‑бинариев (только статика/конфиги + автотесты)
make deploy-nobuild BRANCH=main
```

[↑ к матрице](#карта-шагов-автодеплой-vs-вручную)

Что делает автодеплой:
- Обновляет репозиторий (fetch/checkout/pull) для указанной ветки.
- Собирает `server/cmd/api` и `server/cmd/admin` (кроме `deploy-nobuild`).
- Устанавливает `deploy/launcher.conf` как `/etc/nginx/sites-available/chillhub-launcher.conf` (с бэкапом и откатом), валидирует `nginx -t`, делает reload.
- Раскладывает ТОЛЬКО статику:
  - `landing/` → `/var/www/site/` (с `--delete`).
  - `server/admin_ui/` → `/var/www/launcher/admin_ui/` (с `--delete`).
- НЕ трогает `/var/www/launcher/{manifests,content,news}` — они управляются через Admin UI или вручную.
- Перезапускает `chillhub-api.service`, `chillhub-admin.service`.
- Запускает автотесты (Admin UI, Admin API, Landing, статика, новости/ассеты); при сбоях печатает подробную диагностику (nginx/systemd/FS) и завершает с ошибкой.

Параметры деплоя (прокидываются в `scripts/deploy.sh`):
- `--admin-user <name>` — логин администратора.
- `--admin-pass <plain>` — пароль администратора в открытом виде; скрипт сгенерирует `ADMIN_PASSWORD_BCRYPT` через Go (bcrypt cost=12).
- `--admin-pass-bcrypt <hash>` — можно передать готовый bcrypt‑хэш вместо `--admin-pass`.
- `--jwt-secret <val>` — секрет для JWT. Если не указан — скрипт сгенерирует случайный base64 (48 байт) через `openssl`/`/dev/urandom`.
- `--cookie-domain <host>` — домен cookie (по умолчанию `launcher.samoy.love`).
- `--cookie-secure <true|false>` — флаг Secure для cookie (по умолчанию `true`).
- `--downloads-dir <path>` — внешняя директория установщиков (по умолчанию соседняя с `REPO_DIR`, т.е. `$(dirname REPO_DIR)/downloads`).
– Новые флаги проверки и UX:
  - `--site-base-url <url>` — базовый URL для HTTP‑тестов (по умолчанию `https://launcher.samoy.love`).
  - `--fail-on-mismatch` — прерывать деплой, если сравнение манифестов (`site/admin_ui/systemd/bin`) выявило несовпадения.
  - `--strict` — трактовать некоторые предупреждения как ошибки (напр., если `/admin/` вернул 200, а не 401/302).
  - `NO_COLOR=1` — отключить цветной вывод (по умолчанию цвета включены, если вывод в TTY).

Примеры:
```bash
# Минимальный прод‑деплой с авто‑секретом и генерацией bcrypt из пароля
make deploy BRANCH=main EXTRA_ARGS="--admin-user admin --admin-pass 'S0meStrongPass'"

# Явные секреты и внешняя папка установщиков
make deploy BRANCH=main EXTRA_ARGS="--jwt-secret 'base64...' --admin-user admin --admin-pass-bcrypt '$2a$12$...' --downloads-dir /home/ubuntu/installers"

# Строгая проверка целостности и URL для тестов
make deploy BRANCH=main EXTRA_ARGS="--site-base-url https://launcher.samoy.love --fail-on-mismatch --strict"
```

### Деплой с Windows (локально, через PowerShell)

Если вы на Windows и хотите обновить сервер без GitHub Actions, используйте скрипт `scripts/deploy-win.ps1` или цель Makefile `deploy-win`.

Требования на Windows: установлен Go (для сборки linux/amd64), OpenSSH клиент (`ssh`, `scp`). На сервере — `rsync`, `nginx`, `systemd`, `sudo`.

Примеры (PowerShell):

```powershell
# Минимально: билд, загрузка артефактов, выкладка и смоук‑тесты
./scripts/deploy-win.ps1 `
  -Host your.vps.host `
  -User ubuntu `
  -KeyPath "C:\Users\you\.ssh\id_rsa"

# С передачей секретов и логином админа (bcrypt предпочтительно)
./scripts/deploy-win.ps1 -Host your.vps.host -User ubuntu -KeyPath "C:\Users\you\.ssh\id_rsa" `
  -JwtSecret "base64-48bytes" -AdminUser admin -AdminPasswordBcrypt "$2y$12$..." `
  -CookieDomain "launcher.samoy.love" -CookieSecure "true"

# Если нужен plain‑пароль (bcrypt будет получен на сервере)
./scripts/deploy-win.ps1 -Host your.vps.host -User ubuntu -KeyPath "C:\Users\you\.ssh\id_rsa" `
  -AdminUser admin -AdminPasswordPlain "YourStrongPassword"
```

Примеры (через Make на Windows):

```bash
# Базовый вызов
make deploy-win HOST=your.vps.host USER=ubuntu KEY="C:/Users/you/.ssh/id_rsa"

# Со всеми параметрами
make deploy-win HOST=your.vps.host USER=ubuntu KEY="C:/Users/you/.ssh/id_rsa" \
  BRANCH=main JWT="base64-48bytes" ADMIN_USER=admin ADMIN_BCRYPT="$2y$12$..." \
  COOKIE_DOMAIN=launcher.samoy.love COOKIE_SECURE=true DOWNLOADS_DIR="C:/data/downloads"
```

Подробности по параметрам см. `scripts/README.md` (раздел `deploy-win.ps1`).

### Раскладка артефактов (ручная)
```bash
# Выполняйте на сервере (SSH), из домашней директории пользователя,
# где уже клонирован репозиторий в ~/Launcher-Project
cd ~/Launcher-Project

# 1) Лендинг → /var/www/site (копирование/синхронизация, без перемещения)
sudo rsync -a --delete ./landing/ /var/www/site/

# 2) Admin UI → /var/www/launcher/admin_ui (чистая статика)
sudo rsync -a --delete ./server/admin_ui/   /var/www/launcher/admin_ui/

# ВАЖНО: прод‑деплой НЕ трогает /var/www/launcher/{manifests,content,news}.
# Эти каталоги наполняются через Admin UI (загрузка ZIP/версий, новости и ассеты) или вручную при первичной инициализации.

# 2.1) Установщики: внешняя директория (рядом с репозиторием) → /var/www/site/downloads
# По умолчанию скрипт ожидает каталон downloads рядом с REPO_DIR; можно указать явный флагом --downloads-dir
sudo mkdir -p /var/www/site/downloads
sudo rsync -a ~/downloads/ /var/www/site/downloads/

# 3) Бинарии (сборка на сервере внутри модуля `server/` и установка)
cd ./server
go mod tidy
go build -o ../api   ./cmd/api
go build -o ../admin ./cmd/admin
cd -
sudo install -m 0755 ./api   /opt/chillhub/api
sudo install -m 0755 ./admin /opt/chillhub/admin
```

[↑ к матрице](#карта-шагов-автодеплой-vs-вручную)

### systemd (1 раз)
```bash
# Установите unit-файлы из репозитория
sudo install -m 0644 ~/Launcher-Project/deploy/systemd/chillhub-api.service   /etc/systemd/system/chillhub-api.service
sudo install -m 0644 ~/Launcher-Project/deploy/systemd/chillhub-admin.service /etc/systemd/system/chillhub-admin.service
sudo systemctl daemon-reload
sudo systemctl enable chillhub-api.service chillhub-admin.service
sudo systemctl restart chillhub-api.service chillhub-admin.service
```

### 4) Обновления
```bash
# После обновления бинариев
sudo systemctl restart chillhub-api.service chillhub-admin.service
# После обновления лендинга/конфигов nginx
sudo nginx -t && sudo systemctl reload nginx
```

[↑ к матрице](#карта-шагов-автодеплой-vs-вручную)

#### Частые ошибки (troubleshooting)

- **/admin отдаёт 404**
  - Убедитесь, что установлен актуальный конфиг nginx `deploy/launcher.conf` и перезагружен nginx.
  - В конфиге присутствует точное правило `location = /admin/ { root /var/www/launcher/admin_ui; try_files /admin.html =404; }`, которое отдаёт файл UI. Проксируется только `/admin/api/*`.

- **Лаунчер при старте пишет 404 на GET https://launcher.samoy.love/manifests/launcher/latest.json**
  - Клиент берёт `latest.json` по пути `manifests/launcher/latest.json` (см. `launcher/ChillHub/UpdateWindow.xaml.cs`). Если файла нет — будет 404.
  - Создайте его одним из способов:
    - Через Admin UI: вкладка «Лаунчер» → загрузите ZIP новой версии и оставьте флаг «Обновить latest». Это создаст `content/manifests/launcher/<version>.json` и `content/manifests/launcher/latest.json` и положит файлы в `content/content/launcher/<version>/files/`.
    - Вручную (для первичной инициализации):
      1) Скопируйте манифест версии в `/var/www/launcher/manifests/launcher/<version>.json`.
      2) Создайте `/var/www/launcher/manifests/launcher/latest.json` со структурой `{ "version": "<version>" }`.
      3) Убедитесь, что файлы самой версии лежат в `/var/www/launcher/content/launcher/<version>/files/`.
  - После выкладки манифестов перезагрузка nginx не требуется; проверьте по URL в браузере, что `latest.json` открывается без 404.
  - В прод‑скрипте деплоя `manifests/` не синхронизируется и не модифицируется — это сделано намеренно, чтобы не потерять данные.

- **/admin/api/* → 404**
  - Обновите/пересоберите `server/cmd/admin` и перезапустите `chillhub-admin.service`. Полный список маршрутов объявлен в `server/cmd/admin/routes.go`: канонический путь — `/admin/api/...`, форма `/admin/...` создаётся автоматически как алиас (кроме `auth/*` и `upload/*` — они существуют только под `/admin/api/...`).

- **Метрики в админке пусты**
  - Это ожидаемо: серверная часть (`POST /metrics/report`) готова, но лаунчер пока не отправляет события.


---

### Просмотр логов (systemd)

Сервисы устанавливаются как `chillhub-api.service` и `chillhub-admin.service` (см. `deploy/systemd/`). Для просмотра логов используйте `journalctl`:

```bash
# Текущий хвост логов API и следить в реальном времени
sudo journalctl -u chillhub-api.service -e -f

# Аналогично для Admin
sudo journalctl -u chillhub-admin.service -e -f

# Логи за последний час
sudo journalctl -u chillhub-api.service --since "1 hour ago"

# Логи за сегодня с уровнями
sudo journalctl -u chillhub-admin.service --since today -o short-iso

# Проверить статус и последние строки
systemctl status chillhub-api.service
systemctl status chillhub-admin.service

# Если нужно перезапустить
sudo systemctl restart chillhub-api.service
sudo systemctl restart chillhub-admin.service
```

[↑ к матрице](#карта-шагов-автодеплой-vs-вручную)

Подсказки:
- Если логов нет, убедитесь, что сервисы активированы и запущены: `sudo systemctl enable --now chillhub-api.service chillhub-admin.service`.
- Для фильтрации по тексту: `journalctl -u chillhub-api.service | grep ERROR`.
- Конфигурации unit-файлов лежат в `deploy/systemd/`; при изменении не забудьте `sudo systemctl daemon-reload`.

---

## Полезные ссылки и файлы
- Конфиг nginx (prod): `deploy/launcher.conf` → на сервере `/etc/nginx/sites-available/chillhub-launcher.conf`
- Проверка конфига nginx настоящим `nginx -t` в Docker: `sh deploy/nginx-check.sh` (по умолчанию образ версии прода `nginx:1.24-alpine`; `NGINX_IMAGE=nginx:alpine` — проверка «на будущее»). Подробности — `deploy/README.md`.
- Systemd юниты: `deploy/systemd/`
- Документация скриптов: `scripts/README.md`
- CI/CD (ручной запуск из GitHub Actions): `.github/workflows/deploy.yml`

### CI/CD: ручной запуск в GitHub Actions

Workflow `.github/workflows/deploy.yml` можно запустить вручную (Run workflow) с параметрами:

- `environment` — окружение (`prod` по умолчанию; можно указать `stage`).
- `site_base_url` — базовый URL для HTTP‑проверок (по умолчанию `https://launcher.samoy.love`).
- `fail_on_mismatch` — падать при любых несовпадениях манифестов (рекомендуется `true`).
- `strict` — делать часть предупреждений фатальными (например, 200 на `/admin/`).
- `reason` — причина деплоя (опционально).

Особенности:

- Сборка бинарей выполняется сразу для `linux/amd64` и `linux/arm64`; на сервере выбирается корректная архитектура по `uname -m` и проверяется через `file`.
- Перед загрузкой на сервер генерируются манифесты `build/*.manifest` (для `site`, `admin_ui`, `bin`, `systemd`). Они также загружаются в job‑артефакты `deploy-manifests` для диагностики.
- На сервере выполняется сравнение манифестов с развёрнутыми файлами; при `fail_on_mismatch=true` job завершается ошибкой.
- Логи сгруппированы с помощью `::group::…` (Build/Bundle/Manifests/Rsync/Verification/Tests), чтобы ускорить навигацию.


## Примечания по безопасности и качеству
- Серверные порты приложений 55700/55777 закрыты внешнему миру, доступны только через nginx.
- Ни манифесты, ни исполняемые файлы не подписываются: подпись из проекта убрана. Подлинность раздачи держится на TLS, а SmartScreen предупреждает при установке, пока не наберётся репутация загрузок.
- Каждая запись манифеста обязана нести хотя бы один хеш (Blake3 и/или SHA‑256): запись без хешей означала бы установку файла вообще без проверки целостности, поэтому такой манифест отвергается.
- Пользовательские данные лаунчера (`config.json`, логи) лежат в `%APPDATA%\ChillHub`, а НЕ в каталоге установки `%LOCALAPPDATA%\ChillHub`. При обновлении со старой версии конфиг мигрирует автоматически.
- В ZIP лаунчера не должно быть `config.json` и `launcher.version`: пересечение манифеста с preserve‑правилами апдейтера даёт бесконечный цикл самообновления. Проверка — `dotnet run --project updater/tests/ManifestPreserveCheck`.
