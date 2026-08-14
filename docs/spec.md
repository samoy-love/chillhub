# Техническое задание (ТЗ) — ChillHub (MVP)

Версия документа: 1.3
Дата: 2026-08-01

## Оглавление
- [1. Цели и общая концепция](#1-цели-и-общая-концепция)
- [2. Область MVP](#2-область-mvp)
- [3. Архитектура и структура проекта](#3-архитектура-и-структура-проекта)
  - [3.1. Карта консистентности путей (Path Consistency)](#31-карта-консистентности-путей-path-consistency)
  - [3.2. Разбиение серверного кода по пакетам](#32-разбиение-серверного-кода-по-пакетам)
- [4. Пути установки по умолчанию](#4-пути-установки-по-умолчанию)
- [5. Безопасность и целостность](#5-безопасность-и-целостность)
- [6. Форматы данных](#6-форматы-данных)
  - [6.1. Манифест версии](#61-манифест-версии)
  - [6.2. Реестр игр (динамический)](#62-реестр-игр-динамический)
  - [6.3. Новости](#63-новости)
- [7. Public API (server/cmd/api)](#7-public-api-servercmdapi)
- [8. Admin UI/API (server/cmd/admin + server/admin_ui)](#8-admin-uiapi-servercmdadmin--serveradmin_ui)
  - [8.1. Игры](#81-игры)
  - [8.2. Лаунчер](#82-лаунчер)
  - [8.3. Новости](#83-новости)
  - [8.4. Admin login](#84-admin-login)
  - [8.5. Режим технических работ](#85-режим-технических-работ)
  - [8.6. Метрики лаунчера](#86-метрики-лаунчера)
  - [8.7. Обратная связь (Feedback)](#87-обратная-связь-feedback)
- [9. Клиент (launcher/)](#9-клиент-launcher)
  - [9.1. Данные клиента: конфиг и логи](#91-данные-клиента-конфиг-и-логи)
  - [9.2. Проверка целостности игры](#92-проверка-целостности-игры)
  - [9.3. Страница игры и откат на другую версию сборки](#93-страница-игры-и-откат-на-другую-версию-сборки)
  - [9.4. Режим технических работ в клиенте](#94-режим-технических-работ-в-клиенте)
- [10. Содержимое контента (content/)](#10-содержимое-контента-content)
- [11. Инсталлятор (NSIS)](#11-инсталлятор-nsis)
- [12. Nginx (общее)](#12-nginx-общее)
- [13. Нефункциональные требования](#13-нефункциональные-требования)
- [14. Ограничения и допущения (MVP)](#14-ограничения-и-допущения-mvp)
- [15. Сценарии и флоу](#15-сценарии-и-флоу)
- [16. Требования к качеству и тестированию](#16-требования-к-качеству-и-тестированию)
- [17. План расширений (после MVP)](#17-план-расширений-после-mvp)
- [18. Ссылки на ключевые файлы](#18-ссылки-на-ключевые-файлы)
- [19. Домены и DNS](#19-домены-и-dns)
- [20. CI/CD (GitHub Actions)](#20-cicd-github-actions)
  - [20.1. Требование паритета с scripts/deploy.sh](#201-требование-паритета-с-scriptsdeploysh)
  - [20.2. Паритет и различия: GitHub Actions vs scripts/deploy.sh](#202-паритет-и-различия-github-actions-vs-scriptsdeploysh)
- [21. Деплой на сервер (ручной)](#21-деплой-на-сервер-ручной)
- [22. Локальная разработка и автотесты деплоя](#22-локальная-разработка-и-автотесты-деплоя)
- [23. Безопасность секретов](#23-безопасность-секретов)
- [24. Выявленные проблемы и рекомендации](#24-выявленные-проблемы-и-рекомендации)
- [25. Дорожная карта доработок (этапы)](#25-дорожная-карта-доработок-этапы)
- [27. План снижения ложных срабатываний антивирусов/Windows Defender](#27-план-снижения-ложных-срабатываний-антивирусовwindows-defender-launcher-updater-installer)
  - [27.1. Метаданные и репутация](#271-метаданные-и-репутация)
  - [27.2. Поведение Updater (безопасные паттерны)](#272-поведение-updater-безопасные-паттерны)
  - [27.3. Инсталлятор (NSIS)](#273-инсталлятор-nsis)
  - [27.4. Сетевое взаимодействие](#274-сетевое-взаимодействие)
  - [27.5. Содержимое и упаковка](#275-содержимое-и-упаковка)
  - [27.6. CI/Проверки](#276-cipроверки)
  - [27.7. Отчётность и апелляции](#277-отчётность-и-апелляции)
  - [27.8. Кодовые практики (клиент/апдейтер)](#278-кодовые-практики-клиентапдейтер)
  - [27.9. Распределение](#279-распределение)
  - [27.10. Интеграция в дорожную карту (приоритетизация)](#2710-интеграция-в-дорожную-карту-приоритетизация)

Примечание о плейсхолдерах: в этом документе используется `<SERVER_IP>` как обозначение публичного IP VPS. Замените на фактический адрес при настройке DNS и серверов. Для единообразия используйте один и тот же IP по всему документу и в конфигурации.

## 1. Цели и общая концепция
- Лаунчер для Windows 10–11, умеющий:
  - Автоматически обновляться (self-update).
  - Устанавливать/обновлять игры из заданного источника, обеспечивая точную репликацию серверной версии папки игры у пользователя.
  - Показывать новости (лаунчера и игр) с обложками и содержимым (Markdown).
- Серверная часть на Golang, раздача через nginx. Предоставляет Public API и Admin API.
- Админ-панель (web) для Управления:
  - Списком игр (динамический реестр игр, редактируемый в UI, хранится в `content/manifests/_registry/games.json`).
  - Версиями (загрузка ZIP, активация `latest`).
  - Новости (редактирование Markdown, загрузка/конвертация ассетов, публикация, предпросмотр).

### 1.1. Почему ChillHub

ChillHub избавляет от рутинной и хрупкой ручной установки из архивов и ускоряет релизы модпаков.

- Автообновления по диффу: загружаются только изменения; локальная папка становится эталонной копией серверной версии.
- Консистентность: лишние файлы удаляются, пустые каталоги создаются по манифесту, хеш‑проверки (Blake3/SHA‑256).
- Быстро: многопоточность, HTTP Range, возобновление `.part`.
- Простой флоу: «Обновить → Играть», без ручных распаковок и конфликтов модов.
- Админ‑релизы в один шаг: загрузка ZIP, публикация `latest` в UI.
- Коммуникация: новости/гайды с обложками прямо в клиенте.

Основано на текущем репозитории и уточненных требованиях пользователя.

## 2. Область MVP
- Только серверные сборки (без локальных пользовательских). [Требование]
- Клиентские логи не требуются (только серверные). [Требование]
- Обновление игр скачивает только дифф: добавляет/обновляет измененные файлы, удаляет отсутствующие на сервере и создает пустые каталоги из манифеста. [Требование]
- Проверка свободного места — только под рассчитанный дифф без запаса. [Требование]
- Установщик NSIS (per-user). [Требование]
- Динамический список игр — управление из админки, используется публичным API (без хардкода). [Требование]

## 3. Архитектура и структура проекта
См. `README.md`:
- `server/` — Golang:
  - Public API (`/api/*`) + статика `/manifests/*`, `/content/*`, `/news/*` для dev — `server/cmd/api`.
  - Admin API (`/admin/api/*`, с алиасами `/admin/*`) и статика админки `server/admin_ui/*` — `server/cmd/admin`.
- `launcher/` — C# WPF лаунчер.
- `updater/` — отдельный exe самообновления (`YourLauncher.Updater`) и общие preserve-правила (`updater/UpdatePreserve.cs`).
- `landing/` — статический лендинг (отдается по корню `https://launcher.samoy.love/`).
- `deploy/` — конфиги деплоя: `deploy/launcher.conf` (nginx; на сервере кладётся как `chillhub-launcher.conf`), `deploy/nginx-check.sh`, systemd unit-файлы.
- `content/` — статика (манифесты, бинарное содержимое версий, новости и ассеты, состояние режима техработ, метрики, инбокс обратной связи).
- `scripts/` — скрипты сборки/публикации/деплоя (`run-dev.ps1`, `deploy.sh`, `deploy-win.ps1`, `installer.nsi` и др.).

### 3.1. Карта консистентности путей (Path Consistency)

- Инсталлятор (NSIS)
  - Бинарии лаунчера: `launcher/ChillHub/bin/<Config>/net8.0-windows/*` → инсталлируются в `%LOCALAPPDATA%/ChillHub/` (см. `scripts/installer.nsi`).
  - Ярлыки: меню/рабочий стол.

- Клиент / Обновление
  - Манифест лаунчера: `{ApiBase}/manifests/launcher/latest.json` → `{ApiBase}/manifests/launcher/{version}.json`.
  - Контент лаунчера/игр: `{ApiBase}/content/{gameId}/{version}/files/...` (собирается из манифеста).
  - Локальная папка игр по умолчанию: `D:\Games\ChillHub` (или `C:\Games\ChillHub`).
  - Пользовательские данные клиента: `%APPDATA%\ChillHub\` — `config.json`, `logs\client*.log`, очередь обратной связи.
    Каталог установки (`%LOCALAPPDATA%\ChillHub`) содержит только файлы сборки; см. [9.1](#91-данные-клиента-конфиг-и-логи).

- Public API (`server/cmd/api`)
  - `/api/*` (проксируется nginx), dev‑статика: `/manifests/*`, `/content/*`, `/news/*`, `/assets/*` → `content/` каталоги.

- Admin API/UI (`server/cmd/admin`, `server/admin_ui`)
  - Prod: `/admin/ui/*` (статика), `/admin/` (admin.html), `/admin/api/*` (backend, защищён через `auth_request`).
  - Dev: `http://localhost:55777/admin` (бэкенд + выдача статики для удобства).

- Nginx (`deploy/launcher.conf` → на сервере `/etc/nginx/sites-available/chillhub-launcher.conf`)
  - root `/var/www/site` → лендинг.
  - alias `/var/www/launcher/{content,manifests,news,admin_ui}`.
  - fallback `/assets/*` → сначала лендинг `/var/www/site/assets/`, затем `@news_assets_fallback` → `/var/www/launcher/news/assets/`.

Домены и маршрутизация (prod):
- `launcher.samoy.love` — боевой домен лаунчера:
  - `/` — лендинг из `landing/`.
  - `/api/*` — прокси на Public API (`127.0.0.1:55700`).
  - `/admin/api/*` — прокси на Admin API (`127.0.0.1:55777`) 1:1 (без переписывания пути).
  - `/admin/` и `/admin/ui/*` — статика админки из `/var/www/launcher/admin_ui/` (точный матч для `/admin/` отдает `admin.html`).
  - `/feedback/submit` и `/metrics/report` — публичные (без авторизации) POST‑эндпоинты, проксируются на Admin API (`127.0.0.1:55777`); объявлены в конфиге ДО защищённого `location /admin/api/`.
  - `/content/*`, `/manifests/*`, `/news/*` — статика контента (`/var/www/launcher/...`).
  - `/assets/*` — «комбинированные ассеты»: сперва лендинг (`/var/www/site/assets`), затем fallback на новости (`/var/www/launcher/news/assets`).
- `samoy.love` — плейсхолдер/заглушка (простая страница) — включается при необходимости.
- Другие проекты на том же хосте (например `metro.samoy.love`) живут в собственных файлах `sites-available` — наш конфиг их не описывает и деплой их не перезаписывает.

Порты (dev):
- Public API: `:55700` (`server/cmd/api`)
- Admin API/UI: `:55777` (`server/cmd/admin`)

### 3.2. Разбиение серверного кода по пакетам

`server/cmd/admin` — это только сборка роутера и middleware; обработчики живут в
`server/internal/*`. Прежний монолитный `server/cmd/admin/main.go` распилен:

| Пакет | Ответственность |
|---|---|
| `server/cmd/admin/main.go` | конфигурация процесса, middleware-цепочка, rate‑лимиты публичных эндпоинтов, старт HTTP-сервера |
| `server/cmd/admin/routes.go` | единый список эндпоинтов (`apiRoutes()`), автогенерация алиасов `/admin/api/... → /admin/...` |
| `server/internal/adminapi/auth` | вход/выход/refresh/me/verify, cookies, CSRF |
| `server/internal/adminapi/builds` | загрузка ZIP (обычная, потоковая, чанковая), распаковка, манифесты, `latest`, проверка путей (`paths.go`) |
| `server/internal/adminapi/news` | новости, индексы, Markdown-предпросмотр, ассеты |
| `server/internal/adminapi/feedback` | инбокс обратной связи + публичный приём обращений |
| `server/internal/adminapi/games` | реестр игр, иконки, скан папок манифестов |
| `server/internal/adminapi/media` | обработка изображений (ресайз/конвертация, ffmpeg) |
| `server/internal/adminutil` | общие примитивы: `RequireMethod`, `WriteJSON`, `DetectContentRoot` и защита от path traversal (`IsSafeGameID`, `IsSafeVersion`, `IsSafeNewsSlug`, `NewsSlugPath`, `EnsureWithin`, `SanitizeFilename`, `SanitizeAssetPath`) |
| `server/internal/httpx` | RequestID, CORS, логирование, `NoStore` |
| `server/internal/maintenance` | режим технических работ (публичный и админские эндпоинты) |
| `server/internal/metrics` | приём и агрегация метрик лаунчера |
| `server/internal/ratelimit` | лимитер по адресу клиента (`ClientIP` учитывает `X-Forwarded-For`/`X-Real-IP`) |

Два следствия, важных при чтении кода:

- Маршруты объявляются один раз в `apiRoutes()` под каноническим путём
  `/admin/api/...`; форма `/admin/...` создаётся автоматически (`aliasOf`). Эндпоинты
  с `noAlias: true` (вся `auth/*`, поддерево `upload/*`) существуют только под
  `/admin/api/...`.
- Пакет `maintenance` подключён к обоим бинарям: публичный `GET /api/maintenance`
  регистрируется в `server/cmd/api`, запись состояния — в `server/cmd/admin`.

## 4. Пути установки по умолчанию
- Лаунчер (каталог установки): `%LOCALAPPDATA%\ChillHub\` (`scripts/installer.nsi` — `INSTALL_DIR`). Здесь лежат только файлы сборки.
- Игры: `D:\Games\ChillHub\` (если диска `D:` нет — `C:\Games\ChillHub\`).
- Пользовательские данные клиента: `%APPDATA%\ChillHub\` (`config.json`, `logs\`). Подробности и миграция — [9.1](#91-данные-клиента-конфиг-и-логи).
- Локальный bcrypt‑хэш пароля админки для dev‑запуска: `%LOCALAPPDATA%\ChillHub\admin.secret.json` (создаётся `scripts/run-dev.ps1`, к каталогу установки отношения не имеет).

## 5. Безопасность и целостность
- Целостность файлов обеспечивается хешами (Blake3 — основной, SHA-256 — опционально). Клиент проверяет доступные хеши. На стороне админ‑сервера оба хеша считаются при загрузке ZIP за один проход по файлу.
- Криптографической подписи манифестов в проекте нет: подлинность раздачи обеспечивается только TLS. Соответственно, тот, кто получил доступ на запись в каталог контента, может подменить и файлы, и манифест.
- Структура манифеста проверяется до загрузки, независимо от источника (`ManifestValidator` на клиенте, `validateManifest` на сервере): отвергаются выход за пределы каталога (`..`, абсолютные пути, UNC), неканонические формы, дубликаты путей и записи без хешей. Это защита не от подмены, а от произвольной записи на диск.
- Клиент не должен определяться как вирус:
  - Использовать стандартные механики самообновления (robocopy, temp + перезапуск), без подозрительных техник.

## 6. Форматы данных
### 6.1. Манифест версии
Генерируется сервером Админки при загрузке ZIP и хранится в `content/manifests/{gameId}/{version}.json` (для лаунчера: `gameId = launcher`). Хеши (SHA-256 и Blake3) считаются на сервере.

Структура (единая, подтверждена в `server/internal/adminapi/builds/builds.go`, тип `manifest`):
```json
{
  "version": "1.0.0",
  "buildId": "1.0.0",
  "gameId": "lethal-company",
  "createdAt": "2025-09-15T12:34:56Z",
  "files": [
    {
      "path": "Bin/Game.exe",
      "size": 12345678,
      "blake3": "<hex>",
      "sha256": "<hex>",
      "executable": true
    }
  ],
  "emptyDirs": ["Saves", "Cache"],
}
```
Дополнительно: `content/manifests/{gameId}/latest.json` содержит `{ "version": "1.x.y" }`.

Примечания:
- Админ-сервер при загрузке ZIP считает оба хеша (SHA-256 и Blake3) за один проход по файлу.
- Поля `signature` больше нет: подпись манифестов из проекта убрана. В манифестах, выпущенных раньше, оно ещё встречается и игнорируется обеими сторонами.
- В манифест лаунчера (`gameId = launcher`) НЕ должны попадать пользовательские файлы (`config.json`, `launcher.version`) — иначе возникает петля самообновления. Проверка: [22](#22-локальная-разработка-и-автотесты-деплоя).

### 6.2. Реестр игр (динамический)
Хранится в `content/manifests/_registry/games.json`, управляется из админки.
```json
{
  "items": [
    {
      "gameId": "lethal-company",
      "title": "Lethal Company",
      "exeRelativePath": "PEAK.exe",
      "iconUrl": "/manifests/lethal-company/icon.png"
    }
  ]
}
```
Public API использует этот список (если существует), иначе сканирует директорию `content/manifests/` (кроме служебных папок) — см. `server/cmd/api/main.go` (`loadGamesFromRegistry`, `loadGamesByScanning`).

### 6.3. Новости
- Индексы:
  - Лаунчер: `content/news/index.json`
  - По игре: `content/news/games/{gameId}/index.json`
- Элемент индекса:
```json
{
  "id": "news-1",
  "title": "Заголовок",
  "slug": "news-1",
  "createdAt": "2025-09-15T11:58:41Z",
  "summary": "Краткое описание...",
  "coverUrl": "/assets/cover.jpg",
  "published": true
}
```
- Контент новости: Markdown-файл по slug:
  - Лаунчер: `content/news/{slug}.md`
  - Игра: `content/news/games/{gameId}/{slug}.md`
- Ассеты новостей: `content/news/assets/` (+ подпапки).

## 7. Public API (server/cmd/api)
Ниже формальная спецификация публичных эндпоинтов. Базовый URL в dev: `http://localhost:55700`. В prod: `https://launcher.samoy.love`.

<a id="api-get-games"></a>
- `GET /api/games`
  - Описание: Список игр.
  - Ответ 200: `{ "items": GameInfo[] }`
  - Модель `GameInfo` (`server/cmd/api/main.go`):
    - `gameId: string`, `title: string`, `hasLatest: bool`, `latestVersion?: string`, `manifestUrl?: string`, `exeRelativePath?: string`, `iconUrl?: string`.

<a id="api-get-game-by-id"></a>
- `GET /api/games/{gameId}`
  - Описание: Информация по конкретной игре (включая latest, если есть).
  - Ответ 200: `GameInfo` (см. выше) или 404.

<a id="api-get-latest"></a>
- `GET /api/games/{gameId}/versions/latest`
  - Описание: Метаданные latest-версии.
  - Ответ 200: `{ gameId: string, version?: string, manifestUrl?: string, hasLatest?: bool }`.

<a id="api-get-builds"></a>
- `GET /api/games/{gameId}/builds`
  - Описание: Список доступных версий по файлам в `content/manifests/{gameId}/*.json` (кроме `latest.json`).
  - Ответ 200: `{ gameId: string, items: string[] }`.

<a id="api-maintenance"></a>
- `GET /api/maintenance` (также `HEAD`)
  - Описание: состояние режима технических работ. Опрашивается каждым лаунчером при старте и далее раз в 60 секунд.
  - **Ответ всегда 200** — «режим выключен» это нормальный ответ, а не 404. Отсутствующий или битый файл состояния трактуется как «выключен».
  - Окно активности вычисляет СЕРВЕР: клиенту не нужно сравнивать дедлайн со своими часами. Если окно ещё не началось или уже истекло, эндпоинт отдаёт `enabled: false` без вмешательства администратора (автосброс).
  - Ответ 200:
    ```json
    {
      "enabled": true,
      "reason": "Обновляем раздачу",
      "startsAt": "2026-08-01T20:00:00Z",
      "endsAt": "2026-08-01T22:00:00Z",
      "blocks": { "install": true, "update": true, "launch": false },
      "serverTime": "2026-08-01T20:13:07Z"
    }
    ```
  - Поля: `enabled` — режим действует прямо сейчас; `reason` — текст для баннера (обрезается сервером до 500 байт при записи); `startsAt`/`endsAt` — RFC 3339 в UTC, возвращаются только пока окно активно, пустые значения означают «с этого момента» / «до отмены»; `blocks` — три независимых запрета (`install` — установка отсутствующей игры, `update` — обновление установленной, `launch` — запуск); `serverTime` — время сервера, чтобы клиент с уехавшими часами правильно показал обратный отсчёт. Поле `updatedBy` наружу не отдаётся.
  - Реализация: `server/internal/maintenance/maintenance.go` (`PublicHandler`, `Effective`). Состояние кэшируется в памяти и перечитывается только при изменении mtime/размера файла, поэтому опрос стоит один `os.Stat`.

<a id="api-news-index"></a>
- `GET /news/index.json`
  - Описание: Индекс новостей лаунчера. Сервер фильтрует элементы по `published=true` (если поле отсутствует — пропускает).
  - Ответ 200: `{ items: any[] }` (схема элементов см. ниже).

<a id="api-news-game-index"></a>
- `GET /news/games/{gameId}/index.json`
  - Описание: Индекс новостей по игре c такой же фильтрацией.
  - Ответ 200: `{ items: any[] }`.

Dev-статика (только в локальном запуске `api`):
- `/manifests/*` → `content/manifests/*`
- `/content/*` → `content/content/*`
- `/news/*` → `content/news/*`
- `/assets/*` → `content/news/assets/*`

Примечание: Public API формирует список игр из реестра `content/manifests/_registry/games.json` (если есть), иначе сканирует папку `content/manifests/` за исключением `_registry`. См. `loadGamesFromRegistry()` и `loadGamesByScanning()` в `server/cmd/api/main.go`.
В продакшене каталоги `/var/www/launcher/{manifests,content,news}` наполняются через Admin UI либо вручную; деплой-скрипт их не изменяет.

Примечание: Базовый URL строится с учетом `X-Forwarded-Proto` (http/https). См. `baseURL()`.

Публичные POST‑эндпоинты, которые обслуживает процесс **admin** (`:55777`), а не `cmd/api`, — nginx проксирует их отдельными `location`, объявленными до защищённого `/admin/api/`:
- `POST /feedback/submit` — приём обращений из лаунчера, см. [8.7](#87-обратная-связь-feedback).
- `POST /metrics/report` — приём событий метрик, см. [8.6](#86-метрики-лаунчера).

Rate limit Public API (`server/cmd/api/main.go`): по умолчанию **600 запросов в минуту** на адрес клиента; настраивается переменными `API_RATE_LIMIT` и `API_RATE_WINDOW` (значение лимита `0` или отрицательное — лимит выключен). Лимитер навешен только на JSON‑эндпоинты (`/api/*`, `/news/*.json`): dev‑раздача `/content/` и `/manifests/` под ним не ходит, иначе установка игры (тысячи файлов в 16 потоков) упиралась бы в лимит.

[↑ к оглавлению](#оглавление) • [↑ наверх](#техническое-задание-тз--chillhub-mvp)

## 8. Admin UI/API (server/cmd/admin + server/admin_ui)
Админ-панель доступна на `:55777/admin` (dev). Ниже функциональность по разделам и спецификация API.

**Канонический префикс — `/admin/api/...`.** Полный список эндпоинтов объявлен один
раз в `server/cmd/admin/routes.go` (`apiRoutes()`); форма `/admin/...` регистрируется
автоматически как алиас (`aliasOf`). Исключения (`noAlias: true`), существующие
ТОЛЬКО под `/admin/api/...`: все `auth/*` и всё поддерево чанковой загрузки
`upload/*`. Ниже пути приводятся в канонической форме.

### 8.1. Игры
- Загрузка версии (ZIP):
  - `POST /admin/api/upload` — простая multipart‑загрузка (обратная совместимость; UI её не использует).
  - `POST /admin/api/uploadStream` — потоковый режим с прогрессом (NDJSON).
    - FormData: `kind=game|launcher`, `gameId?` (для `game`), `version`, `zip`, `updateLatest=0|1`.
    - Ответ: последовательность NDJSON событий: `start`, `zipSaved`, множество `unzip`, затем `composeStart`, множество `file`, `done` или `error`.
  - Чанковая загрузка (используется UI для многогигабайтных сборок; только `/admin/api/...`):
    `POST /admin/api/upload/init`, `POST /admin/api/upload/chunk`, `GET /admin/api/upload/status`,
    `POST /admin/api/upload/complete`, `POST /admin/api/upload/process` (NDJSON‑распаковка),
    `POST /admin/api/upload/cleanup`. Незавершённые загрузки подчищает фоновый janitor
    (`StartUploadJanitor`).
  - Публикация атомарна: ZIP распаковывается во временный каталог‑стейдж на том же томе
    (`stageVersionDir`), и только после полной распаковки и подсчёта хешей каталог версии
    подменяется одним `rename`. Обрыв загрузки не оставляет битую версию на раздаче.
  - Манифест, записанный по итогам загрузки, проходит структурную проверку (`validateManifest`); при нарушении публикация отклоняется с ошибкой.
- Список версий: `GET /admin/api/list?gameId={id}` → `{ items: [{version}], latest }`.
- Активация latest: `POST /admin/api/activate?gameId={id}&version={v}` (создает/обновляет `latest.json`).
- Удаление версии: `POST /admin/api/deleteVersion?gameId={id}&version={v}` (удаляет манифест и папку `content/{gameId}/{version}`; корректирует `latest.json`).
- Свободное место на диске сервера: `GET /admin/api/system/free`.
- Подсказка следующей версии: (убрана). Версию вводит администратор вручную в UI.
- Сборка манифеста из уже разложенных файлов (compose) — убрана в пользу загрузки ZIP.
- Предпросмотр файлов версии в UI: чтение `GET /manifests/{gameId}/{version}.json` и древовидный показ.
- Редактор реестра игр (динамический список):
  - `GET /admin/api/games` — читает/создает `content/manifests/_registry/games.json`.
  - `POST /admin/api/games/save` — сохраняет `{ items: [{ gameId, title, exeRelativePath, iconUrl, order, pinned, unpublished }] }`.
  - `GET /admin/api/games/scan` — ищет новые игры по папкам манифестов (исключая `_registry`, `launcher`, `repo`).
  - `POST /admin/api/games/purge` (форма: `gameId`) — удаляет игру целиком: запись реестра, `content/manifests/{gameId}/` и все распакованные сборки `content/content/{gameId}/`. Необратимо.
  - Загрузка иконки игры: `POST /admin/api/games/icon/upload` (multipart: `gameId`, `file`) → сохраняет `content/manifests/{gameId}/icon.png` и возвращает URL.
  - `unpublished: true` скрывает игру из публичного `GET /api/games`, оставляя запись и файлы на месте. Значение по умолчанию — `false`: реестры, записанные до появления флага, продолжают публиковаться.

Галерея игры (`server/internal/adminapi/gamegallery`, вкладка «Галерея» в карточке игры):
- Картинки лежат в `content/content/{gameId}/gallery/`, порядок, подписи и обложка — в `gallery.json` рядом с ними.
- **Размер обложки — 1920 × 620**, как у Steam Library Hero: витрина лаунчера считает свою высоту из ширины по этой же пропорции. Годится и удвоенный 3840 × 1240. На SteamGridDB это раздел Heroes, картинка оттуда подходит без обработки. На широком окне высота упирается в потолок, и обложка кадрируется по центру — как это делает и Steam.
- `POST /admin/api/games/gallery/setCover` (форма: `gameId`, `file`) — ставит обложку **и регистрирует файл в `items`**: лаунчер строит витрину по `items`, и обложка, которой там нет, до него не доезжает.
- `POST /admin/api/games/gallery/setCaption` (форма: `gameId`, `file`, `caption`) — подпись; отсутствующий файл дописывается в конец `items`.
- `file` — путь относительно корня галереи, подпапки разрешены (`shots/moon.png`).
- `delete` и `rename` правят `gallery.json` вместе с диском: удалённая картинка уходит из `items`, а обложка, указывавшая на неё, сбрасывается.

Аутентификация (cookie + JWT, `server/internal/adminapi/auth/auth.go`):
- `POST /admin/api/auth/login` — тело: `{ username, password }` или `application/x-www-form-urlencoded`.
  - Устанавливает cookies: `access_token` (HttpOnly), `refresh_token` (HttpOnly), `csrf_token` (не HttpOnly).
- `POST /admin/api/auth/logout` — очистка cookies.
- `POST /admin/api/auth/refresh` — обновление access/refresh + CSRF.
- `GET /admin/api/auth/me` — `{ user }` при валидной сессии.
- `GET /admin/api/auth/verify` — 200/401 для `auth_request` в nginx.

CSRF: для методов `POST/PUT/PATCH/DELETE` требуется заголовок `X-CSRF-Token` с тем же значением, что в cookie `csrf_token`.

### 8.2. Лаунчер
- Просмотр текущего манифеста лаунчера: UI читает `GET /manifests/launcher/latest.json` и затем `GET /manifests/launcher/{version}.json`.
- Загрузка версии лаунчера (ZIP): те же эндпоинты, что и для игр, с `kind=launcher` (`POST /admin/api/uploadStream` или чанковая загрузка).
- ВАЖНО: в ZIP лаунчера не должно быть `config.json` и `launcher.version` — см. [22](#22-локальная-разработка-и-автотесты-деплоя).

### 8.3. Новости
- Индекс по области: `GET /admin/api/news/list?scope=launcher|game&gameId={optional}`.
- Получить новость: `GET /admin/api/news/get?scope=...&gameId=...&slug=...` → `{ markdown, published, coverUrl }`.
- Сохранить: `POST /admin/api/news/save` (multipart: `scope, gameId?, slug, markdown, coverUrl?, published?`) — сохраняет, обновляет meta и пересобирает индекс.
- Удалить: `DELETE /admin/api/news/delete?scope=...&gameId=...&slug=...`.
- Публикация: `POST /admin/api/news/publish` (`scope, gameId?, slug, published=true|false`).
- Предпросмотр: `POST /admin/api/news/preview` — `{ listHtml, contentHtml }`.
- Пересборка индекса: `POST /admin/api/news/rebuild?scope=...&gameId=...`.
- Загрузка обложки: `POST /admin/api/news/uploadCover`.
- Ассеты: базовая директория `content/news/assets`.
  - Листинг: `GET /admin/api/news/assets?path={rel}&q={opt}&dirsOnly={0|1}`.
  - Создать папку: `POST /admin/api/news/assets/mkdir`.
  - Переименовать: `POST /admin/api/news/assets/rename`.
  - Удалить: `POST /admin/api/news/assets/delete`.
  - Загрузка файла: `POST /admin/api/news/assets/upload` (конвертация изображений, ресайз до 1080 минимальной стороны; GIF/WEBP → WEBP при наличии ffmpeg).
  - Загрузка по URL: `POST /admin/api/news/assets/uploadByUrl` (та же обработка, имя подбирается безопасно).
- Все параметры `gameId`/`slug`/`path` проверяются общими примитивами `adminutil` (`IsSafeGameID`, `IsSafeNewsSlug`, `EnsureWithin`, `SanitizeAssetPath`); выход за пределы `content/` невозможен.

- Здоровье сервиса: `GET /admin/api/health` → `ok` (в nginx вынесен в отдельный `location` до `auth_request`, поэтому доступен без авторизации). Алиас `/admin/health` также зарегистрирован.

UI админки: `server/admin_ui/admin.html` + `admin.js` (Bootstrap 5, темная тема). Кнопки «Собрать» и «Предложить следующую версию» удалены; используется загрузка ZIP и просмотр списков версий.

Сервер админки для dev также отдает статику, чтобы UI работал без nginx:
- `/manifests/*` → `content/manifests/*`
- `/news/*` → `content/news/*`
- `/assets/*` → `content/news/assets/*`

### 8.4. Admin login
- Первый запуск (локально): при старте `scripts/run-dev.ps1` скрипт запросит пароль для пользователя `admin` и сохранит bcrypt‑хэш вне репозитория — в `%LOCALAPPDATA%/ChillHub/admin.secret.json`. На следующих запусках используется сохранённый хэш.
- Сервер (деплой): `scripts/deploy.sh` при отсутствии пароля запросит ввод (скрытый), вычислит bcrypt и сохранит в `/etc/chillhub/admin.env` (используется через systemd `EnvironmentFile`).
- Ротация:
  - Локально — удалить `%LOCALAPPDATA%/ChillHub/admin.secret.json` и перезапустить `run-dev.ps1` (будет повторный запрос), либо заранее проставить переменную `ADMIN_PASSWORD_BCRYPT`.
  - Сервер — отредактировать `/etc/chillhub/admin.env` (ключи `ADMIN_USERNAME=admin`, `ADMIN_PASSWORD_BCRYPT=<hash>`), затем `sudo systemctl daemon-reload && sudo systemctl restart chillhub-admin`.
- Override/окружение:
  - Локально можно задать `ADMIN_PASSWORD_BCRYPT` (и при необходимости `ADMIN_USERNAME`, но по умолчанию всегда `admin`) перед запуском `run-dev.ps1`.
  - На сервере предпочтительно хранить в `/etc/chillhub/admin.env`; при запуске скрипт формирует drop‑in, ссылающийся на этот файл.
 - Быстрый сброс (локально):
   - Перед стартом: `./scripts/run-dev.ps1 -ResetAdminAuth ...` — сгенерирует новый случайный пароль (будет показан в консоли), пересчитает `ADMIN_PASSWORD_BCRYPT`, создаст новый `JWT_SECRET` и запустит процессы с этими значениями.
   - Во время работы: в интерактивном окне `run-dev.ps1` нажмите `p` или русскую `з` для сброса пароля и `JWT_SECRET` с последующим автоперезапуском процессов.

### 8.5. Режим технических работ

Позволяет на время релиза или обслуживания раздачи запретить клиентам установку,
обновление и/или запуск игр и показать баннер с причиной. Реализация —
`server/internal/maintenance/`.

Хранение: один файл `<contentRoot>/maintenance/state.json` (в проде —
`/var/www/launcher/maintenance/state.json`). Никакой БД: состояние лежит рядом с
реестром игр и инбоксом обратной связи, `cat` показывает истину. **Отсутствие
файла = режим выключен**; это не ошибка. Запись атомарная (temp + rename).

Публичный эндпоинт для лаунчера — `GET /api/maintenance`, см.
[раздел 7](#api-maintenance). Ниже — админские, все под `/admin/api/...`:

- `GET /admin/api/maintenance/get` → `{ state, effective, path }`, где `state` —
  запись как она лежит на диске, `effective` — то же, что увидит клиент прямо
  сейчас (позволяет показать «включено, но окно закончилось час назад»), `path` —
  путь к файлу состояния (для поддержки).
- `POST /admin/api/maintenance/set` — тело: полное желаемое состояние
  ```json
  {
    "enabled": true,
    "reason": "Обновляем раздачу",
    "startsAt": "2026-08-01T20:00:00Z",
    "endsAt": "2026-08-01T22:00:00Z",
    "blocks": { "install": true, "update": true, "launch": false }
  }
  ```
  Частичного обновления нет намеренно: наполовину применённое окно техработ хуже,
  чем его отсутствие. Валидация: тело не больше 64 КиБ; `reason` обрезается до
  500 байт; `startsAt`/`endsAt` — строго RFC 3339, иначе **400**; `endsAt` должен
  быть позже `startsAt`, иначе **400**; обе метки нормализуются в UTC. Сервер сам
  проставляет `updatedAt` и `updatedBy` (логин администратора). Ответ — та же
  структура, что у `get`.
- `POST /admin/api/maintenance/clear` — удаляет файл состояния (канонический
  «выключено»). Отсутствие файла не считается ошибкой.

Изменения пишутся в журнал строками `[audit] maintenance set ...` / `[audit] maintenance clear ...`.

Автосброс: окно считает сервер (`Effective`) при каждом запросе. Истёкшее или ещё
не начавшееся окно отдаётся как «выключено» вместе со всеми блокировками —
устаревший `endsAt` не может навсегда запереть клиентов.

### 8.6. Метрики лаунчера

Минимальная телеметрия (`server/internal/metrics/`): не система мониторинга, а
файл событий и один агрегирующий запрос. Ни TSDB, ни экспортёров, ни внешних
зависимостей.

**Публичный приём:** `POST /metrics/report` (без авторизации; обслуживается
процессом admin, потому что он — единственный писатель файла событий).
- Rate limit: **30 запросов в минуту** на адрес клиента (`metricsRateLimit` в
  `server/cmd/admin/main.go`; для сравнения `/feedback/submit` — 5/мин).
- Ограничение тела: **8 КиБ** (`MaxBodyBytes`).
- Поля события: `installId`, `event`, `appVersion`, `os`, `gameId`, `version`,
  `result`, `durationMs`, `bytes`, `errorCode`.
  - `event` — только из списка: `launcher_start`, `game_install`, `game_update`,
    `game_launch`, `error`. Неизвестное значение — **400** (опечатка в клиенте
    видна сразу, а не портит агрегат).
  - `result` — `ok` | `fail` | `cancel`; иное значение просто отбрасывается.
  - `gameId` — либо пустой (событие без игры, как `launcher_start`), либо
    существующая игра. Проверка двойная: формат (`IsSafeGameID`, не длиннее 80,
    без ведущего `_`) и наличие в реестре `manifests/_registry/games.json`.
    Не прошло — **400** и `chillhub_telemetry_rejected_total{reason="unknown_game"}`.
    Реестр читается через кэш по mtime и размеру файла, поэтому добавленная в
    админке игра начинает считаться без перезапуска, а нечитаемый реестр
    оставляет только проверку формата — дыра в статистике хуже лишней строки.
    Та же проверка применяется и при агрегации: события, принятые до появления
    гейта, уходят из панели без переписывания и очистки файла.
  - Отрицательные `durationMs`/`bytes` обнуляются, строки обрезаются по длине
    (installId 64, версии 64, os 120, gameId 80, errorCode 120).
  - Время события ставит СЕРВЕР (`ts`, UTC); часы клиента не используются.

**Что собирается и что не собирается** (это осознанное решение, зафиксированное в
комментарии пакета):
- Собирается: `installId` — непрозрачный случайный идентификатор, который клиент
  генерирует один раз и хранит локально; он идентифицирует установку, а не
  человека, и НЕ выводится из железа, MAC, серийника диска, Windows SID, имени
  пользователя или аккаунта. Плюс тип события, версия лаунчера, грубая строка ОС,
  контекст установки/обновления и время приёма.
- НЕ собирается и отбрасывается, даже если клиент это пришлёт: IP‑адрес (адрес
  запроса используется только для rate limit и в файл не пишется), имена
  пользователей и аккаунтов, имя машины, e‑mail, пути файловой системы и каталоги
  установки, идентификаторы оборудования, отпечатки экрана/локали, произвольный
  текст логов. Список полей — allowlist самого декодера: посторонние члены JSON
  отбрасываются, поэтому будущая версия клиента не сможет «случайно» добавить поле.

**Хранение:** `<contentRoot>/metrics/events.jsonl` — по одному JSON‑объекту на
строку, только дозапись. Ротация вместо подрезки: по достижении `MaxFileBytes`
(16 МиБ) активный файл становится `events.1.jsonl`, предыдущее поколение
удаляется. Итого дисковый потолок — 2×16 МиБ. (Инбокс обратной связи устроен
иначе — он переписывает весь JSON на каждое обращение и потому нуждается в
жёстком лимите записей; для метрик такая схема была бы квадратичной.)

**Админские эндпоинты:**
- `GET /admin/api/metrics/summary?from=&to=&gameId=` — сводка за период.
  `from`/`to` — RFC 3339 (иначе 400), по умолчанию последние 30 суток; `gameId` —
  необязательный фильтр. Ответ: `totals` (события, запуски лаунчера, установки и
  их ok/fail, обновления и их ok/fail, запуски игр, ошибки, уникальные установки,
  скачанные байты, средние `avgInstallMs`/`avgUpdateMs` — только по успешным
  операциям с указанной длительностью), `byDay` (сутки по UTC, не более 400
  корзин), `byGame`, `topErrors`, `appVersions`, `os` (топ‑20 в каждом).
  Битая строка в файле пропускается, обрыв чтения не роняет сводку — частичные
  числа лучше, чем 500.
- `POST /admin/api/metrics/clear` — удаляет оба поколения файла; пишет
  `[audit] metrics clear by=...`.

### 8.7. Обратная связь (Feedback)

- Публичный приём: `POST /feedback/submit` (без авторизации, rate limit 5 запросов
  в минуту на адрес клиента). Ограничения хранилища: `MaxLogBytes` 256 КиБ на
  вложенные логи, `MaxItems` 2000 обращений, `MaxTotalBytes` 64 МиБ на файл
  инбокса; при превышении инбокс подрезается (`Prune`).
- Хранение: `<contentRoot>/feedback/inbox.json` (в репозитории не отслеживается —
  там персональные данные пользователей).
- Админские эндпоинты: `GET /admin/api/feedback/list`, `GET /admin/api/feedback/get`,
  `POST /admin/api/feedback/delete`, `POST /admin/api/feedback/toggleImportant`,
  `POST /admin/api/feedback/markRead`, `POST /admin/api/feedback/markUnread`,
  `POST /admin/api/feedback/clear`.
- Клиент отправляет как ручные обращения (`Core/Home/FeedbackService.cs`), так и
  автоматические отчёты о необработанных исключениях (`Core/ErrorReporter.cs`);
  последние отключаются настройкой «Конфиденциальность» в лаунчере
  (`AppConfig.AutoErrorReports`).

## 9. Клиент (launcher/)

[↑ к оглавлению](#оглавление) • [↑ наверх](#техническое-задание-тз--chillhub-mvp)
- Технологии: C# WPF (.NET 8), NSIS инсталлятор.
- Конфигурация: `ConfigService.Current` (`launcher/ChillHub/Core/Config.cs`) — `ApiBaseUrl`, `GamesPath`, `DownloadThreads` (2–16), `LastGameId`, `AutoErrorReports`. Путь к файлу — только через `ConfigService.ConfigFilePath`, см. [9.1](#91-данные-клиента-конфиг-и-логи).
- Self-update:
  - При старте открывается окно `UpdateWindow.xaml.cs`, которое:
    - Читает `GET {ApiBase}/manifests/launcher/latest.json`.
    - Загружает манифест `{ApiBase}/manifests/launcher/{version}.json`, проверяет его структуру и сравнивает по хешам локальные файлы лаунчера (SHA-256 и Blake3, если заданы).
    - Файлы из preserve‑списка (`config.json`, `launcher.version`) и служебные артефакты апдейтера при сравнении и скачивании ПРОПУСКАЮТСЯ: апдейтер их всё равно не перезаписывает, и любое расхождение по ним даёт бесконечный цикл самообновления. Единый источник правил — `updater/UpdatePreserve.cs` (`PreserveMatcher`), общий для лаунчера и апдейтера.
    - При необходимости скачивает в `%TEMP%/ChillHub/SelfUpdate/{version}` только отличающиеся файлы (через `SimpleSyncService` план/дифф).
    - Применение: генерирует `apply-update.cmd` (robocopy избранных файлов, создание пустых папок) и перезапускает приложение. Логи применения — `apply-update.log` в temp-папке.
    - DEV-флаг: `YL_DEV_SKIP_SELF_UPDATE=1` пропускает проверку самообновления (см. `UpdateWindow.xaml.cs`).
- Игры:
  - Стартовая страница `HomePage.xaml.cs`:
    - Грузит список игр: `GET {ApiBase}/api/games`.
    - Для выбранной игры подгружает список версий: `GET {ApiBase}/api/games/{gid}/builds` и новости: `GET {ApiBase}/news/games/{gid}/index.json`.
    - Кнопка «Обновить» всегда ставит latest (если известен), иначе первую доступную версию.
    - План обновления формируется через `SimpleSyncService.PlanAsync(manifest, localRoot, contentBaseUrl)` где `contentBaseUrl = {ApiBase}/content/{gid}/{version}/files`.
    - Выполнение диффа: многопоточная загрузка (по умолчанию 8 потоков; ограничение 2–16), поддержка HTTP Range и возобновление `.part`, проверка хешей, перенос в целевой корень, удаление лишних файлов, очистка пустых директорий, создание пустых директорий из манифеста.
    - Проверка свободного места: только по итоговому объему диффа (`DriveInfo.AvailableFreeSpace >= TotalDownloadBytes`).
    - Создание ярлыка на рабочем столе после успешной установки (если указан `exeRelativePath`).
  - Запуск игры: по настройке `exeRelativePath` из реестра игр, путь строится как `{GamesPath}/{gameId}/{exeRelativePath}`.
- Новости в клиенте:
  - Лаунчер: `GET {ApiBase}/news/index.json`.
  - Игра: `GET {ApiBase}/news/games/{gameId}/index.json`.
  - Полный текст карточки открывается по `*.md` URL (роутинг в клиенте).
- Структурная проверка манифеста выполняется в единственной точке — `SimpleSyncService.GetManifestAsync` (`ManifestValidator.Validate`), до любой загрузки файлов. Это покрывает и синхронизацию игр, и самообновление лаунчера.

### 9.1. Данные клиента: конфиг и логи

- Конфигурация: `%APPDATA%\ChillHub\config.json`.
  **Раньше конфиг лежал в `%LOCALAPPDATA%\ChillHub` — это неверно и исправлено.**
  `%LOCALAPPDATA%\ChillHub` — это КАТАЛОГ УСТАНОВКИ лаунчера (`ChillHub.exe`, `*.dll`,
  `runtimes/`), поэтому конфиг оттуда попадал в пакет сборки и в манифест обновления,
  что давало вечный цикл самообновления.
- Миграция: при первом запуске новой версии `ConfigService.MigrateLegacyConfig()`
  переносит `config.json` из `%LOCALAPPDATA%\ChillHub` в `%APPDATA%\ChillHub`.
  Идемпотентно (если новый файл уже есть — ничего не делает), старый файл намеренно
  НЕ удаляется: его ещё может читать не обновившаяся версия, и он входит в
  preserve‑список апдейтера. Ошибки миграции глушатся — она не имеет права ломать запуск.
- Логи: `%APPDATA%\ChillHub\logs\client.log` с ротацией (потолок 5 МиБ, до 3 архивов
  `client.1.log`…`client.3.log`). Путь берётся из `Logger.LogDirectory` / `Logger.LogFilePath`.
- **Логи пишутся по умолчанию.** Переменная `CHILLHUB_CLIENT_LOG` их только
  ВЫКЛЮЧАЕТ: `0`/`false`/`off`/`no` — выключить, любое другое значение и отсутствие
  переменной — включено. Без логов обратная связь и авто‑отчёты приходят пустыми.

### 9.2. Проверка целостности игры

Страница «Настройки» → раздел «Целостность игры». Реализация — `Core/Sync/IntegrityChecker.cs`,
UI — `Pages/SettingsPage.xaml(.cs)`; та же логика доступна со страницы игры.

- Сверяет установленные файлы с манифестом последней опубликованной версии,
  **пересчитывая хеши с диска**; кеш хешей намеренно обходится, поэтому проверка
  может занять несколько минут.
- Отчёт (`IntegrityReport`): всего файлов, отсутствующие, повреждённые, лишние,
  а также признак незавершённого обновления (маркер `.updating` в корне игры).
- Результат — готовый `DiffPlan`, который можно сразу передать в
  `ISyncService.ExecuteAsync`, то есть «Проверить» и «Починить» используют один и
  тот же механизм, что и обычное обновление.
- Файлы из preserve‑списка и служебные (`.staging`, `.version`, `.updating`)
  расхождением не считаются.

### 9.3. Страница игры и откат на другую версию сборки

`Pages/GamePage.xaml(.cs)`, открывается с главной по карточке игры.

- Содержит: сведения об установке и состоянии, прогресс установки/обновления,
  changelog из новостей игры, раздел «Игра по сети».
- Кнопки «Играть» на этой странице нет намеренно — запуск остаётся на главной.
- **Выбор версии сборки, включая откат.** Список версий берётся из
  `GET {ApiBase}/api/games/{gameId}/builds`; можно установить не только `latest`,
  но и любую другую опубликованную версию. Переход на другую версию — тот же
  диффовый механизм: скачивается только разница, лишние файлы удаляются, поэтому
  откат на предыдущую сборку не требует полной перекачки.
- Изменения локального состояния со страницы игры (установка/обновление/откат)
  помечаются флагом, и главная страница обновляет карточку при возврате.

### 9.4. Режим технических работ в клиенте

`Core/Maintenance/MaintenanceService.cs` + `MaintenanceState.cs`.

- Опрашивает `GET {ApiBaseUrl}/api/maintenance` при старте и далее раз в **60 секунд**;
  таймаут одного запроса — 10 секунд.
- Отказоустойчивость важнее свежести: сеть недоступна или ответ не разобрался —
  прежнее состояние сохраняется и пользователю ничего не показывается; сервер без
  этого эндпоинта (404/501) трактуется как «режим выключен», чтобы лаунчер работал
  со старым сервером как раньше. Повторяющиеся сбои пишутся в лог только один раз за серию.
- Что блокировать и как рисовать баннер, решают страницы; сервис лишь хранит
  `Current` и поднимает событие `Changed` (в UI‑потоке).
- Выход из режима автоматический: как только сервер ответил `enabled: false`,
  UI разблокируется без перезапуска клиента.

## 10. Содержимое контента (content/)
- Манифесты: `content/manifests/{gameId}/{version}.json`, `latest.json`.
- Файлы версий: `content/content/{gameId}/{version}/files/...` (клиент формирует прямые URLs по этому корню).
- Новости:
  - Лаунчер: `content/news/index.json`, `content/news/{slug}.md`.
  - По игре: `content/news/games/{gameId}/index.json`, `content/news/games/{gameId}/{slug}.md`.
  - Ассеты: `content/news/assets/**`.
- Реестр игр: `content/manifests/_registry/games.json` (источник Public API).
- Режим технических работ: `content/maintenance/state.json` (отсутствие файла = режим выключен), см. [8.5](#85-режим-технических-работ).
- Метрики: `content/metrics/events.jsonl` и предыдущее поколение `content/metrics/events.1.jsonl`, см. [8.6](#86-метрики-лаунчера).
- Обратная связь: `content/feedback/inbox.json` (персональные данные; в git не отслеживается).
- Временные файлы загрузок админки: `content/tmp/` (чанки и ZIP незавершённых загрузок, подчищаются janitor'ом).

## 11. Инсталлятор (NSIS)
- Per-user инсталляция (`RequestExecutionLevel user`).
- Файлы лаунчера берутся из `launcher/ChillHub/bin/Debug/net8.0-windows/*` (настроить для Release при продакшен сборке).
- Создаются ярлыки в меню и на рабочем столе.
- Создаются папки игр (`D:\Games\ChillHub` или `C:\Games\ChillHub`).
- Uninstall не удаляет пользовательские данные/контент (только ярлыки и записи реестра лаунчера).

## 12. Nginx (общее)
Конфигурация prod хранится в репозитории как `deploy/launcher.conf` и включает:
- Хост: **только** `launcher.samoy.love` (вместе с собственным блоком `:80 → :443`).
  На сервере живут и другие проекты (например `metro.samoy.love`) — они описаны в
  своих файлах `sites-available` со своими сертификатами; добавлять их в наш
  `server_name` нельзя.
- Маршрутизация:
  - `/api/*` → `http://127.0.0.1:55700` (передаются `X-Forwarded-*`). Сюда же попадает `/api/maintenance`.
  - `/feedback/submit` и `/metrics/report` → `http://127.0.0.1:55777`, отдельными `location =` ДО защищённого `/admin/api/` (публичные, без `auth_request`, с малым лимитом тела).
  - `/admin/api/health` → `http://127.0.0.1:55777` отдельным `location =` до `auth_request`.
  - `/admin/api/*` → `http://127.0.0.1:55777` (1:1, без переписывания пути), защищено `auth_request /_auth` → `/admin/api/auth/verify`.
  - `/admin/` и `/admin/ui/*` — статика из `/var/www/launcher/admin_ui/` (точный матч на `/admin/` отдает `admin.html`).
  - `/content/*`, `/manifests/*`, `/news/*` — статика из `/var/www/launcher/...`.
  - `/assets/*` — `try_files` с приоритетом ассетов лендинга и fallback на новости (внутренний `@news_assets_fallback`), чтобы избежать ограничения смешивания `alias`/`root`.
- Добавление `X-Forwarded-Proto`, `X-Forwarded-Host`, `X-Forwarded-Port` — для корректного `baseURL()` в Public API.
- Для стриминга загрузок ZIP в админке отключены буферизации (`proxy_buffering off`, `proxy_request_buffering off`) и увеличены таймауты.
- gzip включён для текста/JSON и намеренно выключен в `location ^~ /content/` (иначе ломаются `sendfile` и докачка по `Range`) — разбор в `deploy/README.md`.
- На dev API/UI (`server/cmd/api`, `server/cmd/admin`) сами отдают статику для удобства локального запуска; в prod статику отдает nginx.

### Раскладка конфига на сервере

Наш конфиг — **отдельный файл**, а не общий на несколько сайтов:

```
/etc/nginx/sites-available/chillhub-launcher.conf   <- копия deploy/launcher.conf
/etc/nginx/sites-enabled/chillhub-launcher.conf     -> симлинк на неё
```

Деплой (`scripts/deploy.sh`, `scripts/deploy-nginx.sh`, `scripts/deploy-win.ps1`,
`.github/workflows/deploy.yml`) перезаписывает **ровно** `chillhub-launcher.conf`.
Пока файл назывался `launcher.conf` и был общим на два сайта, релиз лаунчера сносил
чужой проект; отдельное имя делает это невозможным. Старый симлинк `launcher.conf`
в `sites-enabled` можно удалять только после того, как чужой сайт вынесен в свой файл.

Версия nginx на проде — **1.24.0 (Ubuntu)**, поэтому в конфиге используется
совместимая форма `listen 443 ssl http2;`: отдельная директива `http2 on;`
появилась только в 1.25 и на 1.24 роняет `nginx -t`, из‑за чего reload не применит
конфиг вообще.

### Проверка конфига настоящим `nginx -t` (Docker)

```bash
# основная проверка — на версии прода (nginx:1.24-alpine):
sh deploy/nginx-check.sh
# проверка «на будущее» — на свежем nginx:
NGINX_IMAGE=nginx:alpine sh deploy/nginx-check.sh
```

`deploy/nginx-check.sh` поднимает контейнер с официальным образом nginx и гоняет там
настоящие `nginx -T` и `nginx -t`. Скрипт сам генерирует одноразовую обёртку
(`events {}` + `http { include ... }`), так как `launcher.conf` — фрагмент для
`sites-available` и валиден только внутри `http { ... }`; подключает конфиг **без
изменений** (монтирует read‑only) и создаёт самоподписанный сертификат по тем же
путям Let's Encrypt, что указаны в конфиге. Код возврата — родной от nginx
(`0` = конфиг в порядке). Требуется Docker. Скрипт готов к запуску на любом раннере
с Docker одной строкой (`sh deploy/nginx-check.sh`), но в текущие workflow ещё не
подключён. Подробности и история (почему `crossplane` было недостаточно) — в
`deploy/README.md`.

На сервере конфиг всё равно проверяется: `.github/workflows/deploy.yml` и
`scripts/deploy.sh` делают бэкап, устанавливают файл, выполняют `nginx -t` и
откатываются при ошибке. Авторитетной проверкой на боевом хосте остаётся
`sudo nginx -t`.

Производственные директории:
- Лендинг: `/var/www/site` (копия `landing/`).
- Контент: `/var/www/launcher/{content,manifests,news,admin_ui}` (админ‑UI синхронизируется; каталоги `content,manifests,news` наполняются через Admin UI или вручную, деплой их не модифицирует).
- Бинарники: `/opt/chillhub/{api,admin}`.

Сертификаты: Let’s Encrypt (рекомендуется `certbot --nginx -d launcher.samoy.love`).

Firewall (пример UFW): открыть 80/443, закрыть 55700/55777 наружу.

VPS: `<SERVER_IP> (Ubuntu, user: ubuntu)`.

Порты приложений: `:55700` (Public API), `:55777` (Admin API) — слушают loopback.

Systemd:
- `deploy/systemd/chillhub-api.service`
- `deploy/systemd/chillhub-admin.service`

GitHub Actions деплой: `.github/workflows/deploy.yml`.

Полный пример конфига Nginx вынесен в `deploy/README.md` (раздел «Nginx (prod) — полный пример конфига»). Для боевой установки используйте `deploy/launcher.conf` и сравнивайте с примером при отладке.

Secrets для CI/CD: `SSH_HOST`, `SSH_USER`, `SSH_PRIVATE_KEY`.

Маппинг Admin API: публичный путь `/admin/api/*` проксируется на бэкенд `/admin/*` без изменения кода бэкенда.

## 13. Нефункциональные требования
- Производительность загрузок: многопоточность 2–16 потоков (по умолчанию 8), HTTP Range, возобновление.
- Надежность: 3 попытки скачивания на файл с экспоненциальной задержкой, проверка хешей на диске после скачивания, атомарное перемещение из staging.
- UX/Удобство:
  - Админка: drag&drop для ZIP и изображений, предпросмотры, массовые действия.
  - Клиент: индикаторы прогресса, скорость, ETA, блокировка кнопок на время обновления, проверка запускаемой игры.

## 14. Ограничения и допущения (MVP)
- Клиентские логи ВКЛЮЧЕНЫ по умолчанию (`%APPDATA%\ChillHub\logs\client.log`, ротация). Переменная `CHILLHUB_CLIENT_LOG=0` их выключает — см. [9.1](#91-данные-клиента-конфиг-и-логи). Серверные логи — стандартный вывод (`httpx.Logging` в обоих сервисах).
- Подписи манифестов нет: подлинность раздачи держится только на TLS. Структурная проверка манифеста при этом обязательна и от режимов не зависит.
- Генерация Blake3-хеша сервером не реализована в `generate-manifest.ps1` (может быть пустым). Клиент поддерживает проверку, если значение присутствует. Штатный путь публикации — загрузка ZIP через админку, там оба хеша считает сервер.
- Защита админки реализована: cookie + JWT, CSRF, `auth_request` в nginx (см. [8.4](#84-admin-login)).
- Метрики принимаются сервером (`POST /metrics/report`), но лаунчер их пока НЕ отправляет: клиентской части нет, поэтому сводка в админке будет пустой.

## 15. Сценарии и флоу
- Публикация новой версии игры:
  1) Собрать ZIP с содержимым `{version}/files/...`.
  2) В админке во вкладке «Игры» выбрать `gameId`, `version`, загрузить ZIP с чекбоксом «Обновить latest» при необходимости.
  3) Проверить список версий (`/admin/api/list?gameId=`), при необходимости активировать latest отдельной кнопкой.

- Публикация новой версии лаунчера:
  1) Вкладка «Лаунчер»: указать `version`, загрузить ZIP; опционально обновить latest.
  2) Клиенты при запуске проверят манифест и обновятся по диффу.

- Управление списком игр:
  1) Вкладка «Игры (редактирование)»: 
     - «Обновить» — подтянет новые `gameId` из папок манифестов.
     - «Добавить» — вручную добавляет строку.
     - «Сохранить» — записывает `content/manifests/_registry/games.json`.
  2) Указать `exeRelativePath` и `iconUrl` для лучшего UX клиента.

- Включение режима технических работ:
  1) `POST /admin/api/maintenance/set` (из админки) с полным состоянием: `enabled`, `reason`, при необходимости `startsAt`/`endsAt` в RFC 3339 и набор `blocks`.
  2) Клиенты подхватят режим на ближайшем опросе (не позже чем через минуту) и покажут баннер.
  3) Выключение: дождаться `endsAt` (сервер сбросит режим сам) либо `POST /admin/api/maintenance/clear`.

- Новости:
  1) Выбрать раздел «Лаунчер» или «Игра», выбрать игру (если требуется).
  2) Создать/открыть материал, редактировать Markdown, загрузить/привязать обложку.
  3) «Сохранить» и «Опубликовать» (published=true). 
  4) При необходимости пересобрать индекс.

## 16. Требования к качеству и тестированию
- Юнит/интеграционные проверки (минимум):
  - Парсинг манифестов и корректная сборка плана диффа при сценариях: только добавления, только удаление, смешанные изменения, пустые папки.
  - Проверка хешей: совпадение/несовпадение SHA-256 и Blake3.
  - Проверка свободного места: отказ, если дифф не помещается.
  - Падение сети и возобновление скачивания `.part`.
  - Public API: корректные ответы, фильтрация новостей по `published`.
  - Admin API: сохранение реестра, загрузка ZIP, rebuild индексов новостей, работа с ассетами (создание/переименование/удаление/загрузка/по URL) включая ffmpeg и без него.

## 17. План расширений (после MVP)
- ~~Авторизация админ-панели, аудит действий~~ — сделано (cookie+JWT, CSRF, строки `[audit] ...` в журнале). Внешний IdP (OIDC) не требуется.
- Поддержка дифф-патчей по блокам/битторрент/компрессии.
- Клиентская часть метрик: лаунчер пока не отправляет события в `POST /metrics/report` (серверная часть готова, см. [8.6](#86-метрики-лаунчера)).
- Алерты по метрикам (Telegram) и серверные метрики RPS/latency — не реализованы, см. `Backlog.md`.
- Автогенерация Blake3 при сборке контента вне админки.
- Релизные сборки (Release) — см. раздел 27.

## 18. Ссылки на ключевые файлы
- Public API: `server/cmd/api/main.go`
- Admin API: `server/cmd/admin/main.go` (процесс), `server/cmd/admin/routes.go` (список эндпоинтов), обработчики — `server/internal/adminapi/*`
- Режим техработ: `server/internal/maintenance/maintenance.go`
- Метрики: `server/internal/metrics/metrics.go`
- Админ UI: `server/admin_ui/admin.html`, `server/admin_ui/admin.js`
- Клиент: 
  - Обновление лаунчера: `launcher/ChillHub/UpdateWindow.xaml.cs`
  - Синхронизация файлов: `launcher/ChillHub/Core/Sync/SimpleSyncService.cs`
  - Проверка целостности игры: `launcher/ChillHub/Core/Sync/IntegrityChecker.cs`
  - Конфиг и логи: `launcher/ChillHub/Core/Config.cs`, `launcher/ChillHub/Core/Logging/Logger.cs`
  - Режим техработ: `launcher/ChillHub/Core/Maintenance/MaintenanceService.cs`
  - Главная страница: `launcher/ChillHub/Pages/HomePage.xaml.cs`; страница игры: `launcher/ChillHub/Pages/GamePage.xaml.cs`; настройки: `launcher/ChillHub/Pages/SettingsPage.xaml.cs`
- Апдейтер и preserve‑правила: `updater/Program.cs`, `updater/UpdatePreserve.cs`, тест `updater/tests/ManifestPreserveCheck`
- Инсталлятор: `scripts/installer.nsi`
- Nginx (prod): `deploy/launcher.conf` (на сервере — `chillhub-launcher.conf`), проверка — `deploy/nginx-check.sh`
- Systemd: `deploy/systemd/chillhub-api.service`, `deploy/systemd/chillhub-admin.service`
- CI/CD: `.github/workflows/deploy.yml`
- Документация скриптов: `scripts/README.md`

## 19. Домены и DNS
- `launcher.samoy.love` → A: `<SERVER_IP>` (Oracle VPS)
- `samoy.love` → может оставаться на текущих A; при переводе на VPS — также A: `<SERVER_IP>`.
- Почта: оставить текущие MX записи (Mail.ru), SPF/TXT/NS/CAA без изменений.

Рекомендуемые записи:
- A `launcher.samoy.love` = `<SERVER_IP>`, TTL 3600.
- (Опционально) A `samoy.love` = `<SERVER_IP>`, если нужен плейсхолдер с этого же сервера.

Let’s Encrypt выписывает сертификаты на оба домена.

## 20. CI/CD (GitHub Actions)
Workflow `.github/workflows/deploy.yml` (при наличии):
- Триггер: вручную (workflow_dispatch).
- Шаги: checkout → setup-go → сборка бэкендов для linux/amd64 → копирование артефактов (landing, admin_ui, nginx.conf, systemd) → SCP на сервер → SSH-скрипт раскладки. ВАЖНО: каталоги `manifests/content/news` НЕ синхронизируются в прод (управляются через Admin UI) — см. `.github/workflows/deploy.yml`.
- Secrets: `SSH_HOST`, `SSH_USER`, `SSH_PRIVATE_KEY`.

### 20.1. Требование паритета с scripts/deploy.sh
`deploy.yml` обязан обеспечивать ту же функциональность, что и `scripts/deploy.sh`:
- Сборка Go‑бинариев для linux/amd64 и раскладка в `/opt/chillhub`.
- Синхронизация статик: `landing/` → `/var/www/site`, `server/admin_ui/` → `/var/www/launcher/admin_ui/`.
- Установка `deploy/launcher.conf` как `/etc/nginx/sites-available/chillhub-launcher.conf` и перезагрузка nginx с валидацией `nginx -t` (с бэкапом и откатом при ошибке).
- Перезапуск `chillhub-api.service`, `chillhub-admin.service` и `daemon-reload`.
- Опциональная генерация systemd drop-in с переменными окружения из GitHub Secrets (`JWT_SECRET`, `ADMIN_USER`, `ADMIN_PASSWORD_BCRYPT` или `ADMIN_PASSWORD_PLAIN`, `COOKIE_DOMAIN`, `COOKIE_SECURE`).
- Опциональная синхронизация внешней папки установщиков `DOWNLOADS_DIR` → `/var/www/site/downloads` (если секрет задан и каталог существует на сервере).
- Послеукладочные смоук‑тесты (Admin UI, Admin API health, лендинг, статика), с диагностикой при сбоях (nginx/systemd/journalctl).

Этот паритет реализован в `.github/workflows/deploy.yml` (см. шаг «Deploy on server (SSH)»).

### 20.2. Паритет и различия: GitHub Actions vs scripts/deploy.sh

Файл рабочего процесса: `.github/workflows/deploy.yml`.

- Что совпадает с `scripts/deploy.sh` (паритет):
  - Сборка Go‑бинариев (`api`, `admin`) под Linux/amd64 (`CGO_ENABLED=0`).
  - Выкладка «только статики»:
    - `landing/` → `/var/www/site/` (с `--delete`).
    - `server/admin_ui/` → `/var/www/launcher/admin_ui/` (с `--delete`).
  - Каталоги контента `/var/www/launcher/{manifests,content,news}` не трогаются. Управляются через Admin UI или вручную — это критично для сохранности данных.
  - Установка nginx‑конфига в `/etc/nginx/sites-available/chillhub-launcher.conf` (отдельный файл, чужие сайты на хосте не затрагиваются), проверка `nginx -t`, `systemctl reload nginx`.
  - Перезапуск `chillhub-api.service`, `chillhub-admin.service`.
  - Смоук‑тесты (Admin UI, Admin API, лендинг, статика, soft‑checks `latest.json` и `assets/ping.txt`) с диагностикой при сбоях.

- Различия:
  - CI доставляет артефакты через SCP в один временный каталог на сервере (`$HOME/deploy`), затем выполняет SSH‑скрипт для раскладки.
  - Обработка секретов:
    - В `deploy.sh` используется `EnvironmentFile=/etc/chillhub/admin.env` (персистентно). В CI‑скрипте переменные подставляются прямо в drop‑in (`Environment=...`), то есть не создаётся/не переписывается `admin.env`.
    - CI может принять секреты: `JWT_SECRET`, `ADMIN_USER`, `ADMIN_PASSWORD_BCRYPT` или `ADMIN_PASSWORD_PLAIN` (если задан только plain, workflow сам вычислит bcrypt через небольшой Go‑сниппет). Также поддерживаются `COOKIE_DOMAIN`, `COOKIE_SECURE`.
  - В CI добавлен опциональный шаг синхронизации внешней директории установщиков (`DOWNLOADS_DIR`) в `/var/www/site/downloads/`, если переменная задана и каталог существует на сервере.

- Известные мелочи/примечания:
  - В конце сценария CI есть двойной вызов `daemon-reload`/`restart` сервисов (перед nginx и после) — не критично, но можно убрать дублирование без изменения функционала.

Требования к секретам для CI:

- Обязательные: `SSH_HOST`, `SSH_USER`, `SSH_PRIVATE_KEY`.
- Опциональные: `JWT_SECRET`, `ADMIN_USER`, `ADMIN_PASSWORD_BCRYPT` или `ADMIN_PASSWORD_PLAIN`, `COOKIE_DOMAIN`, `COOKIE_SECURE`, `DOWNLOADS_DIR`.

Поведение по умолчанию: контент (`manifests`, `content`, `news`) не синхронизируется ни в CI, ни в `deploy.sh`. Их наполнение осуществляется через Admin UI или вручную.

## 21. Деплой на сервер (ручной)
См. также сводную таблицу шагов в `README.md` → раздел «[Карта шагов: автодеплой vs вручную](README.md#карта-шагов-автодеплой-vs-вручную)».
1) Подготовка сервера: `nginx`, `rsync`, `ufw`, `certbot`.
2) Создать каталоги: `/var/www/site`, `/var/www/launcher/{content,manifests,news,admin_ui}`, `/opt/chillhub`.
3) Разместить `deploy/launcher.conf` в `/etc/nginx/sites-available/chillhub-launcher.conf`, включить symlink в `sites-enabled`, проверить `nginx -t`, `systemctl reload nginx`. Общий файл `launcher.conf` больше не используется — см. [12](#12-nginx-общее).
4) Скопировать только админ-UI статику в `var/www/launcher/admin_ui`. Каталоги `manifests/content/news` не трогать — наполняются через Admin UI.
5) Собрать/скопировать бинарники в `/opt/chillhub`.
6) Установить systemd сервисы, `daemon-reload`, `enable`, `restart`.
7) Выписать сертификаты LE.

## 22. Локальная разработка и автотесты деплоя
Подробные инструкции по локальному запуску и флагам вынесены в `scripts/README.md` (раздел про `run-dev.ps1`).

Локальные проверки, которые стоит гонять перед выкладкой:

```bash
# Go: юнит-тесты серверных пакетов (auth, builds, news, feedback,
# maintenance, metrics, ratelimit, adminutil, роутер admin)
cd server && go test ./...

# .NET: тесты клиента (план/дифф, кеш хешей, маркер .updating, проверка манифеста)
dotnet test launcher/tests/ChillHub.Tests

# nginx: настоящий `nginx -t` в Docker на версии прода
sh deploy/nginx-check.sh

# предохранитель от петли самообновления
dotnet run --project updater/tests/ManifestPreserveCheck
```

### Предохранитель от петли самообновления (`updater/tests/ManifestPreserveCheck`)

Класс регрессии, ради которого существует тест: файл попадает **одновременно** в
манифест лаунчера и под preserve‑правила апдейтера. Апдейтер такой файл не
перезаписывает (на то он и preserve), а лаунчер сверяет его хеш с манифестом —
расхождение неустранимо, и обновление предлагается при каждом запуске, каждый раз
полностью перекачивая лаунчер.

Именно так себя вели `config.json` (расходится после первого запуска игры, когда
сохраняется `LastGameId`) и `launcher.version` (апдейтер пишет его сам, UTF‑8 с
BOM — 10 байт против 8 в манифесте). Усугублялось тем, что каталог установки
`%LOCALAPPDATA%\ChillHub` совпадал с каталогом пользовательского конфига; конфиг
переехал в `%APPDATA%\ChillHub` — см. [9.1](#91-данные-клиента-конфиг-и-логи).

Поэтому **пользовательские файлы не должны попадать в манифест лаунчера.**
Preserve‑правила заданы в одном месте — `PreserveMatcher.DefaultRules` в
`updater/UpdatePreserve.cs` (`config.json`, `launcher.version`); их используют и
апдейтер, и лаунчер.

Запуск:

```bash
dotnet run --project updater/tests/ManifestPreserveCheck
# либо по конкретным файлам/каталогам манифестов:
dotnet run --project updater/tests/ManifestPreserveCheck -- <файл-или-каталог> [...]
```

Тест сначала проверяет сам детектор на двух встроенных манифестах («плохой» обязан
падать, «хороший» — проходить), затем сканирует реальные манифесты репозитория.
Код возврата: `0` — чисто, `1` — найдено пересечение (или сломан сам детектор).

## 23. Безопасность секретов
- SSH-ключи хранятся в GitHub Secrets. На сервере должен быть установлен публичный ключ пользователя `ubuntu`.
- В репозитории не хранить приватные ключи и пароли.

---
Это ТЗ агрегирует явные и неявные требования из репозитория и предыдущих обсуждений. При изменениях реализации требуется синхронизировать документ.

## 24. Выявленные проблемы и рекомендации

- **Маршруты и путаница путей** — решено.
  - Канонический префикс в коде — `/admin/api/...`; форма `/admin/...` порождается автоматически из того же списка (`server/cmd/admin/routes.go`), поэтому «потерять» маршрут при добавлении нового больше нельзя.

- **Безопасность админки**
  - Используется одна учётная запись из ENV (`ADMIN_USERNAME`/`ADMIN_PASSWORD_BCRYPT`) — этого достаточно. Ограничения по IP и rate‑limit в рамках этого раздела не требуются. Логирование событий входа — да. Поддержка внешнего IdP (OIDC) — не требуется.
  - CSRF реализован (cookie `csrf_token` + заголовок). Проверить и выставить прод‑значения `SameSite`/`Secure` через ENV (`COOKIE_DOMAIN`, `COOKIE_SECURE=true`).

- **CORS**
  - Public API (`server/cmd/api`): разрешено `*`. Допустимо — там нет cookie‑авторизации.
  - Admin API: CORS по умолчанию **выключен** (`httpx.CORSDisabled`), потому что админка отдаётся с того же origin, а авторизация — на cookie. Открыть конкретные origin'ы можно переменной `ADMIN_CORS_ORIGIN` (список через запятую); `*` вместе с cookie использовать нельзя.

- **Контентная синхронизация в CI/CD**
  - Ранее workflow разворачивал `content/` в прод (риск потери данных). Исправлено: `.github/workflows/deploy.yml` синхронизирует только `landing/`, `admin_ui/`, бинарии и конфиги.

- **Подпись манифестов** — отсутствует намеренно, убрана из проекта.
  - Подлинность раздачи обеспечивается только TLS. Обязательной остаётся структурная проверка манифеста: пути, дубликаты, наличие хешей.

- **Клиент: обработка ошибок** — частично решено.
  - Появился централизованный логгер (`Core/Logging/Logger.cs`, ротация, включён по умолчанию) и авто‑отчёты об ошибках (`Core/ErrorReporter.cs`). Уровней Verbose пока нет; молчаливые `catch { }` в ряде мест остались осознанно (логгер и миграция конфига не имеют права ронять запуск).

- **Статика кеширования**
  - Для больших файлов (`/content/`) выставлен `no-cache`. Возможна оптимизация с ETag/Last-Modified и `Cache-Control: public, max-age=...` для версионных путей.

- **Линт и стиль**
  - `golangci-lint` включает "all" — оставить как есть (конфигурация уже настроена). `stylelint`: при необходимости использовать локальные исключения для Bootstrap.

- **Тесты** — частично решено.
  - Go: есть тесты в `server/cmd/admin` и в пакетах `adminapi/{auth,builds,news,feedback}`, `adminutil`, `maintenance`, `metrics`, `ratelimit` (включая проверку путей манифеста). Запуск: `cd server && go test ./...`.
  - .NET: xUnit‑проект `launcher/tests/ChillHub.Tests` (план/дифф `PlanAsyncTests`, кеш хешей, маркер `.updating`, структурная проверка манифеста) и отдельный предохранитель `updater/tests/ManifestPreserveCheck` (см. [22](#22-локальная-разработка-и-автотесты-деплоя)).
  - Не покрыто: сценарии «мало места», сетевые сбои и несоответствие хеша на живой загрузке.

## 25. Дорожная карта доработок (этапы)

### Этап 1 (высокий приоритет)
- ~~Перевести бэкенд admin на единственный префикс `/admin/api/*`~~ — сделано: канонический список маршрутов в `server/cmd/admin/routes.go`, `/admin/*` — автоматический алиас. Осталось при желании убрать shim в `server/admin_ui/admin.js`.
- Добавить rate‑limit на `/admin/api/auth/login` на уровне Nginx (`limit_req`) — без изменений кода Go. (Лимиты уже есть у публичных `/feedback/submit` и `/metrics/report` — в коде Go, `server/internal/ratelimit`.)
- ~~Ревизия документации по схемам Public/Admin API~~ — выполнена в версии 1.3 документа.
- Подключить `sh deploy/nginx-check.sh` в CI (скрипт готов, workflow пока его не вызывает).
- Улучшить DX деплоя/запуска (Windows):
  - Makefile цель `make dev`: запуск `api`, `admin`, WPF‑клиента через `scripts/run-dev.ps1` с параметрами (`-ContentRoot`, `-GamesPath`, `-Env`, `-SetClientConfig`, `-BuildServers`).
  - Makefile цель `make smoke`: локальные смоук‑проверки HTTP (health/UI/статика) и простые проверки клиента (чтение конфигурации, базовые вызовы API), с читаемыми отчётами.
  - Makefile цель `make deploy`/`make deploy-nobuild`: прокси к CI шагам, унификация команд вручную и в автодеплое.
  - Комфортные пресеты для частых сценариев: «чистый старт dev», «против prod API», «перезапуск всех процессов», «сборка серверов под Windows».
- Анти‑FP (AV/Defender), часть 1:
  - Быстрый Defender‑scan артефактов в CI.
  - Прописать версии/метаданные в `.csproj` и NSIS (VIAddVersionKey, BrandingText).

### Этап 2 (средний)
- Ввести ETag/Last‑Modified и корректные Cache‑Control для статик‑локаций Nginx (особенно для версионных путей контента).
- Единый «deploy dry‑run»: шаг в CI, который выполняет все команды, кроме реального копирования/рестартов (проверка `nginx -t`, smoke‑скриптов).
- Анти‑FP (AV/Defender), часть 2:
  - VirusTotal API (диагностически) для Release‑кандидатов (учесть rate‑limit; не блокировать релизы на первом этапе).
  - MSIX POC как альтернативная упаковка (опционально; без обязательной миграции).
- Клиентские логи и диагностика:
  - ~~Единый логгер~~ — сделан (`Core/Logging/Logger.cs`, ротация, `%APPDATA%\ChillHub\logs`). Осталось: уровень Verbose и его включение переменной окружения.
  - Улучшенные сообщения об ошибках (включая коды и краткие рекомендации), запись причины отказа при проверке хешей/свободного места/сетевых сбоях.
  - Отдельный диагностический отчёт по результатам self‑update (в temp), удобный для прикрепления к баг‑репортам.

### Этап 3 (средний/низкий)
- Тесты:
  - Go: базовые тесты есть (см. раздел 24 → «Тесты»); не хватает интеграционных на загрузку ZIP и NDJSON‑поток.
  - C#: тесты плана/диффа/хешей есть; не хватает сценариев ошибок (мало места, сетевые сбои, несоответствие хеша) и очистки временных файлов.
- Документация API: сгенерировать OpenAPI/Swagger для Public API.
- Автотесты деплоя как отдельный workflow для stage‑окружения (проверки: `nginx -t`, доступность статики и health endpoints).

<!-- Раздел 26 перенесён в 3.1 -->


## 27. План снижения ложных срабатываний антивирусов/Windows Defender (Launcher, Updater, Installer)

Ниже набор практик и задач для минимизации риска флагов SmartScreen/Defender и движков AV. Все шаги бесплатные: Authenticode‑подписание из проекта исключено, поэтому репутация набирается только объёмом загрузок и корректным поведением приложения.

### 27.1. Метаданные и репутация — [Высокий]
- Исполняемые файлы не подписываются: Authenticode из проекта исключён. Следствие — SmartScreen будет показывать предупреждение при установке, пока не наберётся репутация загрузок. Это осознанный компромисс.
- Версионирование и сведения о продукте: FileVersion, ProductVersion, CompanyName, ProductName, Copyright.
  - Добавить ресурсы версии в проекты `.csproj` и в NSIS (BrandingText, VIAddVersionKey).

### 27.2. Поведение Updater (безопасные паттерны) — [Высокий]
- Избегать самозаменяющегося процесса; апдейтер — отдельный exe (соблюдается).
- Логи — в `%TEMP%/ChillHub/`, не писать в системные каталоги.
- Не запрашивать права администратора без необходимости (никаких UAC при обычных операциях).
- Минимизировать вызовы внешних утилит; по возможности использовать встроенный .NET‑код, не дергать `cmd`/`powershell`.
- Никаких инжекций/драйверов/хака реестра; только файловые операции и перезапуск целевого exe.

### 27.3. Инсталлятор (NSIS) — [Высокий]
- Персональная установка (per‑user), без записи в защищённые каталоги.
- Стандартные MUI/Modern UI диалоги; отсутствие скрытых окон и обфускации скриптов.
- Подпись инсталлятора + таймштамп.
- Альтернатива на будущее: рассмотреть MSIX‑упаковку для лучшей совместимости с защитными политиками Windows.

### 27.4. Сетевое взаимодействие — [Средний]
- Все загрузки — по HTTPS, корректный `User-Agent`, отсутствие self‑signed сертификатов.
- Внешний доступ только через 443 (nginx); внутренние порты слушают loopback (как сейчас).

### 27.5. Содержимое и упаковка — [Высокий]
- Не использовать упаковщики/обфускаторы для exe — это часто триггерит эвристику AV.
- Версионные имена артефактов (например, `ChillHub-Setup-x.y.z.exe`), стабильные пути и структура.

### 27.6. CI/Проверки — [Высокий]
- В CI добавить:
  - Быстрое сканирование Microsoft Defender артефактов релиза.
  - (Опционально) VirusTotal API — как диагностический сигнал для Release‑кандидатов (учитывать rate‑limit).

### 27.7. Отчётность и апелляции — [Средний]
- При ложных срабатываниях — загружать образцы в порталы Microsoft и крупных AV‑вендоров как false positive.
- Вести список хэшей релизов и статусы рассмотрения у вендоров.

### 27.8. Кодовые практики (клиент/апдейтер) — [Средний]
- Не использовать P/Invoke к чувствительным WinAPI без необходимости.
- Исключить бесконечные ретраи/таймеры без backoff (в апдейтере использовать экспоненциальные задержки).
- Понятные имена процессов, читабельные логи, отсутствие скрытых окон без причин.

### 27.9. Распределение — [Высокий]
- Публиковать инсталлятор на подписанном домене `https://launcher.samoy.love/downloads/...`.
- Избегать дистрибуции через файлообменники/непрозрачные источники.

### 27.10. Интеграция в дорожную карту (приоритетизация)
- Этап 1:
  - Добавление FileVersion/ProductVersion/CompanyName в ресурсы версии.
  - Быстрый Defender‑scan артефактов в CI.
- Этап 2:
  - VirusTotal check (диагностически) для Release; POC MSIX; формализация процесса апелляций false positive.
- Этап 3:
  - Автотесты поведения апдейтера (UAC, коды выхода, cleanup), статический анализ (аккуратно тюнить gosec и .NET‑анализаторы).

