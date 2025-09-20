# Техническое задание (ТЗ) — ChillHub (MVP)

Версия документа: 1.1
Дата: 2025-09-20

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

Домены и маршрутизация (prod):
- `launcher.samoy.love` — боевой домен лаунчера:
  - `/` — лендинг из `landing/`.
  - `/api/*` — прокси на Public API (`127.0.0.1:55700`).
  - `/admin/api/*` — прокси на Admin API (`127.0.0.1:55777`), с маппингом на бэкенде `/admin/*`.
  - `/admin/` и `/admin/ui/*` — статика админки из `/var/www/launcher/admin_ui/`.
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
- Подпись манифестов: Ed25519. Публичный ключ зашит в клиент (план/заметки в `README.md`).
- Хеши файлов в манифесте: Blake3 (основной), SHA-256 (опционально). Клиент проверяет оба при наличии значений. На стороне админ-сервера хеши считаются при загрузке ZIP.
- Клиент не должен определяться как вирус: 
  - Использовать стандартные механики самообновления (robocopy, temp + перезапуск), без подозрительных техник.
  - Подписывать инсталлятор/EXE в будущих этапах (вне MVP).

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
  "emptyDirs": ["Saves", "Cache"],
  "signature": ""  
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
Базовые пути (dev):
- `/api/games` — список игр. Ответ: `{ items: GameInfo[] }`, где `GameInfo` включает `gameId`, `title`, `hasLatest`, `latestVersion?`, `manifestUrl?`, `exeRelativePath?`, `iconUrl?`. Источник игр — реестр или скан папок (`loadGames`).
- `/api/games/{gameId}` — информация по игре (`GameInfo`).
- `/api/games/{gameId}/versions/latest` — `{ gameId, version, manifestUrl }` либо `{ gameId, hasLatest:false }`.
- `/api/games/{gameId}/builds` — `{ gameId, items: string[] }` — список доступных версий (по *.json, кроме `latest.json`).
- Новости:
  - `/news/index.json` — индекс лаунчера, фильтруется по `published == true` (или включается, если поле отсутствует). См. `handleNewsIndex`.
  - `/news/games/{gameId}/index.json` — индекс по игре, аналогично фильтрация `published`. См. `handleGameNewsIndex`.
- Статика (для dev запуска `api`):
  - `/manifests/*` → `content/manifests/*`
  - `/content/*` → `content/content/*`
  - `/news/*` → `content/news/*`
  - `/assets/*` → `content/news/assets/*`

Примечание: Public API формирует список игр из реестра `content/manifests/_registry/games.json` (если есть), иначе сканирует папку `content/manifests/` за исключением `_registry`. См. `loadGamesFromRegistry()` и `loadGamesByScanning()` в `server/cmd/api/main.go`.

Примечание: Базовый URL строится с учетом `X-Forwarded-Proto` (http/https). См. `baseURL()`.

## 8. Admin UI/API (server/cmd/admin + server/admin_ui)
Админ-панель доступна на `:8081` (dev). Ниже функциональность по разделам:

### 8.1. Игры
- Загрузка версии (ZIP):
  - `POST /admin/upload` с `FormData(kind=game, gameId, version, zip, updateLatest=0|1)` — стандартный режим.
  - `POST /admin/uploadStream` — потоковый режим с прогрессом (NDJSON), используется UI. Параметры те же.
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

- Здоровье сервиса: `GET /admin/health` → `ok`.

UI админки: `server/admin_ui/admin.html` + `admin.js` (Bootstrap 5, темная тема). Кнопки «Собрать» и «Предложить следующую версию» удалены; используется загрузка ZIP и просмотр списков версий.

Сервер админки для dev также отдает статику, чтобы UI работал без nginx:
- `/manifests/*` → `content/manifests/*`
- `/news/*` → `content/news/*`
- `/assets/*` → `content/news/assets/*`

## 9. Клиент (launcher/)
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
  - `/admin/api/*` → `http://127.0.0.1:55777/admin/*` (проксирование с дописыванием сегмента `/admin/`).
  - `/admin/` и `/admin/ui/*` — статика из `/var/www/launcher/admin_ui/`. Прочие `/admin/*` (legacy вызовы UI) проксируются на `:55777`.
  - `/content/*`, `/manifests/*`, `/news/*` — статика из `/var/www/launcher/...`.
  - `/assets/*` — `try_files` с приоритетом ассетов лендинга и fallback на новости.
