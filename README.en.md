# Chill Hub

[Русский](README.md) · English

[![CI](https://github.com/samoy-love/chillhub/actions/workflows/ci.yml/badge.svg)](https://github.com/samoy-love/chillhub/actions/workflows/ci.yml)
[![codecov](https://codecov.io/gh/samoy-love/chillhub/branch/main/graph/badge.svg)](https://codecov.io/gh/samoy-love/chillhub)
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

**Modpacks are assembled by the server, not by the player.** A Thunderstore
modpack is an almost empty package holding a list of dependencies: downloading
it whole gives you nine megabytes containing no mods at all. The real set is a
walk of the dependency tree — a hundred and fifty packages and gigabytes of
files, laid out by the rules of that particular game. The server does that work
once for everyone and publishes the result as an ordinary version, with the
same manifest, diff and rollback as a game build. All the player picks is what
to launch: their own Steam copy or the Chill Hub build, with mods or without.
Details are in [docs/modpacks.md](docs/modpacks.md).

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
manifests, news) and the admin one (ZIP build uploads, game registry,
Thunderstore modpack builds, news, maintenance mode, metrics, feedback). Both
listen on loopback only. The admin UI is vanilla JS with no bundler.

**Production** — systemd units behind the system nginx, atomic releases with
rollback via [deploy-kit](https://github.com/samoy-love/deploy-kit). One host
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
| `server/cmd/admin` | Admin API: build uploads, `latest` promotion, registry, modpacks, news, maintenance |
| `server/admin_ui/` | Admin web UI |
| `landing/` | Landing page at the domain root |
| `content/` | Manifests, version files, news, maintenance state |
| `scripts/` | Dev runners, installer build, NSIS script |
| `.deploy-kit/` | Deployment target definitions |
| `docs/` | [Specification](docs/spec.md), [modpacks](docs/modpacks.md), [configuration](docs/configuration.md), [installer](docs/installer.md), [security policy](docs/SECURITY.md) |

## Tests

Close to two thousand tests on the client (xUnit), several hundred on the
server (`go test -race`) and a couple of hundred on the web side
(`node --test`); current coverage is on the codecov badge above. A red run
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
[deploy-kit](https://github.com/samoy-love/deploy-kit). The installer is rebuilt by
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
needs a separate run with `target=installer`. Tags trigger nothing: the only
automatic entry point is a merge into `main`, and which targets it touches is
worked out by the `changes` job, per target, from its own baseline.

Touching `launcher/**`, `updater/**` or the pipeline itself requires bumping
`<Version>` in `launcher/ChillHub/ChillHub.csproj`: a .NET build is not
byte-reproducible, and the admin server refuses to publish a different set of
files under a number that is already taken. The `Launcher version bump` gate
checks this before the merge.

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
| [launcher.samoy.love](https://launcher.samoy.love) | This launcher — [samoy-love/chillhub](https://github.com/samoy-love/chillhub) |
| [snakes.samoy.love](https://snakes.samoy.love) | Browser territory-capture game — [samoy-love/snakes](https://github.com/samoy-love/snakes) |
| [metro.samoy.love](https://metro.samoy.love) | Offline PWA of the Moscow metro map — [samoy-love/metro-map](https://github.com/samoy-love/metro-map) |
| [status.samoy.love](https://status.samoy.love) | Service status: an on-host agent, a Telegram bot and an external watchdog — [samoy-love/status.samoy.love](https://github.com/samoy-love/status.samoy.love) |
| [samoy.love](https://samoy.love) | Personal site — [samoy-love/samoy.love](https://github.com/samoy-love/samoy.love) |
| Monitoring | [samoy-love/metrics.samoy.love](https://github.com/samoy-love/metrics.samoy.love) — monitoring for the whole ecosystem; both Chill Hub binaries expose a Prometheus endpoint on loopback for it to scrape |
| Pipeline | [samoy-love/deploy-kit](https://github.com/samoy-love/deploy-kit) — the shared release pipeline behind all of the above |

## Contacts and license

Alexey Samoylov — [alex@samoy.love](mailto:alex@samoy.love),
[t.me/tr0llex](https://t.me/tr0llex). Security reports:
[docs/SECURITY.md](docs/SECURITY.md). Tasks and plans live in
[issues](https://github.com/samoy-love/chillhub/issues).

MIT, see [LICENSE](LICENSE).
