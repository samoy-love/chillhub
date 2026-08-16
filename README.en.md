# Chill Hub

[Русский](README.md) · English

[![CI](https://github.com/tr0llex/chillhub/actions/workflows/ci.yml/badge.svg)](https://github.com/tr0llex/chillhub/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/tr0llex/chillhub/branch/main/graph/badge.svg)](https://codecov.io/gh/tr0llex/chillhub)
[![prod](https://img.shields.io/website?url=https%3A%2F%2Flauncher.samoy.love&up_message=online&up_color=2ea043&down_message=offline&label=launcher.samoy.love)](https://launcher.samoy.love)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A Windows launcher that distributes modded game builds and keeps them up to
date for a small circle of players — [launcher.samoy.love](https://launcher.samoy.love).

Updating a modpack should mean "Update → Play", not "download the archive,
unpack it here, delete those three files, and don't lose your config". Every
manual step in that sequence is a step someone performs wrong, and a broken
install is indistinguishable from a broken build until somebody debugs it.

Chill Hub grew out of a private mod updater for Lethal Company (C#, WinForms,
2023–2024): one game, a hardcoded file list, updates poured over whatever was
there. Same problem, rebuilt for many games — with diffs, hashes and rollbacks.

![Launcher main screen](docs/img/launcher-main.svg)

## How it works

**Updates are diffs.** The server publishes a manifest per version (paths,
sizes, Blake3 and SHA-256 hashes); the client compares it against what's on
disk and downloads only the difference — over several HTTP Range streams,
resuming after any drop. The game folder ends up an exact copy of the
published version, and switching to any version, rollback included, costs the
same diff rather than a full re-download.

**The launcher updates itself from a separate executable** (`updater/`),
because a running process can't overwrite its own files. That's also why user
data lives in `%APPDATA%\ChillHub` rather than next to the binaries.

**Nothing is signed** — a deliberate choice, not an omission: authenticity
rests on TLS, and SmartScreen warns on install until download reputation
accumulates. The installer is per-user (NSIS, no admin rights required).

Details — manifest format, the diff algorithm, the updater's preserve rules,
integrity checks — are in [docs/spec.md](docs/spec.md).

## Stack

**Client** — C# WPF on .NET 8, published self-contained, so no runtime has to
be present on the user's machine. Distributed as a single NSIS installer built
in CI.

**Server** — Go, two independent binaries: the public API (games, versions,
manifests, news) and the admin one (ZIP build uploads, game registry, news,
maintenance mode, metrics, feedback). Both listen on loopback only. The admin
UI is vanilla JS with no bundler.

**Production** — systemd units behind the system nginx, atomic releases with
rollback via [deploy-kit](https://github.com/tr0llex/deploy-kit). One host
serves everything: `/` the landing page, `/api/*` the public API,
`/admin/api/*` and `/admin/ui/*` the admin side, `/content/*` and
`/downloads/*` the builds straight from nginx.

## Quick start

Requires Go 1.26+, the .NET 8 SDK and PowerShell 7.

```powershell
scripts\run-dev.ps1     # api + admin + client, three windows
scripts\run-admin.ps1   # admin only
scripts\run-client.ps1  # client only
```

Before pushing, the same gates CI runs:

```powershell
cd server; go vet ./...; go test ./... -race
cd ..\launcher; dotnet test
```

## Layout

| Path | Purpose |
|---|---|
| `launcher/` | Client: updates, launching games, news, integrity checks |
| `updater/` | Self-update executable and the rules for preserving user files |
| `server/cmd/api` | Public API |
| `server/cmd/admin` | Admin API: build uploads, `latest` promotion, registry, news, maintenance |
| `server/admin_ui/` | Admin web UI |
| `landing/` | Landing page at the domain root |
| `content/` | Manifests, version files, news, maintenance state |
| `scripts/` | Dev runners, installer build, NSIS script |
| `.deploy-kit/` | Deployment target definitions |
| `docs/` | [Specification](docs/spec.md), [configuration](docs/configuration.md), [installer](docs/installer.md), [security policy](docs/SECURITY.md) |

## Tests

Over a thousand tests on the client (xUnit) and several hundred on the server
(`go test -race`); current coverage is on the codecov badge above. A red run
stops the deployment.

CI gates more than the test suites: golangci-lint and `go vet` on both Linux
and Windows, a cross-compile to linux/arm64 as on production, `dotnet format
--verify-no-changes`, ESLint, Stylelint and HTMLHint for the landing page and
admin UI, `node --test` for the admin UI's escaping helpers, and govulncheck
plus a vulnerable-NuGet scan. The hash seam between Go and C# is pinned
separately (`hashvector_test.go` / `HashVectorTests.cs`), as is the launcher
manifest against the updater's preserve rules — details in
[docs/spec.md](docs/spec.md).

## Deployment

Five targets ship independently — landing page, public API, admin server, admin
UI and the installer — through
[deploy-kit](https://github.com/tr0llex/deploy-kit). The installer is rebuilt by
every merge that touches the client: the exe is swapped on the site
(`/downloads/ChillHub-Setup.exe`) and the self-update build goes to the admin
panel; making it active is a manual `latest` switch there.

```bash
dk                          # what is on production right now
dk deploy chillhub-api      # public API only
dk deploy --all --dry-run   # show the plan, touch nothing
dk rollback chillhub-admin
```

From CI: Actions → Deploy → Run workflow, with the target selectable there.
`all` ships the four targets above but does NOT build the installer — that
needs either a separate run with `target=installer` or a `v*` tag, which does
both.

The admin password is stored as a hash in a systemd drop-in and set separately
from deployment: it changes rarely, and carrying a secret through every deploy
is one more chance to lose it. Content — builds, news, feedback — is
snapshotted by `deploy/backup-content.sh` on a systemd timer; that data exists
nowhere else.

## Part of samoy.love

The domain reads as the owner's surname, Samoylov. Every project below lives on
one host and ships through one pipeline.

| Project | What it is |
|---|---|
| [launcher.samoy.love](https://launcher.samoy.love) | This launcher — [tr0llex/chillhub](https://github.com/tr0llex/chillhub) |
| [snakes.samoy.love](https://snakes.samoy.love) | Browser territory-capture game — [tr0llex/snakes](https://github.com/tr0llex/snakes) |
| [metro.samoy.love](https://metro.samoy.love) | Offline PWA of the Moscow metro map — [tr0llex/metro-map](https://github.com/tr0llex/metro-map) |
| [status.samoy.love](https://status.samoy.love) | Service status: an on-host agent, a Telegram bot and an external watchdog — [tr0llex/status.samoy.love](https://github.com/tr0llex/status.samoy.love) |
| [samoy.love](https://samoy.love) | Personal site — [tr0llex/samoy.love](https://github.com/tr0llex/samoy.love) |
| Monitoring | [tr0llex/metrics.samoy.love](https://github.com/tr0llex/metrics.samoy.love) — monitoring for the whole ecosystem; both Chill Hub binaries expose a Prometheus endpoint on loopback for it to scrape |
| Pipeline | [tr0llex/deploy-kit](https://github.com/tr0llex/deploy-kit) — the shared release pipeline behind all of the above |

## Contacts and license

Alexey Samoylov — [alex@samoy.love](mailto:alex@samoy.love),
[t.me/tr0llex](https://t.me/tr0llex). Security reports:
[docs/SECURITY.md](docs/SECURITY.md). Tasks and plans live in
[issues](https://github.com/tr0llex/chillhub/issues).

MIT, see [LICENSE](LICENSE).