- Добавление `X-Forwarded-Proto`, `X-Forwarded-Host`, `X-Forwarded-Port` — для корректного `baseURL()` в Public API.
- Для стриминга загрузок ZIP в админке отключены буферизации (`proxy_buffering off`, `proxy_request_buffering off`) и увеличены таймауты.
- На dev API/UI (`server/cmd/api`, `server/cmd/admin`) сами отдают статику для удобства локального запуска; в prod статику отдает nginx.

Производственные директории:
- Лендинг: `/var/www/site` (копия `landing/`).
- Контент: `/var/www/launcher/{content,manifests,news,admin_ui}` (копия из `content/` и `server/admin_ui/`).
- Бинарники: `/opt/chillhub/{api,admin}`.

Сертификаты: Let’s Encrypt (рекомендуется `certbot --nginx -d launcher.samoy.love -d samoy.love`).

Firewall (пример UFW): открыть 80/443, закрыть 55700/55777 наружу.

VPS: `158.179.204.241 (Ubuntu, user: ubuntu)`.

Порты приложений: `:55700` (Public API), `:55777` (Admin API) — слушают loopback.

Systemd:
- `deploy/systemd/chillhub-api.service`
- `deploy/systemd/chillhub-admin.service`

GitHub Actions деплой: `.github/workflows/deploy.yml`.

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
- Local dev: `scripts/dev.ps1`

## 19. Домены и DNS
- `launcher.samoy.love` → A: `158.179.204.241` (Oracle VPS)
- `samoy.love` → может оставаться на текущих A; при переводе на VPS — также A: `158.179.204.241`.
- Почта: оставить текущие MX записи (Mail.ru), SPF/TXT/NS/CAA без изменений.

Рекомендуемые записи:
- A `launcher.samoy.love` = `158.179.204.241`, TTL 3600.
- (Опционально) A `samoy.love` = `158.179.204.241`, если нужен плейсхолдер с этого же сервера.

Let’s Encrypt выписывает сертификаты на оба домена.

## 20. CI/CD (GitHub Actions)
Workflow `.github/workflows/deploy.yml`:
- Триггер: только вручную (workflow_dispatch) через кнопку «Run workflow» в GitHub Actions.
- Шаги: checkout → setup-go → сборка бэкендов для linux/amd64 → упаковка артефактов (landing, content, admin_ui, nginx.conf, systemd) → SCP на сервер → SSH-скрипт раскладки (rsync в `/var/www/site`, `/var/www/launcher/...`, установка бинарей в `/opt/chillhub`, установка systemd и nginx-конфига, `nginx -t`, reload).
- Secrets: `SSH_HOST`, `SSH_USER`, `SSH_PRIVATE_KEY`.

## 21. Деплой на сервер (ручной)
1) Подготовка сервера: `nginx`, `rsync`, `ufw`, `certbot`.
2) Создать каталоги: `/var/www/site`, `/var/www/launcher/{content,manifests,news,admin_ui}`, `/opt/chillhub`.
3) Разместить `deploy/launcher.conf` в `/etc/nginx/sites-available/launcher.conf`, включить symlink в `sites-enabled`, проверить `nginx -t`, `systemctl reload nginx`.
4) Скопировать контент (rsync/scp) и админ-UI статику в `var/www`.
5) Собрать/скопировать бинарники в `/opt/chillhub`.
6) Установить systemd сервисы, `daemon-reload`, `enable`, `restart`.
7) Выписать сертификаты LE.

## 22. Локальная разработка
- Скрипт `scripts/dev.ps1`:
  - `-Env local|prod` — проставляет клиенту `ApiBaseUrl` (`%LOCALAPPDATA%/ChillHub/config.json`).
  - `-RunServers` — запускает `api` и `admin` (go run) с `CONTENT_ROOT` указывающим на `content/` из репозитория.
  - `-BuildServers` — сборка Windows dev бинарей.

## 23. Безопасность секретов
- SSH-ключи хранятся в GitHub Secrets. На сервере должен быть установлен публичный ключ пользователя `ubuntu`.
- В репозитории не хранить приватные ключи и пароли.

---
Это ТЗ агрегирует явные и неявные требования из репозитория и предыдущих обсуждений. При изменениях реализации требуется синхронизировать документ.
