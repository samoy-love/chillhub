# ChillHub

[![Lint](https://github.com/tr0llex/chillhub/actions/workflows/lint.yml/badge.svg)](https://github.com/tr0llex/chillhub/actions/workflows/lint.yml)
[![codecov](https://codecov.io/gh/tr0llex/chillhub/branch/main/graph/badge.svg)](https://codecov.io/gh/tr0llex/chillhub)
[![прод](https://img.shields.io/website?url=https%3A%2F%2Flauncher.samoy.love&up_message=online&up_color=2ea043&down_message=offline&label=launcher.samoy.love)](https://launcher.samoy.love)

Лаунчер игр для Windows 10–11: раздача сборок, автообновления и новости.

**Обновления идут диффом** — скачиваются только изменившиеся файлы, и папка игры
у пользователя становится точной копией серверной версии. Лишнее удаляется,
пустые каталоги создаются по манифесту, целостность проверяется хешами
(Blake3/SHA-256), докачки переживают обрыв связи (HTTP Range и `.part`).

Ради этого всё и затевалось: обновить модпак — это «Обновить → Играть», а не
«скачай архив, распакуй сюда, удали там».

Сайт: **[launcher.samoy.love](https://launcher.samoy.love)**

## Из чего состоит

| Часть | Что делает |
|---|---|
| `launcher/` | Клиент на C# WPF: обновления, запуск игр, новости, проверка целостности |
| `updater/` | Отдельный exe самообновления и правила сохранения пользовательских файлов |
| `server/cmd/api` | Публичный API: список игр, версии, манифесты, новости |
| `server/cmd/admin` | Админ-API: приём ZIP-сборок, активация `latest`, реестр игр, новости, техработы, метрики, обратная связь |
| `server/admin_ui/` | Веб-морда админки |
| `landing/` | Лендинг на корне домена |
| `content/` | Манифесты, файлы версий, новости и ассеты, состояние техработ |

Спецификация API и админки — [docs/spec.md](docs/spec.md), переменные окружения
сервисов — [docs/configuration.md](docs/configuration.md).

## Домены и порты

`https://launcher.samoy.love` — один хост на всё:

| Путь | Куда идёт |
|---|---|
| `/` | лендинг из `landing/` |
| `/api/*` | публичный API, `127.0.0.1:55700` |
| `/admin/api/*` | админ-API, `127.0.0.1:55777` |
| `/admin/ui/*` | морда админки, статика |
| `/content/*`, `/downloads/*` | сборки и ассеты, отдаёт nginx напрямую |

Порты приложений закрыты снаружи: наружу смотрит только nginx.

## Локальная разработка

Нужны Go 1.22+, .NET 8 SDK и PowerShell 7.

```powershell
scripts\run-dev.ps1     # api + admin + клиент, три окна
scripts\run-admin.ps1   # только админка
scripts\run-client.ps1  # только клиент
```

Перед пушем — то же, что гоняет CI:

```powershell
cd server; go vet ./...; go test ./... -race
cd ..\launcher; dotnet test
```

## Выкатка

Пять целей, каждая катится отдельно: лендинг, публичный API, админ-сервер, морда
админки и установщик. Всё — общим пайплайном
[deploy-kit](https://github.com/tr0llex/deploy-kit).

```bash
dk deploy chillhub-api      # только публичный API
dk deploy --all --dry-run   # посмотреть план, ничего не трогая
dk rollback chillhub-admin
```

Из CI: Actions → Release → Run workflow, там же выбор цели. Тег `v*` катит всё и
собирает установщик в GitHub Release.

Описания целей — в `.deploy-kit/`, конфигурация nginx — в
[deploy-kit/nginx](https://github.com/tr0llex/deploy-kit/tree/main/nginx).

Пароль админки хранится **хешем в systemd drop-in** на сервере и задаётся
отдельно от выкатки: он меняется редко, а провозить секрет через каждый деплой —
лишний повод его потерять.

Контент (сборки, новости, обращения) снимает `deploy/backup-content.sh` по
systemd-таймеру — этих данных нет больше нигде.

## Что здесь легко сломать

**Манифест без хешей** отвергается: запись без Blake3 и SHA-256 означала бы
установку файла вообще без проверки целостности.

**`config.json` и `launcher.version` не должны попадать в ZIP лаунчера.**
Пересечение манифеста с preserve-правилами апдейтера даёт бесконечный цикл
самообновления. Проверка: `dotnet run --project updater/tests/ManifestPreserveCheck`.

**Пользовательские данные лежат в `%APPDATA%\ChillHub`**, а не в каталоге
установки. При обновлении со старых версий конфиг мигрирует сам.

**Подписи нет** — ни у манифестов, ни у исполняемых файлов, это осознанное
решение. Подлинность раздачи держится на TLS, а SmartScreen предупреждает при
установке, пока не наберётся репутация загрузок.

## Планы

Задачи — в [issues](https://github.com/tr0llex/chillhub/issues).
