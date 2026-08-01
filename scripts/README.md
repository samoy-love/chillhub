# Скрипты разработки

Содержание:
- [`run-dev.ps1` — локальная разработка (3 окна)](#run-devps1--локальная-разработка-3-окна)
- [`deploy-win.ps1` — удалённый деплой с Windows](#deploy-winps1--удалённый-деплой-с-windows)
- [Прочие скрипты](#прочие-скрипты)
- [Проверки перед пушем](#проверки-перед-пушем)

## `run-dev.ps1` — локальная разработка (3 окна)
Скрипт запускает три процесса в отдельных окнах: Public API (`server/cmd/api`), Admin (`server/cmd/admin`) и клиент WPF (`launcher/ChillHub`). Умеет обновлять клиентскую конфигурацию (`%APPDATA%/ChillHub/config.json`). Требования: Go 1.22+, .NET 8 SDK. Windows PowerShell/PowerShell 7.

### Параметры
- `-ContentRoot <path>` — путь к директории `content/` (для dev-статик и API).
- `-GamesPath <path>` — локальная папка установки игр (пробрасывается клиенту как `ChillHub_GAMES_PATH`).
- `-Env local|prod` — выбирает `ApiBaseUrl` для клиента (`local` по умолчанию: `http://localhost:55700`; `prod`: `https://launcher.samoy.love`).
- `-SetClientConfig` — записать/обновить `%APPDATA%\ChillHub\config.json` (GamesPath, ApiBaseUrl, др.).
- `-BuildServers` — перед запуском собрать dev‑бинарии Go‑серверов под Windows.
- `-ResetAdminAuth` — перед запуском сгенерировать новый пароль администратора (случайный), посчитать `ADMIN_PASSWORD_BCRYPT`, сгенерировать `JWT_SECRET`, вывести их значения в консоль и перезапустить процессы с новыми переменными в текущей сессии.

### Примеры запуска
```powershell
# Стандартный запуск в локальной среде, с записью конфигурации клиента
./scripts/run-dev.ps1 -Env local -SetClientConfig -ContentRoot (Resolve-Path ./content)

# Указать путь к папке игр вручную
./scripts/run-dev.ps1 -ContentRoot (Resolve-Path ./content) -GamesPath 'D:\Games\ChillHub' -SetClientConfig

# Предварительно собрать Go‑серверы (быстрее стартует), затем запустить
./scripts/run-dev.ps1 -BuildServers -ContentRoot (Resolve-Path ./content) -SetClientConfig

# Сбросить локальные учётные данные админа перед стартом (новый пароль и JWT секрет)
./scripts/run-dev.ps1 -Env local -SetClientConfig -ContentRoot (Resolve-Path ./content) -ResetAdminAuth
```

### Управление во время работы
- Нажмите `r` или русскую `к` + Enter — перезапуск всех трёх процессов.
- Нажмите `p` или русскую `з` + Enter — сбросить пароль администратора и `JWT_SECRET`, затем перезапустить процессы.
- Нажмите `q` или русскую `й` + Enter — корректное завершение (освобождает порты, закрывает клиент).

### Примечания
- Для запуска клиента против прод‑сервера задайте `-Env prod` (пропишет `ApiBaseUrl` в `%APPDATA%\ChillHub\config.json`).
- Конфиг клиента живёт в `%APPDATA%\ChillHub`, а НЕ в `%LOCALAPPDATA%\ChillHub`: последний — это каталог установки лаунчера, и конфиг оттуда попадал в манифест обновления, давая бесконечный цикл самообновления. Локальный bcrypt‑хэш админа (`admin.secret.json`) при этом по‑прежнему лежит в `%LOCALAPPDATA%\ChillHub` — это отдельный файл dev‑скрипта.
- Логи клиента: `%APPDATA%\ChillHub\logs\client.log` (ротация, до 3 архивов). Пишутся по умолчанию; выключить — `CHILLHUB_CLIENT_LOG=0`.
- Проверка доступности после старта:
  - API: http://localhost:55700/api/games
  - Режим техработ: http://localhost:55700/api/maintenance (всегда 200)
  - Admin UI: http://localhost:55777/admin
- Полезные переменные окружения для dev:
  - `YL_DEV_SKIP_SELF_UPDATE=1` — пропустить проверку самообновления лаунчера.
  - `CHILLHUB_MANIFEST_STRICT=1` — строгий режим проверки подписи манифестов (по умолчанию действует режим совместимости).
  - `API_RATE_LIMIT=0` — отключить rate limit публичного API на время нагрузочных проверок.

---

## `deploy-win.ps1` — удалённый деплой с Windows

PowerShell‑скрипт для запуска прод‑деплоя с локальной Windows‑машины на удалённый VPS. Повторяет шаги CI (сборка Go для linux/amd64, упаковка статики, загрузка по SCP, применение на сервере, смоук‑тесты).

Требования:
- Windows: установлен Go, OpenSSH клиент (`ssh`, `scp`).
- Сервер (Ubuntu): `rsync`, `nginx`, `systemd`, `sudo`.

Параметры:
- `-Host <host>` — адрес сервера (обязательный)
- `-User <user>` — SSH‑пользователь (обязательный)
- `-KeyPath <path>` — путь к приватному SSH‑ключу (обязательный)
- `-Branch <name>` — ветка для сборки (по умолчанию `main`)
- `-DownloadsDir <path>` — внешняя директория установщиков (опционально)
- `-JwtSecret <val>` — JWT секрет (опционально)
- `-AdminUser <name>` — логин администратора (по умолчанию `admin`)
- `-AdminPasswordBcrypt <hash>` — bcrypt‑хэш пароля админа (предпочтительно)
- `-AdminPasswordPlain <pass>` — открытый пароль (если указан, bcrypt будет рассчитан на сервере)
- `-CookieDomain <host>` — домен cookie (по умолчанию `launcher.samoy.love`)
- `-CookieSecure <true|false>` — Secure флаг (по умолчанию `true`)

Примеры (PowerShell):

```powershell
# Минимальный запуск
./scripts/deploy-win.ps1 -Host your.vps.host -User ubuntu -KeyPath "C:\Users\you\.ssh\id_rsa"

# С секретами и bcrypt
./scripts/deploy-win.ps1 -Host your.vps.host -User ubuntu -KeyPath "C:\Users\you\.ssh\id_rsa" `
  -JwtSecret "base64-48bytes" -AdminUser admin -AdminPasswordBcrypt "$2y$12$..." `
  -CookieDomain "launcher.samoy.love" -CookieSecure "true"

# С plain‑паролем (bcrypt посчитается на сервере)
./scripts/deploy-win.ps1 -Host your.vps.host -User ubuntu -KeyPath "C:\Users\you\.ssh\id_rsa" `
  -AdminUser admin -AdminPasswordPlain "YourStrongPassword"
```

Через Make (Windows):

```bash
make deploy-win HOST=your.vps.host USER=ubuntu KEY="C:/Users/you/.ssh/id_rsa"

make deploy-win HOST=your.vps.host USER=ubuntu KEY="C:/Users/you/.ssh/id_rsa" \
  BRANCH=main JWT="base64-48bytes" ADMIN_USER=admin ADMIN_BCRYPT="$2y$12$..." \
  COOKIE_DOMAIN=launcher.samoy.love COOKIE_SECURE=true DOWNLOADS_DIR="C:/data/downloads"
```

Замечания по безопасности:
- По возможности используйте `-AdminPasswordBcrypt` вместо `-AdminPasswordPlain`.
- При передаче plain‑пароля через командную строку он может попасть в историю/журналы — используйте с осторожностью.

---

## Прочие скрипты

| Скрипт | Где запускается | Назначение |
|---|---|---|
| `deploy.sh` | сервер / по SSH | полный деплой: сборка, статика, конфиг nginx, systemd, смоук‑тесты |
| `deploy-nginx.sh` | НА СЕРВЕРЕ, под root | идемпотентная раскладка nginx‑конфига в **свой** файл `/etc/nginx/sites-available/chillhub-launcher.conf` + симлинк, бэкап, `nginx -t` и откат при ошибке. Имя файла переопределяется переменной `SITE_NAME`. Чужие сайты на хосте не затрагиваются |
| `build-installer.ps1` | Windows | сборка NSIS‑инсталлятора (`installer.nsi`) |
| `bench-upload.ps1` | Windows | замеры скорости загрузки сборок в админку |
| `compress-images.ps1` | Windows | пакетное сжатие изображений |
| `run-admin.ps1` / `run-client.ps1` / `dev.ps1` / `env.ps1` | Windows | точечный запуск отдельных частей и настройка окружения |

Проверка nginx‑конфига без сервера — `sh deploy/nginx-check.sh` (настоящий `nginx -t`
в Docker на версии прода); подробности в `deploy/README.md`.

---

## Проверки перед пушем

```bash
cd server && go test ./...                               # серверные пакеты
dotnet test launcher/tests/ChillHub.Tests                # клиент: план/дифф, хеши, подпись
dotnet run --project updater/tests/ManifestPreserveCheck # петля самообновления
sh deploy/nginx-check.sh                                 # настоящий nginx -t в Docker
make lint                                                # web/go/dotnet линтеры
```

`ManifestPreserveCheck` падает, если в манифест лаунчера попали пользовательские
файлы (`config.json`, `launcher.version`) — это ровно та ситуация, из‑за которой
лаунчер начинает предлагать обновление при каждом запуске. Подробности —
`Documentation.md` §22.
