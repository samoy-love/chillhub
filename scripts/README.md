# Скрипты разработки

## `run-dev.ps1` — локальная разработка (3 окна)
Скрипт запускает три процесса в отдельных окнах: Public API (`server/cmd/api`), Admin (`server/cmd/admin`) и клиент WPF (`launcher/ChillHub`). Умеет обновлять клиентскую конфигурацию (`%LOCALAPPDATA%/ChillHub/config.json`). Требования: Go 1.22+, .NET 8 SDK. Windows PowerShell/PowerShell 7.

### Параметры
- `-ContentRoot <path>` — путь к директории `content/` (для dev-статик и API).
- `-GamesPath <path>` — локальная папка установки игр (пробрасывается клиенту как `ChillHub_GAMES_PATH`).
- `-Env local|prod` — выбирает `ApiBaseUrl` для клиента (`local` по умолчанию: `http://localhost:55700`; `prod`: `https://launcher.samoy.love`).
- `-SetClientConfig` — записать/обновить `%LOCALAPPDATA%\ChillHub\config.json` (GamesPath, ApiBaseUrl, др.).
- `-BuildServers` — перед запуском собрать dev‑бинарии Go‑серверов под Windows.

### Примеры запуска
```powershell
# Стандартный запуск в локальной среде, с записью конфигурации клиента
./scripts/run-dev.ps1 -Env local -SetClientConfig -ContentRoot (Resolve-Path ./content)

# Указать путь к папке игр вручную
./scripts/run-dev.ps1 -ContentRoot (Resolve-Path ./content) -GamesPath 'D:\Games\ChillHub' -SetClientConfig

# Предварительно собрать Go‑серверы (быстрее стартует), затем запустить
./scripts/run-dev.ps1 -BuildServers -ContentRoot (Resolve-Path ./content) -SetClientConfig
```

### Управление во время работы
- Нажмите `r` или русскую `к` + Enter — перезапуск всех трёх процессов.
- Нажмите `q` + Enter — корректное завершение (освобождает порты, закрывает клиент).

### Примечания
- Для запуска клиента против прод‑сервера задайте `-Env prod` (пропишет `ApiBaseUrl` в `%LOCALAPPDATA%/ChillHub/config.json`).
- Проверка доступности после старта:
  - API: http://localhost:55700/api/games
  - Admin UI: http://localhost:55777/admin
