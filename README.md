# ChillHub — README

## Оглавление
- [Обзор](#обзор)
- [Локальная разработка (Windows 11)](#локальная-разработка-windows-11)
  - [Зависимости](#зависимости)
  - [Клонирование](#клонирование)
  - [Контент (минимум)](#контент-минимум)
  - [Запуск (3 окна)](#запуск-3-окна)
  - [Admin UI: контент](#admin-ui-контент)
- [Скрипты: dev и инсталлятор](#скрипты-dev-и-инсталлятор)
- [Деплой на сервер (Ubuntu + nginx)](#деплой-на-сервер-ubuntu--nginx)
  - [Подготовка (1 раз)](#подготовка-1-раз)
  - [Раскладка артефактов](#раскладка-артефактов)
  - [systemd (1 раз)](#systemd-1-раз)
  - [Обновления](#обновления)
  - [Просмотр логов (systemd)](#просмотр-логов-systemd)
- [Полезные ссылки и файлы](#полезные-ссылки-и-файлы)
- [Примечания по безопасности и качеству](#примечания-по-безопасности-и-качеству)

## Обзор
- ChillHub — лаунчер для Windows 10–11 для кооператива и моддинга.
- Обновления устанавливаются как дифф: скачиваются только изменившиеся файлы, конечная папка игры у пользователя становится точной копией серверной версии.
- Новости лаунчера и игр — Markdown + изображения/ассеты. Показываются в клиенте.
- Сервер на Golang + nginx:
  - Public API — выдаёт список игр, версии, манифесты, новости (JSON/статик).
  - Admin API — приём ZIP‑сборок, активация `latest`, редактирование реестра игр, управление новостями и ассетами.
- Админ‑панель (web) даёт UI для всех административных операций (загрузка версий, правка реестра, новости/ассеты).

Основные директории:
- `server/` — Go: `cmd/api` (public) и `cmd/admin` (admin), плюс статика для dev.
- `launcher/` — C# WPF лаунчер.
- `landing/` — статический лендинг (отдаётся на корне домена в проде).
- `content/` — манифесты, файлы версий, новости и их ассеты.
- `deploy/` — конфиги nginx (`deploy/launcher.conf`), systemd юниты.
- `scripts/` — вспомогательные скрипты (локальный запуск, сборка инсталлятора, утилиты).

Доменная схема (prod):
- `https://launcher.samoy.love`
  - `/` — лендинг из `landing/`.
  - `/api/*` → Public API (`127.0.0.1:55700`).
  - `/admin/api/*` → Admin API (`127.0.0.1:55777/admin/*`).
  - `/admin/`, `/admin/ui/*` — админ‑UI статика.
  - `/content/*`, `/manifests/*`, `/news/*` — статика контента.
  - `/assets/*` — ассеты лендинга (без fallback). 

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

### Запуск (3 окна)
Скрипт поднимет API, Admin и клиент (WPF) в отдельных окнах и пропишет клиенту нужный `ApiBaseUrl`.
```powershell
# локальная среда + запись клиентского ApiBaseUrl
.\scripts\run-dev.ps1 -Env local -SetClientConfig -ContentRoot (Resolve-Path .\content)
```
Проверка:
- API: http://localhost:55700/api/games
- Admin UI: http://localhost:55777/admin

Подсказки управления:
- В управляющей консоли `run-dev.ps1` нажмите `r` (или русскую `к`) → рестарт всех процессов.
- Нажмите `q` → корректное завершение и освобождение портов.

### Admin UI: контент
- Откройте `http://localhost:55777/admin`.
- Загрузите ZIP сборки игры/лаунчера (вкладка «Игры»/«Лаунчер»), при необходимости активируйте `latest`.
- Отредактируйте реестр игр (вкладка «Игры (редактирование)»).
- Создайте/отредактируйте новости, загрузите/сожмите изображения (вкладка «Новости», «Ассеты»).

---

## Скрипты: dev и инсталлятор

### scripts/run-dev.ps1 — локальная разработка (3 окна)
Скрипт запускает три процесса в отдельных окнах: Public API (`server/cmd/api`), Admin (`server/cmd/admin`) и клиент WPF (`launcher/ChillHub`). Умеет обновлять клиентскую конфигурацию (`%LOCALAPPDATA%/ChillHub/config.json`).

- Параметры:
  - `-ContentRoot <path>` — путь к директории `content/` (для dev-статик и API).
  - `-GamesPath <path>` — локальная папка установки игр (пробрасывается клиенту как `ChillHub_GAMES_PATH`).
  - `-Env local|prod` — выбирает `ApiBaseUrl` для клиента (`local` по умолчанию: `http://localhost:55700`; `prod`: `https://launcher.samoy.love`).
  - `-SetClientConfig` — записать/обновить `%LOCALAPPDATA%\ChillHub\config.json` (GamesPath, ApiBaseUrl, др.).
  - `-BuildServers` — перед запуском собрать dev-бинарии Go-серверов под Windows.

- Примеры запуска:

```powershell
# Стандартный запуск в локальной среде, с записью конфигурации клиента
./scripts/run-dev.ps1 -Env local -SetClientConfig -ContentRoot (Resolve-Path ./content)

# Указать путь к папке игр вручную
./scripts/run-dev.ps1 -ContentRoot (Resolve-Path ./content) -GamesPath 'D:\Games\ChillHub' -SetClientConfig

# Предварительно собрать Go-серверы (быстрее стартует), затем запустить
./scripts/run-dev.ps1 -BuildServers -ContentRoot (Resolve-Path ./content) -SetClientConfig
```

- Управление во время работы:
  - Нажмите `r` или русскую `к` + Enter — перезапуск всех трёх процессов.
  - Нажмите `q` + Enter — корректное завершение (освобождает порты, закрывает клиент).

- Требования: установлен Go 1.22+, .NET 8 SDK. Скрипт рассчитан на Windows (PowerShell).

---

### scripts/build-installer.ps1 — сборка инсталлятора (NSIS)
Скрипт выполняет restore и build/publish C# проекта лаунчера, затем компилирует NSIS-установщик (`ChillHub-Setup.exe`) по скрипту `scripts/installer.nsi`.

- Параметры (с разумными значениями по умолчанию):
  - `-Publish` — вместо `dotnet build` выполнит `dotnet publish` (self-contained по умолчанию).
  - `-Configuration <Debug|Release>` — конфигурация сборки (`Release` по умолчанию).
  - `-Csproj <path>` — путь к csproj лаунчера (`launcher/ChillHub/ChillHub.csproj`).
  - `-Installer <path>` — путь к NSIS-скрипту (`scripts/installer.nsi`).
  - `-MakensisPath <path>` — путь к `makensis.exe` (если не в PATH; можно указать директорию установки NSIS).
  - `-Runtime <RID>` — таргет-рантайм для publish (по умолчанию `win-x64`).
  - `-SelfContained` — собирать self-contained publish (включает runtime) — включено по умолчанию; снимите для framework-dependent.
  - `-NoCompress` — собрать инсталлятор без сжатия (быстро для dev).

- Примеры запуска:

```powershell
# Быстрый dev-инсталлятор без сжатия (предварительно просто build)
./scripts/build-installer.ps1 -Configuration Debug -NoCompress

# Полная публикация self-contained и сборка инсталлятора (Release)
./scripts/build-installer.ps1 -Publish -Configuration Release -Runtime win-x64

# Указать явный путь к makensis.exe, если не добавлен в PATH
./scripts/build-installer.ps1 -MakensisPath 'C:\Program Files (x86)\NSIS\makensis.exe'
```

- Требования: установлен .NET 8 SDK и NSIS 3.x. Если `makensis` не найден в PATH, укажите `-MakensisPath` или директорию установки NSIS.

---

## Деплой на сервер (Ubuntu + nginx)

Предполагаем VPS с Ubuntu, пользователь `ubuntu`, домен `launcher.samoy.love` указывает A‑записью на IP сервера.

### Подготовка (1 раз)
```bash
sudo apt update && sudo apt install -y nginx rsync
sudo apt install -y certbot python3-certbot-nginx

sudo mkdir -p /var/www/site
sudo mkdir -p /var/www/launcher/{content,manifests,news,admin_ui}
sudo mkdir -p /opt/chillhub

# Клонируйте репозиторий (в домашнюю директорию пользователя)
git clone https://github.com/tr0llex/Launcher-Project.git ~/Launcher-Project || true

# Сертификаты (после настройки DNS A-записей)
sudo certbot --nginx -d launcher.samoy.love

# Установите nginx-конфиг из репозитория
sudo install -m 0644 ~/Launcher-Project/deploy/launcher.conf /etc/nginx/sites-available/launcher.conf
sudo ln -sf /etc/nginx/sites-available/launcher.conf /etc/nginx/sites-enabled/launcher.conf
sudo nginx -t && sudo systemctl reload nginx

# Firewall
sudo ufw allow OpenSSH && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp
sudo ufw deny 55700/tcp && sudo ufw deny 55777/tcp
sudo ufw enable
```

### Раскладка артефактов
```bash
# Выполняйте на сервере (SSH), из домашней директории пользователя,
# где уже клонирован репозиторий в ~/Launcher-Project

cd ~/Launcher-Project

# 1) Лендинг → /var/www/site (копирование/синхронизация, без перемещения)
sudo rsync -a --delete ./landing/ /var/www/site/

# 2) Контент и Admin UI → /var/www/launcher/* (копирование/синхронизация)
sudo rsync -a --delete ./content/manifests/ /var/www/launcher/manifests/
sudo rsync -a --delete ./content/content/   /var/www/launcher/content/
sudo rsync -a --delete ./content/news/      /var/www/launcher/news/
sudo rsync -a --delete ./server/admin_ui/   /var/www/launcher/admin_ui/

# 3) Бинарии (сборка на сервере внутри модуля `server/` и установка)
cd ./server
go build -o ../api   ./cmd/api
go build -o ../admin ./cmd/admin
cd -
sudo install -m 0755 ./api   /opt/chillhub/api
sudo install -m 0755 ./admin /opt/chillhub/admin
```

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

#### Частые ошибки (troubleshooting)

- **/admin отдаёт 404**
  - Убедитесь, что установлен актуальный конфиг nginx `deploy/launcher.conf` и перезагружен nginx.
  - В конфиге присутствует точное правило `location = /admin/ { alias /var/www/launcher/admin_ui/admin.html; }`, которое отдаёт файл UI. Все прочие `/admin/*` проксируются в Admin API.

- **Лаунчер при старте пишет 404 на GET https://launcher.samoy.love/manifests/launcher/latest.json**
  - Клиент берёт `latest.json` по пути `manifests/launcher/latest.json` (см. `launcher/ChillHub/UpdateWindow.xaml.cs`). Если файла нет — будет 404.
  - Создайте его одним из способов:
    - Через Admin UI: вкладка «Лаунчер» → загрузите ZIP новой версии и оставьте флаг «Обновить latest». Это создаст `content/manifests/launcher/<version>.json` и `content/manifests/launcher/latest.json` и положит файлы в `content/content/launcher/<version>/files/`.
    - Вручную (для первичной инициализации):
      1) Скопируйте манифест версии в `/var/www/launcher/manifests/launcher/<version>.json`.
      2) Создайте `/var/www/launcher/manifests/launcher/latest.json` со структурой `{ "version": "<version>" }`.
      3) Убедитесь, что файлы самой версии лежат в `/var/www/launcher/content/launcher/<version>/files/`.
  - После выкладки манифестов перезагрузка nginx не требуется; проверьте по URL в браузере, что `latest.json` открывается без 404.

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

Подсказки:
- Если логов нет, убедитесь, что сервисы активированы и запущены: `sudo systemctl enable --now chillhub-api.service chillhub-admin.service`.
- Для фильтрации по тексту: `journalctl -u chillhub-api.service | grep ERROR`.
- Конфигурации unit-файлов лежат в `deploy/systemd/`; при изменении не забудьте `sudo systemctl daemon-reload`.

---

## Полезные ссылки и файлы
- Конфиг nginx (prod): `deploy/launcher.conf`
- Systemd юниты: `deploy/systemd/`
- Локальный запуск: `scripts/run-dev.ps1`
- CI/CD (ручной запуск из GitHub Actions): `.github/workflows/deploy.yml`

## Примечания по безопасности и качеству
- Серверные порты приложений 55700/55777 закрыты внешнему миру, доступны только через nginx.
- Клиент постепенно будет получать подпись кода/инсталлятора (вне MVP).
- Манифесты могут содержать хеши Blake3 и SHA‑256; клиент проверяет значения, если присутствуют.
