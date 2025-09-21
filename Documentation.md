# Техническое задание (ТЗ) — ChillHub (MVP)

Версия документа: 1.2
Дата: 2025-09-21

## Оглавление
- [1. Цели и общая концепция](#1-цели-и-общая-концепция)
- [2. Область MVP](#2-область-mvp)
- [3. Архитектура и структура проекта](#3-архитектура-и-структура-проекта)
  - [3.1. Карта консистентности путей (Path Consistency)](#31-карта-консистентности-путей-path-consistency)
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
- [9. Клиент (launcher/)](#9-клиент-launcher)
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
- [21. Деплой на сервер (ручной)](#21-деплой-на-сервер-ручной)
- [22. Локальная разработка и автотесты деплоя](#22-локальная-разработка-и-автотесты-деплоя)
- [23. Безопасность секретов](#23-безопасность-секретов)
- [24. Выявленные проблемы и рекомендации](#24-выявленные-проблемы-и-рекомендации)
- [25. Дорожная карта доработок (этапы)](#25-дорожная-карта-доработок-этапы)
- [27. План снижения ложных срабатываний антивирусов/Windows Defender](#27-план-снижения-ложных-срабатываний-антивирусовwindows-defender-launcher-updater-installer)
  - [27.1. Подпись кода и метаданные](#271-подпись-кода-и-метаданные)
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
  - Public API (`/api/*`) + статика `/manifests/*`, `/content/*`, `/news/*` для dev.
  - Admin API (`/admin/*`) и статика админки `server/admin_ui/*`.
- `launcher/` — C# WPF лаунчер.
- `landing/` — статический лендинг (отдается по корню `https://launcher.samoy.love/`).
- `deploy/` — конфиги деплоя: `deploy/launcher.conf` (nginx), systemd unit-файлы.
- `content/` — статика (манифесты, бинарное содержимое версий, новости и ассеты).
- `scripts/` — скрипты сборки/публикации (например, `generate-manifest.ps1`, `installer.nsi`).

### 3.1. Карта консистентности путей (Path Consistency)

- Инсталлятор (NSIS)
  - Бинарии лаунчера: `launcher/ChillHub/bin/<Config>/net8.0-windows/*` → инсталлируются в `%LOCALAPPDATA%/ChillHub/` (см. `scripts/installer.nsi`).
  - Ярлыки: меню/рабочий стол.

- Клиент / Обновление
  - Манифест лаунчера: `{ApiBase}/manifests/launcher/latest.json` → `{ApiBase}/manifests/launcher/{version}.json`.
  - Контент лаунчера/игр: `{ApiBase}/content/{gameId}/{version}/files/...` (собирается из манифеста).
  - Локальная папка игр по умолчанию: `D:\Games\ChillHub` (или `C:\Games\ChillHub`).

- Public API (`server/cmd/api`)
  - `/api/*` (проксируется nginx), dev‑статика: `/manifests/*`, `/content/*`, `/news/*`, `/assets/*` → `content/` каталоги.

- Admin API/UI (`server/cmd/admin`, `server/admin_ui`)
  - Prod: `/admin/ui/*` (статика), `/admin/` (admin.html), `/admin/api/*` (backend, защищён через `auth_request`).
  - Dev: `http://localhost:55777/admin` (бэкенд + выдача статики для удобства).

- Nginx (`deploy/launcher.conf`)
  - root `/var/www/site` → лендинг.
  - alias `/var/www/launcher/{content,manifests,news,admin_ui}`.
  - fallback `/assets/*` → сначала лендинг `/var/www/site/assets/`, затем `@news_assets_fallback` → `/var/www/launcher/news/assets/`.

Домены и маршрутизация (prod):
- `launcher.samoy.love` — боевой домен лаунчера:
  - `/` — лендинг из `landing/`.
  - `/api/*` — прокси на Public API (`127.0.0.1:55700`).
  - `/admin/api/*` — прокси на Admin API (`127.0.0.1:55777`) 1:1 (без переписывания пути).
  - `/admin/` и `/admin/ui/*` — статика админки из `/var/www/launcher/admin_ui/` (точный матч для `/admin/` отдает `admin.html`).
  - `/content/*`, `/manifests/*`, `/news/*` — статика контента (`/var/www/launcher/...`).
  - `/assets/*` — «комбинированные ассеты»: сперва лендинг (`/var/www/site/assets`), затем fallback на новости (`/var/www/launcher/news/assets`).
- `samoy.love` — плейсхолдер/заглушка (простая страница) — включается при необходимости.

Порты (dev):
- Public API: `:55700` (`server/cmd/api`)
- Admin API/UI: `:55777` (`server/cmd/admin`)

## 4. Пути установки по умолчанию
- Лаунчер: `%LOCALAPPDATA%/ChillHub/` (`scripts/installer.nsi` — `APPDIR`) 
- Игры: `D:/Games/ChillHub/` (если диска `D:` нет — `C:/Games/ChillHub/`).

## 5. Безопасность и целостность
- Подпись манифестов не используется; целостность обеспечивается хешами файлов (Blake3 — основной, SHA-256 — опционально). Клиент проверяет доступные хеши. На стороне админ‑сервера хеши считаются при загрузке ZIP.
- Клиент не должен определяться как вирус:
  - Использовать стандартные механики самообновления (robocopy, temp + перезапуск), без подозрительных техник.
  - Подписывать инсталлятор/EXE на этапах релиза (см. раздел 27).

## 6. Форматы данных
### 6.1. Манифест версии
Генерируется сервером Админки при загрузке ZIP и хранится в `content/manifests/{gameId}/{version}.json` (для лаунчера: `gameId = launcher`). Хеши (SHA-256 и Blake3) считаются на сервере.

Структура (единая, подтверждена в `server/cmd/admin/main.go`):
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
  "emptyDirs": ["Saves", "Cache"]
}
```
Дополнительно: `content/manifests/{gameId}/latest.json` содержит `{ "version": "1.x.y" }`.

Примечания:
- Админ-сервер при загрузке ZIP считает оба хеша (SHA-256 и Blake3) за один проход по файлу.

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

[↑ к оглавлению](#оглавление) • [↑ наверх](#техническое-задание-тз--chillhub-mvp)

## 8. Admin UI/API (server/cmd/admin + server/admin_ui)
Админ-панель доступна на `:55777/admin` (dev). Ниже функциональность по разделам и спецификация API.

### 8.1. Игры
- Загрузка версии (ZIP):
  - `POST /admin/upload` (может быть оставлен для обратной совместимости; рекомендуем `uploadStream`).
  - `POST /admin/uploadStream` — потоковый режим с прогрессом (NDJSON), используется UI.
    - FormData: `kind=game|launcher`, `gameId?` (для `game`), `version`, `zip`, `updateLatest=0|1`.
    - Ответ: последовательность NDJSON событий: `start`, `zipSaved`, множество `unzip`, затем `composeStart`, множество `file`, `done` или `error`.
- Список версий: `GET /admin/list?gameId={id}` → `{ items: [{version}], latest }`.
- Активация latest: `POST /admin/activate?gameId={id}&version={v}` (создает/обновляет `latest.json`).
- Удаление версии: `POST /admin/deleteVersion?gameId={id}&version={v}` (удаляет манифест и папку `content/{gameId}/{version}`; корректирует `latest.json`).
- Подсказка следующей версии: (убрана). Версию вводит администратор вручную в UI.
- Сборка манифеста из уже разложенных файлов (compose) — убрана в пользу загрузки ZIP.
- Предпросмотр файлов версии в UI: чтение `GET /manifests/{gameId}/{version}.json` и древовидный показ.
- Редактор реестра игр (динамический список):
  - `GET /admin/games` — читает/создает `content/manifests/_registry/games.json`.
  - `POST /admin/games/save` — сохраняет `{ items: [{ gameId, title, exeRelativePath, iconUrl }] }`.
  - `GET /admin/games/scan` — ищет новые игры по папкам манифестов (исключая `_registry`, `launcher`, `repo`).
  - Загрузка иконки игры: `POST /admin/games/icon/upload` (multipart: `gameId`, `file`) → сохраняет `content/manifests/{gameId}/icon.png` и возвращает URL.

Аутентификация (cookie + JWT, `server/cmd/admin/auth.go`):
- `POST /admin/api/auth/login` — тело: `{ username, password }` или `application/x-www-form-urlencoded`.
  - Устанавливает cookies: `access_token` (HttpOnly), `refresh_token` (HttpOnly), `csrf_token` (не HttpOnly).
- `POST /admin/api/auth/logout` — очистка cookies.
- `POST /admin/api/auth/refresh` — обновление access/refresh + CSRF.
- `GET /admin/api/auth/me` — `{ user }` при валидной сессии.
- `GET /admin/api/auth/verify` — 200/401 для `auth_request` в nginx.

CSRF: для методов `POST/PUT/PATCH/DELETE` требуется заголовок `X-CSRF-Token` с тем же значением, что в cookie `csrf_token`.

### 8.2. Лаунчер
- Просмотр текущего манифеста лаунчера: UI читает `GET /manifests/launcher/latest.json` и затем `GET /manifests/launcher/{version}.json`.
- Загрузка версии лаунчера (ZIP): `POST /admin/upload` с `FormData(kind=launcher, version, zip, updateLatest=0|1)`.

### 8.3. Новости
- Индекс по области: `GET /admin/news/list?scope=launcher|game&gameId={optional}`.
- Получить новость: `GET /admin/news/get?scope=...&gameId=...&slug=...` → `{ markdown, published, coverUrl }`.
- Сохранить: `POST /admin/news/save` (multipart: `scope, gameId?, slug, markdown, coverUrl?, published?`) — сохраняет, обновляет meta и пересобирает индекс.
- Удалить: `DELETE /admin/news/delete?scope=...&gameId=...&slug=...`.
- Публикация: `POST /admin/news/publish` (`scope, gameId?, slug, published=true|false`).
- Предпросмотр: `POST /admin/news/preview` — `{ listHtml, contentHtml }`.
- Пересборка индекса: `POST /admin/news/rebuild?scope=...&gameId=...`.
- Ассеты: базовая директория `content/news/assets`.
  - Листинг: `GET /admin/news/assets?path={rel}&q={opt}&dirsOnly={0|1}`.
  - Создать папку: `POST /admin/news/assets/mkdir`.
  - Переименовать: `POST /admin/news/assets/rename`.
  - Удалить: `POST /admin/news/assets/delete`.
  - Загрузка файла: `POST /admin/news/assets/upload` (конвертация изображений, ресайз до 1080 минимальной стороны; GIF/WEBP → WEBP при наличии ffmpeg).
  - Загрузка по URL: `POST /admin/news/assets/uploadByUrl` (та же обработка, имя подбирается безопасно).

- Здоровье сервиса: `GET /admin/health` → `ok`. В продакшене публичные вызовы UI обращаются к `/admin/api/*` (та же логика, зеркальные хендлеры зарегистрированы в бэкенде).

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

## 9. Клиент (launcher/)

[↑ к оглавлению](#оглавление) • [↑ наверх](#техническое-задание-тз--chillhub-mvp)
- Технологии: C# WPF (.NET 8), NSIS инсталлятор.
- Конфигурация (упрощенно): `ConfigService.Current` — содержит `ApiBaseUrl`, `GamesPath`, `DownloadThreads` и пр. (см. код клиента).
- Self-update:
  - При старте открывается окно `UpdateWindow.xaml.cs`, которое:
    - Читает `GET {ApiBase}/manifests/launcher/latest.json`.
    - Загружает манифест `{ApiBase}/manifests/launcher/{version}.json` и сравнивает по хешам локальные файлы лаунчера (SHA-256 и Blake3, если заданы).
    - При необходимости скачивает в `%TEMP%/ChillHub/SelfUpdate/{version}` только отличающиеся файлы (через `SimpleSyncService` план/дифф).
    - Применение: генерирует `apply-update.cmd` (robocopy избранных файлов, создание пустых папок) и перезапускает приложение. Логи применения — `apply-update.log` в temp-папке.
    - DEV-флаг: `YL_DEV_SKIP_SELF_UPDATE=1` пропускает проверку самообновления (см. `UpdateWindow.xaml.cs`).
- Игры:
  - Стартовая страница `HomePage.xaml.cs`:
    - Грузит список игр: `GET {ApiBase}/api/games`.
    - Для выбранной игры подгружает список версий: `GET {ApiBase}/api/games/{gid}/builds` и новости: `GET {ApiBase}/news/games/{gid}/index.json`.
    - Кнопка «Обновить» всегда ставит latest (если известен), иначе первую доступную версию.
    - План обновления формируется через `SimpleSyncService.PlanAsync(manifest, localRoot, contentBaseUrl)` где `contentBaseUrl = {ApiBase}/content/{gid}/{version}/files`.
    - Выполнение диффа: многопоточная загрузка (по умолчанию 8 потоков; ограничение 2–16), поддержка HTTP Range и возобновления `.part`, проверка хешей, перенос в целевой корень, удаление лишних файлов, очистка пустых директорий, создание пустых директорий из манифеста.
    - Проверка свободного места: только по итоговому объему диффа (`DriveInfo.AvailableFreeSpace >= TotalDownloadBytes`).
    - Создание ярлыка на рабочем столе после успешной установки (если указан `exeRelativePath`).
  - Запуск игры: по настройке `exeRelativePath` из реестра игр, путь строится как `{GamesPath}/{gameId}/{exeRelativePath}`.
- Новости в клиенте:
  - Лаунчер: `GET {ApiBase}/news/index.json`.
  - Игра: `GET {ApiBase}/news/games/{gameId}/index.json`.
  - Полный текст карточки открывается по `*.md` URL (роутинг в клиенте).

## 10. Содержимое контента (content/)
- Манифесты: `content/manifests/{gameId}/{version}.json`, `latest.json`.
- Файлы версий: `content/content/{gameId}/{version}/files/...` (клиент формирует прямые URLs по этому корню).
- Новости:
  - Лаунчер: `content/news/index.json`, `content/news/{slug}.md`.
  - По игре: `content/news/games/{gameId}/index.json`, `content/news/games/{gameId}/{slug}.md`.
  - Ассеты: `content/news/assets/**`.
- Реестр игр: `content/manifests/_registry/games.json` (источник Public API).

## 11. Инсталлятор (NSIS)
- Per-user инсталляция (`RequestExecutionLevel user`).
- Файлы лаунчера берутся из `launcher/ChillHub/bin/Debug/net8.0-windows/*` (настроить для Release при продакшен сборке).
- Создаются ярлыки в меню и на рабочем столе.
- Создаются папки игр (`D:\Games\ChillHub` или `C:\Games\ChillHub`).
- Uninstall не удаляет пользовательские данные/контент (только ярлыки и записи реестра лаунчера).

## 12. Nginx (общее)
Конфигурация prod находится в `deploy/launcher.conf` и включает:
- Хосты: `launcher.samoy.love` (основной), `samoy.love` (заглушка), дефолтный сервер закрывает прочие хосты `return 444`.
- HTTP→HTTPS редирект для обоих доменов.
- Маршрутизация:
  - `/api/*` → `http://127.0.0.1:55700` (передаются `X-Forwarded-*`).
  - `/admin/api/*` → `http://127.0.0.1:55777` (1:1, без переписывания пути).
  - `/admin/` и `/admin/ui/*` — статика из `/var/www/launcher/admin_ui/` (точный матч на `/admin/` отдает `admin.html`).
  - `/content/*`, `/manifests/*`, `/news/*` — статика из `/var/www/launcher/...`.
  - `/assets/*` — `try_files` с приоритетом ассетов лендинга и fallback на новости (внутренний `@news_assets_fallback`), чтобы избежать ограничения смешивания `alias`/`root`.
- Добавление `X-Forwarded-Proto`, `X-Forwarded-Host`, `X-Forwarded-Port` — для корректного `baseURL()` в Public API.
- Для стриминга загрузок ZIP в админке отключены буферизации (`proxy_buffering off`, `proxy_request_buffering off`) и увеличены таймауты.
- На dev API/UI (`server/cmd/api`, `server/cmd/admin`) сами отдают статику для удобства локального запуска; в prod статику отдает nginx.

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
- Нет клиентских логов по умолчанию. Серверные логи — стандартный вывод (`httpx.Logging` в Public API, `adminLoggingMiddleware` в Admin API).
- Для отладки клиентские логи можно явно включить переменной окружения `CHILLHUB_CLIENT_LOG=1` (по умолчанию выключено).
- Подпись манифестов Ed25519 — в планах; поле `signature` присутствует в формате, но может быть пустым.
- Генерация Blake3-хеша сервером не реализована в `generate-manifest.ps1` (может быть пустым). Клиент поддерживает проверку, если значение присутствует.
- Защита админки (аутентификация/авторизация) — вне текущего MVP (добавить на проде).

## 15. Сценарии и флоу
- Публикация новой версии игры:
  1) Собрать ZIP с содержимым `{version}/files/...`.
  2) В админке во вкладке «Игры» выбрать `gameId`, `version`, загрузить ZIP с чекбоксом «Обновить latest» при необходимости.
  3) Проверить список версий (`/admin/list?gameId=`), при необходимости активировать latest отдельной кнопкой.

- Публикация новой версии лаунчера:
  1) Вкладка «Лаунчер»: указать `version`, загрузить ZIP; опционально обновить latest.
  2) Клиенты при запуске проверят манифест и обновятся по диффу.

- Управление списком игр:
  1) Вкладка «Игры (редактирование)»: 
     - «Обновить» — подтянет новые `gameId` из папок манифестов.
     - «Добавить» — вручную добавляет строку.
     - «Сохранить» — записывает `content/manifests/_registry/games.json`.
  2) Указать `exeRelativePath` и `iconUrl` для лучшего UX клиента.

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
- Подпись манифестов (Ed25519) end-to-end, верификация в клиенте.
- Авторизация админ-панели (OIDC/JWT), аудит действий.
- Поддержка дифф-патчей по блокам/битторрент/компрессии.
- Telemetry/серверные метрики загрузок, квоты, ограничение скорости.
- Автогенерация Blake3 при сборке контента.
- CI/CD интеграция, релизные сборки (Release) и подпись кода/инсталлятора.

## 18. Ссылки на ключевые файлы
- Public API: `server/cmd/api/main.go`
- Admin API: `server/cmd/admin/main.go`
- Админ UI: `server/admin_ui/admin.html`, `server/admin_ui/admin.js`
- Клиент: 
  - Обновление лаунчера: `launcher/ChillHub/UpdateWindow.xaml.cs`
  - Синхронизация файлов: `launcher/ChillHub/Core/Sync/SimpleSyncService.cs`
  - Главная страница: `launcher/ChillHub/Pages/HomePage.xaml.cs`
- Инсталлятор: `scripts/installer.nsi`
- Nginx (prod): `deploy/launcher.conf`
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

### 20.1. Требование паритета с `scripts/deploy.sh`
`deploy.yml` обязан обеспечивать ту же функциональность, что и `scripts/deploy.sh`:
- Сборка Go-бинариев для linux/amd64 и раскладка в `/opt/chillhub`.
- Синхронизация статик: `landing/` → `/var/www/site`, `server/admin_ui/` → `/var/www/launcher/admin_ui/`.
- Установка `deploy/launcher.conf` и перезагрузка nginx с валидацией `nginx -t`.
- Перезапуск `chillhub-api.service`, `chillhub-admin.service` и `daemon-reload`.
- Опциональная генерация systemd drop-in с переменными окружения из GitHub Secrets (`JWT_SECRET`, `ADMIN_USER`, `ADMIN_PASSWORD_BCRYPT` или `ADMIN_PASSWORD_PLAIN`, `COOKIE_DOMAIN`, `COOKIE_SECURE`).
- Опциональная синхронизация внешней папки установщиков `DOWNLOADS_DIR` → `/var/www/site/downloads` (если секрет задан и каталог существует на сервере).
- Послеукладочные смоук‑тесты (Admin UI, Admin API health, лендинг, статика), с диагностикой при сбоях (nginx/systemd/journalctl).

Этот паритет реализован в `.github/workflows/deploy.yml` (см. шаг «Deploy on server (SSH)»).

## 21. Деплой на сервер (ручной)
См. также сводную таблицу шагов в `README.md` → раздел «[Карта шагов: автодеплой vs вручную](README.md#карта-шагов-автодеплой-vs-вручную)».
1) Подготовка сервера: `nginx`, `rsync`, `ufw`, `certbot`.
2) Создать каталоги: `/var/www/site`, `/var/www/launcher/{content,manifests,news,admin_ui}`, `/opt/chillhub`.
3) Разместить `deploy/launcher.conf` в `/etc/nginx/sites-available/launcher.conf`, включить symlink в `sites-enabled`, проверить `nginx -t`, `systemctl reload nginx`.
4) Скопировать только админ-UI статику в `var/www/launcher/admin_ui`. Каталоги `manifests/content/news` не трогать — наполняются через Admin UI.
5) Собрать/скопировать бинарники в `/opt/chillhub`.
6) Установить systemd сервисы, `daemon-reload`, `enable`, `restart`.
7) Выписать сертификаты LE.

## 22. Локальная разработка и автотесты деплоя
Подробные инструкции по локальному запуску и флагам вынесены в `scripts/README.md` (раздел про `run-dev.ps1`).

## 23. Безопасность секретов
- SSH-ключи хранятся в GitHub Secrets. На сервере должен быть установлен публичный ключ пользователя `ubuntu`.
- В репозитории не хранить приватные ключи и пароли.

---
Это ТЗ агрегирует явные и неявные требования из репозитория и предыдущих обсуждений. При изменениях реализации требуется синхронизировать документ.

## 24. Выявленные проблемы и рекомендации

- **Маршруты и путаница путей**
  - Фактический бэкенд обслуживает Admin API под `/admin/*`, но в проде публичный префикс `/admin/api/*`. Это решено Nginx‑проксированием и клиентским шимом (`server/admin_ui/admin.js`). Рекомендация: в коде бэкенда зарезервировать и обрабатывать префикс `/admin/api/*` напрямую, чтобы убрать прослойку‑шим и снизить риск конфузий.

- **Безопасность админки**
  - Используется одна учётная запись из ENV (`ADMIN_USERNAME`/`ADMIN_PASSWORD_BCRYPT`) — этого достаточно. Ограничения по IP и rate‑limit в рамках этого раздела не требуются. Логирование событий входа — да. Поддержка внешнего IdP (OIDC) — не требуется.
  - CSRF реализован (cookie `csrf_token` + заголовок). Проверить и выставить прод‑значения `SameSite`/`Secure` через ENV (`COOKIE_DOMAIN`, `COOKIE_SECURE=true`).

- **CORS в Public API** (`server/internal/httpx/httpx.go`)
  - Разрешено `*`. Это допустимо для текущих требований; дополнительных ограничений не требуется.

- **Контентная синхронизация в CI/CD**
  - Ранее workflow разворачивал `content/` в прод (риск потери данных). Исправлено: `.github/workflows/deploy.yml` синхронизирует только `landing/`, `admin_ui/`, бинарии и конфиги.

- **Проверка подписи манифестов**
  - Подпись манифестов не используется, поле `signature` удалено; контроль целостности по хешам файлов достаточен.

- **Клиент: обработка ошибок**
  - Много молчаливых `catch { }` в `ChillHub.Core` (например, `ConfigService`, `Logger`) скрывают причины сбоев. Рекомендация: централизованный логгер, явные уровни (Verbose по переменной окружения), улучшенные сообщения и диагностический режим.

- **Статика кеширования**
  - Для больших файлов (`/content/`) выставлен `no-cache`. Возможна оптимизация с ETag/Last-Modified и `Cache-Control: public, max-age=...` для версионных путей.

- **Линт и стиль**
  - `golangci-lint` включает "all" — оставить как есть (конфигурация уже настроена). `stylelint`: при необходимости использовать локальные исключения для Bootstrap.

- **Тесты**
  - Нет автотестов на генерацию манифестов/дифф/хеши. Рекомендация: добавить минимальные unit‑тесты в Go и C#.

## 25. Дорожная карта доработок (этапы)

### Этап 1 (высокий приоритет)
- Перевести бэкенд admin на единственный префикс `/admin/api/*` (задвоить текущие хендлеры под этим префиксом), оставить временно backward‑compat и затем удалить shim в `server/admin_ui/admin.js`.
- Добавить rate‑limit на `/admin/api/auth/login` на уровне Nginx (`limit_req`) — без изменений кода Go.
- Провести ревизию и проверку всей документации: схем запросов/ответов Public API и Admin API (включая NDJSON событий), зафиксировать недостающие поля и точные типы; синхронизировать `Documentation.md` и `README.md`.
- Улучшить DX деплоя/запуска (Windows):
  - Makefile цель `make dev`: запуск `api`, `admin`, WPF‑клиента через `scripts/run-dev.ps1` с параметрами (`-ContentRoot`, `-GamesPath`, `-Env`, `-SetClientConfig`, `-BuildServers`).
  - Makefile цель `make smoke`: локальные смоук‑проверки HTTP (health/UI/статика) и простые проверки клиента (чтение конфигурации, базовые вызовы API), с читаемыми отчётами.
  - Makefile цель `make deploy`/`make deploy-nobuild`: прокси к CI шагам, унификация команд вручную и в автодеплое.
  - Комфортные пресеты для частых сценариев: «чистый старт dev», «против prod API», «перезапуск всех процессов», «сборка серверов под Windows».
- Анти‑FP (AV/Defender), часть 1:
  - Подпись EXE/NSIS с таймштампом (может потребоваться платная сертификация кода; остальной процесс — бесплатный). Проверка подписи/таймштампа и быстрый Defender‑scan в CI.
  - Прописать версии/метаданные в `.csproj` и NSIS (VIAddVersionKey, BrandingText).

### Этап 2 (средний)
- Ввести ETag/Last‑Modified и корректные Cache‑Control для статик‑локаций Nginx (особенно для версионных путей контента).
- Единый «deploy dry‑run»: шаг в CI, который выполняет все команды, кроме реального копирования/рестартов (проверка `nginx -t`, smoke‑скриптов).
- Анти‑FP (AV/Defender), часть 2:
  - VirusTotal API (диагностически) для Release‑кандидатов (учесть rate‑limit; не блокировать релизы на первом этапе).
  - MSIX POC как альтернативная упаковка (опционально; без обязательной миграции).
- Клиентские логи и диагностика:
  - Единый логгер с уровнями (Error/Info/Verbose), включение Verbose по переменной окружения.
  - Улучшенные сообщения об ошибках (включая коды и краткие рекомендации), запись причины отказа при проверке хешей/свободного места/сетевых сбоях.
  - Отдельный диагностический отчёт по результатам self‑update (в temp), удобный для прикрепления к баг‑репортам.

### Этап 3 (средний/низкий)
- Тесты:
  - Go: unit/интеграционные тесты admin/api (загрузка ZIP, NDJSON, индексы новостей, ассеты).
  - C#: тесты плана/диффа/хешей, сценарии ошибок (мало места, сетевые сбои, несоответствие хеша), очистка временных файлов.
- Документация API: сгенерировать OpenAPI/Swagger для Public API.
- Автотесты деплоя как отдельный workflow для stage‑окружения (проверки: `nginx -t`, доступность статики и health endpoints).

<!-- Раздел 26 перенесён в 3.1 -->


## 27. План снижения ложных срабатываний антивирусов/Windows Defender (Launcher, Updater, Installer)

Ниже набор практик и задач для минимизации риска флагов SmartScreen/Defender и движков AV. Большинство шагов — бесплатные. Исключение: код‑подписание (Authenticode) может требовать платный сертификат. Меры интегрированы в дорожную карту (см. раздел 25) и помечены приоритетами.

### 27.1. Подпись кода и метаданные — [Высокий]
- Authenticode‑подпись всех исполняемых файлов: `ChillHub.exe`, `YourLauncher.Updater.exe`, а также NSIS‑инсталлятора.
  - Использовать Timestamp (RFC3161), например `http://timestamp.digicert.com`.
  - Долгосрочно: рассмотреть EV‑сертификат для ускоренного накопления репутации SmartScreen.
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
  - Проверку наличия подписи и валидного таймштампа на артефактах (Windows‑раннер).
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
  - Подпись EXE/NSIS с таймштампом; добавление FileVersion/ProductVersion/CompanyName.
  - Проверка подписи и таймштампа в CI; быстрый Defender‑scan.
- Этап 2:
  - VirusTotal check (диагностически) для Release; POC MSIX; формализация процесса апелляций false positive.
- Этап 3:
  - Автотесты поведения апдейтера (UAC, коды выхода, cleanup), статический анализ (аккуратно тюнить gosec и .NET‑анализаторы).

