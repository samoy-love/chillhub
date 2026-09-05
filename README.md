# Chill Hub

Русский · [English](README.en.md)

[![CI](https://github.com/samoy-love/chillhub/actions/workflows/ci.yml/badge.svg)](https://github.com/samoy-love/chillhub/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/samoy-love/chillhub/branch/main/graph/badge.svg)](https://codecov.io/gh/samoy-love/chillhub)
[![прод](https://img.shields.io/website?url=https%3A%2F%2Flauncher.samoy.love&up_message=online&up_color=2ea043&down_message=offline&label=launcher.samoy.love)](https://launcher.samoy.love)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Лаунчер для Windows, который раздаёт сборки игр с модами небольшому кругу
игроков и держит их в актуальном состоянии —
[launcher.samoy.love](https://launcher.samoy.love).

Обновить модпак должно значить «Обновить → Играть», а не «скачай архив,
распакуй сюда, эти три файла удали, а свой конфиг не потеряй». Каждый ручной
шаг в этой цепочке кто-нибудь однажды сделает не так, а сломанная установка
неотличима от сломанной сборки, пока в этом не разберутся руками.

Chill Hub вырос из приватного апдейтера модов для Lethal Company (C#, WinForms,
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

**Модпаки собирает сервер, а не игрок.** Модпак на Thunderstore — это почти
пустой пакет со списком зависимостей: скачать его целиком значит получить
девять мегабайт, в которых нет ни одного мода. Настоящий набор — это обход
дерева зависимостей, полторы сотни пакетов и гигабайты файлов, разложенные по
правилам конкретной игры. Сервер делает эту работу один раз на всех и
публикует результат обычной версией — с тем же манифестом, диффом и откатом,
что и сборка игры. Игроку остаётся выбрать, что запустить: свою копию из Steam
или сборку Chill Hub, с модами или без. Подробности — в
[docs/modpacks.md](docs/modpacks.md).

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
манифесты, новости) и админский (приём ZIP-сборок, реестр игр, сборка модпаков
с Thunderstore, новости, техработы, метрики, обратная связь). Оба слушают
только loopback. Морда админки — vanilla JS без сборщика.

**Прод** — systemd-юниты за системным nginx, атомарные релизы с откатом через
[deploy-kit](https://github.com/samoy-love/deploy-kit). Один хост раздаёт всё:
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
| `server/cmd/admin` | Админ-API: приём сборок, активация `latest`, реестр, модпаки, новости, техработы |
| `server/admin_ui/` | Веб-морда админки |
| `landing/` | Лендинг на корне домена |
| `content/` | Манифесты, файлы версий, новости, состояние техработ |
| `scripts/` | Запуск для разработки, сборка установщика, NSIS-скрипт |
| `.deploy-kit/` | Описания целей выкатки |
| `docs/` | [Спецификация](docs/spec.md), [модпаки](docs/modpacks.md), [оформление](docs/design.md), [конфигурация](docs/configuration.md), [установщик](docs/installer.md), [политика безопасности](docs/SECURITY.md) |

## Тесты

Около двух тысяч тестов на клиенте (xUnit), несколько сотен на сервере
(`go test -race`) и семь сотен на вебе (`node --test`); актуальное покрытие —
на бейдже codecov вверху. Красный
прогон останавливает выкатку.

CI гейтит не только тесты: golangci-lint и `go vet` на Linux и на Windows,
кросс-сборка под linux/arm64 как на проде, `dotnet format
--verify-no-changes`, ESLint, Stylelint и HTMLHint для лендинга и админки,
`node --test` для сайта и панели целиком — от разбора ответов до вёрстки
разделов в настоящем DOM, — govulncheck и проверка NuGet-пакетов на
уязвимости. Отдельно закреплён тестами стык хешей между Go
и C# (`hashvector_test.go` / `HashVectorTests.cs`) и манифест лаунчера против
preserve-правил апдейтера — подробнее в [docs/spec.md](docs/spec.md).

## Выкатка

Пять целей катятся по отдельности — лендинг, публичный API, админ-сервер,
морда админки и установщик — через
[deploy-kit](https://github.com/samoy-love/deploy-kit). Установщик пересобирается
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
отдельный запуск с `target=installer`. Теги ничего не запускают: единственный
автоматический вход — мерж в `main`, и какие цели он задевает, считает job
`changes` по своему диапазону для каждой цели.

Правка `launcher/**`, `updater/**` или самого пайплайна требует поднять
`<Version>` в `launcher/ChillHub/ChillHub.csproj`: сборка .NET не побайтово
повторима, и админка отказывается публиковать другой набор файлов под уже
занятым номером. Гейт `Launcher version bump` проверяет это до мержа.

Пароль админки хранится хешем в systemd drop-in и задаётся отдельно от
выкатки: он меняется редко, а провозить секрет через каждый деплой — лишний
повод его потерять. Контент — сборки, новости, обращения — снимает
`deploy/backup-content.sh` по systemd-таймеру; этих данных нет больше нигде.

## Часть samoy.love

Домен читается как фамилия владельца — Самойлов. Все перечисленные проекты
живут на одном хосте и катятся одним пайплайном.

| Проект | Что это |
|---|---|
| [launcher.samoy.love](https://launcher.samoy.love) | Этот лаунчер — [samoy-love/chillhub](https://github.com/samoy-love/chillhub) |
| [snakes.samoy.love](https://snakes.samoy.love) | Браузерная игра в захват территории — [samoy-love/snakes](https://github.com/samoy-love/snakes) |
| [metro.samoy.love](https://metro.samoy.love) | Офлайн-PWA со схемой московского метро — [samoy-love/metro-map](https://github.com/samoy-love/metro-map) |
| [status.samoy.love](https://status.samoy.love) | Статус сервисов: агент на хосте, бот в Telegram и внешний сторож — [samoy-love/status.samoy.love](https://github.com/samoy-love/status.samoy.love) |
| [samoy.love](https://samoy.love) | Личная страница — [samoy-love/samoy.love](https://github.com/samoy-love/samoy.love) |
| Мониторинг | [samoy-love/metrics.samoy.love](https://github.com/samoy-love/metrics.samoy.love) — мониторинг всей экосистемы; оба бинаря Chill Hub отдают ему метрики в формате Prometheus на loopback |
| Пайплайн | [samoy-love/deploy-kit](https://github.com/samoy-love/deploy-kit) — общий релизный пайплайн для всех перечисленных |

## Контакты и лицензия

Алексей Самойлов — [alex@samoy.love](mailto:alex@samoy.love),
[t.me/tr0llex](https://t.me/tr0llex). Сообщения об уязвимостях —
[docs/SECURITY.md](docs/SECURITY.md). Задачи и планы — в
[issues](https://github.com/samoy-love/chillhub/issues).

MIT, см. [LICENSE](LICENSE).
