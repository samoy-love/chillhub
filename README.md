# ChillHub — README (Draft)

Этот файл — черновик обновленного README. Он объединяет продуктовый обзор, пошаговые инструкции для локальной разработки на Windows 11 и подробное руководство по развертыванию на сервере (How-To). После ревью можно заменить им основной `README.md`.

---

## Обзор продукта
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
  - `/assets/*` — try_files: сперва ассеты лендинга, затем fallback на новости.

Dev‑порты (локально): Public API `:55700`, Admin API `:55777`.

---

## Локальная среда (Windows 11) — разработка, дебаг, наполнение контента

### 1) Установите зависимости
- Go 1.22+ (добавьте в PATH)
- .NET 8 SDK
- Git
- Visual Studio 2022 (для WPF) или Visual Studio Code
- (Опционально) PowerShell 7 — подойдёт и Windows PowerShell

### 2) Клонируйте проект
```powershell
mkdir C:\Work; Set-Location C:\Work
git clone <repo_url> "Launcher Project"
Set-Location "Launcher Project"
```

### 3) Минимальный контент
- Примеры уже есть в `content/`.
- Для своей игры:
  - Манифесты: `content/manifests/<gameId>/{version}.json` и `latest.json`.
  - Файлы версии: `content/content/<gameId>/<version>/files/...`.
  - Новости: `content/news/` и `content/news/games/<gameId>/`.

### 4) Запуск всех компонентов одной командой (3 окна с логами)
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

### 5) Наполнение данными через Admin UI
- Откройте `http://localhost:55777/admin`.
- Загрузите ZIP сборки игры/лаунчера (вкладка «Игры»/«Лаунчер»), при необходимости активируйте `latest`.
- Отредактируйте реестр игр (вкладка «Игры (редактирование)»).
- Создайте/отредактируйте новости, загрузите/сожмите изображения (вкладка «Новости», «Ассеты»).

---

## Развертывание на сервер (Ubuntu + nginx) — How To

Предполагаем VPS с Ubuntu, пользователь `ubuntu`, домен `launcher.samoy.love` указывает A‑записью на IP сервера.

### 1) Базовая подготовка (один раз)
```bash
sudo apt update && sudo apt install -y nginx rsync
sudo apt install -y certbot python3-certbot-nginx

sudo mkdir -p /var/www/site
sudo mkdir -p /var/www/launcher/{content,manifests,news,admin_ui}
sudo mkdir -p /opt/chillhub

# Скопируйте deploy/launcher.conf в /etc/nginx/sites-available/launcher.conf
sudo ln -s /etc/nginx/sites-available/launcher.conf /etc/nginx/sites-enabled/launcher.conf || true
sudo nginx -t && sudo systemctl reload nginx

# Сертификаты (после настройки DNS A-записей)
sudo certbot --nginx -d launcher.samoy.love -d samoy.love

# Firewall
sudo ufw allow OpenSSH && sudo ufw allow 80/tcp && sudo ufw allow 443/tcp
sudo ufw deny 55700/tcp && sudo ufw deny 55777/tcp
sudo ufw enable
```

### 2) Загрузка и раскладка артефактов (ручной вариант)
```bash
# 1) Лендинг
scp -r landing/* ubuntu@<VPS>:/home/ubuntu/site/
ssh ubuntu@<VPS> "sudo rsync -a --delete /home/ubuntu/site/ /var/www/site/"

# 2) Контент и Admin UI
scp -r content/manifests ubuntu@<VPS>:/home/ubuntu/launcher_manifests/
scp -r content/content   ubuntu@<VPS>:/home/ubuntu/launcher_content/
scp -r content/news      ubuntu@<VPS>:/home/ubuntu/launcher_news/
scp -r server/admin_ui   ubuntu@<VPS>:/home/ubuntu/admin_ui/
ssh ubuntu@<VPS> "sudo rsync -a --delete ~/launcher_manifests/ /var/www/launcher/manifests/ && \
                  sudo rsync -a --delete ~/launcher_content/   /var/www/launcher/content/   && \
                  sudo rsync -a --delete ~/launcher_news/      /var/www/launcher/news/      && \
                  sudo rsync -a --delete ~/admin_ui/           /var/www/launcher/admin_ui/"

# 3) Бинарии (если собираете локально)
GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -o api ./server/cmd/api
GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -o admin ./server/cmd/admin
scp api admin ubuntu@<VPS>:/home/ubuntu/
ssh ubuntu@<VPS> "sudo install -m 0755 ~/api /opt/chillhub/api && sudo install -m 0755 ~/admin /opt/chillhub/admin"
```

### 3) systemd сервисы (один раз)
```bash
# (предварительно загрузите deploy/systemd/*.service в ~/deploy/systemd)
sudo install -m 0644 ~/deploy/systemd/chillhub-api.service   /etc/systemd/system/chillhub-api.service
sudo install -m 0644 ~/deploy/systemd/chillhub-admin.service /etc/systemd/system/chillhub-admin.service
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
