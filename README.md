# ChillHub

Русский · [English](README.en.md)

[![CI](https://github.com/tr0llex/chillhub/actions/workflows/ci.yml/badge.svg)](https://github.com/tr0llex/chillhub/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/tr0llex/chillhub/branch/main/graph/badge.svg)](https://codecov.io/gh/tr0llex/chillhub)
[![прод](https://img.shields.io/website?url=https%3A%2F%2Flauncher.samoy.love&up_message=online&up_color=2ea043&down_message=offline&label=launcher.samoy.love)](https://launcher.samoy.love)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Лаунчер для Windows, который раздаёт сборки игр с модами небольшому кругу
игроков и держит их в актуальном состоянии —
[launcher.samoy.love](https://launcher.samoy.love).

Обновить модпак должно значить «Обновить → Играть», а не «скачай архив,
распакуй сюда, эти три файла удали, а свой конфиг не потеряй». Каждый ручной
шаг в этой цепочке кто-нибудь однажды сделает не так, а сломанная установка
неотличима от сломанной сборки, пока в этом не разберутся руками.

ChillHub вырос из приватного апдейтера модов для Lethal Company (C#, WinForms,
2023–2024): одна игра, зашитый список файлов, обновление лилось поверх того,
что лежало. Та же задача, пересобранная под много игр — с диффами, хешами
и откатами.

![Главный экран лаунчера](docs/img/launcher-main.svg)

## Как устроено

**Обновления идут диффом.** Сервер публикует манифест версии (пути, размеры,
хеши Blake3 и SHA-256), клиент сверяет его с тем, что лежит на диске, и качает
только разницу — в несколько потоков через HTTP Range, с докачкой после
обрыва связи. В итоге папка игры — точная копия опубликованной версии, а
переход на любую версию, включая откат, стоит того же диффа, а не полной
перекачки.

**Себя лаунчер обновляет отдельным exe** (`updater/`), потому что запущенный
процесс не может переписать собственные файлы. Пользовательские данные из-за
этого живут в `%APPDATA%\ChillHub`, а не рядом с бинарями.

**Ничего не подписано** — это решение, а не упущение: подлинность держится на
TLS, SmartScreen предупреждает при установке, пока не наберётся репутация
загрузок. Установщик пользовательский (NSIS, без прав администратора).

Детали — форматы манифеста, диффовый алгоритм, preserve-правила апдейтера,
проверка целостности — в [docs/spec.md](docs/spec.md).

## Стек

**Клиент** — C# WPF на .NET 8. Публикуется self-contained, поэтому рантайма на
машине пользователя не требуется. Раздаётся одним установщиком NSIS, который
собирается в CI.

**Сервер** — Go, два независимых бинаря: публичный API (игры, версии,
манифесты, новости) и админский (приём ZIP-сборок, реестр игр, новости,
техработы, метрики, обратная связь). Оба слушают только loopback. Морда
админки — vanilla JS без сборщика.

**Прод** — systemd-юниты за системным nginx, атомарные релизы с откатом через
[deploy-kit](https://github.com/tr0llex/deploy-kit). Один хост раздаёт всё:
`/` — лендинг, `/api/*` — публичный API, `/admin/api/*` и `/admin/ui/*` —
админка, `/content/*` и `/downloads/*` — сборки, их отдаёт nginx напрямую.

## Быстрый старт

Нужны Go 1.26+, .NET 8 SDK и PowerShell 7.

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

## Структура

| Путь | Назначение |
|---|---|
| `launcher/` | Клиент: обновления, запуск игр, новости, проверка целостности |
| `updater/` | Отдельный exe самообновления и правила сохранения пользовательских файлов |
| `server/cmd/api` | Публичный API |
| `server/cmd/admin` | Админ-API: приём сборок, активация `latest`, реестр, новости, техработы |
| `server/admin_ui/` | Веб-морда админки |
| `landing/` | Лендинг на корне домена |
| `content/` | Манифесты, файлы версий, новости, состояние техработ |
| `scripts/` | Запуск для разработки, сборка установщика, NSIS-скрипт |
| `.deploy-kit/` | Описания целей выкатки |
| `docs/` | [Спецификация](docs/spec.md), [конфигурация](docs/configuration.md), [установщик](docs/installer.md), [политика безопасности](docs/SECURITY.md) |

## Тесты

Больше тысячи тестов на клиенте (xUnit) и несколько сотен на сервере
(`go test -race`); актуальное покрытие — на бейдже codecov вверху. Красный
прогон останавливает выкатку.

CI гейтит не только тесты: golangci-lint и `go vet` на Linux и на Windows,
кросс-сборка под linux/arm64 как на проде, `dotnet format
--verify-no-changes`, ESLint, Stylelint и HTMLHint для лендинга и админки,
`node --test` для функций экранирования в админке, govulncheck и проверка
NuGet-пакетов на уязвимости. Отдельно закреплён тестами стык хешей между Go
и C# (`hashvector_test.go` / `HashVectorTests.cs`) и манифест лаунчера против
preserve-правил апдейтера — подробнее в [docs/spec.md](docs/spec.md).

## Выкатка

Пять целей катятся по отдельности — лендинг, публичный API, админ-сервер,
морда админки и установщик — через
[deploy-kit](https://github.com/tr0llex/deploy-kit). Установщик пересобирается
каждым мержем, задевшим клиента: exe подменяется на сайте
(`/downloads/ChillHub-Setup.exe`), сборка самообновления уезжает в админку;
активной её делает переключение latest в админке — руками.

```bash
dk                          # что сейчас на проде
dk deploy chillhub-api      # только публичный API
dk deploy --all --dry-run   # посмотреть план, ничего не трогая
dk rollback chillhub-admin
```

Из CI: Actions → Deploy → Run workflow, там же выбор цели. Значение `all`
выкатывает четыре цели выше, но установщик НЕ собирает — для него нужен
отдельный запуск с `target=installer` либо тег `v*`, который делает и то и
другое.

Пароль админки хранится хешем в systemd drop-in и задаётся отдельно от
выкатки: он меняется редко, а провозить секрет через каждый деплой — лишний
повод его потерять. Контент — сборки, новости, обращения — снимает
`deploy/backup-content.sh` по systemd-таймеру; этих данных нет больше нигде.

## Часть samoy.love

Домен читается как фамилия владельца — Самойлов. Все перечисленные проекты
живут на одном хосте и катятся одним пайплайном.

| Проект | Что это |
|---|---|
| [launcher.samoy.love](https://launcher.samoy.love) | Этот лаунчер — [tr0llex/chillhub](https://github.com/tr0llex/chillhub) |
| [snakes.samoy.love](https://snakes.samoy.love) | Браузерная игра в захват территории — [tr0llex/snakes](https://github.com/tr0llex/snakes) |
| [metro.samoy.love](https://metro.samoy.love) | Офлайн-PWA со схемой московского метро — [tr0llex/metro-map](https://github.com/tr0llex/metro-map) |
| [status.samoy.love](https://status.samoy.love) | Статус сервисов: агент на хосте, бот в Telegram и внешний сторож — [tr0llex/status.samoy.love](https://github.com/tr0llex/status.samoy.love) |
| [samoy.love](https://samoy.love) | Личная страница — [tr0llex/samoy.love](https://github.com/tr0llex/samoy.love) |
| Мониторинг | [tr0llex/metrics.samoy.love](https://github.com/tr0llex/metrics.samoy.love) — мониторинг всей экосистемы; оба бинаря ChillHub отдают ему метрики в формате Prometheus на loopback |
| Пайплайн | [tr0llex/deploy-kit](https://github.com/tr0llex/deploy-kit) — общий релизный пайплайн для всех перечисленных |

## Контакты и лицензия

Алексей Самойлов — [alex@samoy.love](mailto:alex@samoy.love),
[t.me/tr0llex](https://t.me/tr0llex). Сообщения об уязвимостях —
[docs/SECURITY.md](docs/SECURITY.md). Задачи и планы — в
[issues](https://github.com/tr0llex/chillhub/issues).

MIT, см. [LICENSE](LICENSE).
