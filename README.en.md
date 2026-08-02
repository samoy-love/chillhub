# ChillHub

[![Lint](https://github.com/tr0llex/chillhub/actions/workflows/lint.yml/badge.svg)](https://github.com/tr0llex/chillhub/actions/workflows/lint.yml)
[![codecov](https://codecov.io/gh/tr0llex/chillhub/branch/main/graph/badge.svg)](https://codecov.io/gh/tr0llex/chillhub)
[![prod](https://img.shields.io/website?url=https%3A%2F%2Flauncher.samoy.love&up_message=online&up_color=2ea043&down_message=offline&label=launcher.samoy.love)](https://launcher.samoy.love)

[Русский](README.md) · **English**

A game launcher for Windows 10–11: modded builds, automatic updates and news.
Site — **[launcher.samoy.love](https://launcher.samoy.love)**.

Updating a modpack should mean "Update → Play", not "download the archive,
unpack it here, delete those three files, and don't lose your config". That is
the whole reason this exists.

![Launcher main screen](docs/images/launcher-main.png)

## Updates are diffs

The server publishes a manifest for every version: each file's path, size and
two hashes — Blake3 and SHA-256. The client hashes what is already on disk and
builds a plan: what to download, what to delete, which empty directories to
create. When it finishes, the game folder is an exact copy of the server-side
version, with no leftovers from earlier builds.

Downloads run in 2–16 threads over HTTP Range; partial data stays in `.part`
files and survives a dropped connection. A file is accepted only once its
hashes match. The same path works in both directions: switching to any
published version, rollback included, is a diff rather than a full re-download.

The integrity check in the settings re-hashes files from disk, bypassing the
cache, and reports missing, corrupted and extraneous ones. "Repair" hands the
resulting plan to the very same update machinery.

The launcher updates itself through a separate executable that copies the new
files, skips the user's own (`config.json`, `launcher.version`) and restarts
the app.

## Stack

**Client** — C# WPF on .NET 8, published self-contained, so no runtime has to
be present on the user's machine.

**Server** — Go, two independent binaries: the public API (games, versions,
manifests, news) and the admin one (ZIP build uploads, game registry, news,
maintenance mode, metrics, feedback). Both listen on loopback only.

**Admin UI** — vanilla JS, no bundler.

**Production** — systemd behind the system nginx, atomic releases with
rollback via [deploy-kit](https://github.com/tr0llex/deploy-kit).

## Layout

| Directory | Purpose |
|---|---|
| `launcher/` | The client: updates, launching games, news, integrity checks |
| `updater/` | Self-update executable and the rules for preserving user files |
| `server/cmd/api` | Public API |
| `server/cmd/admin` | Admin API: build uploads, `latest` promotion, registry, news, maintenance |
| `server/admin_ui/` | Admin web UI |
| `landing/` | Landing page at the domain root |
| `content/` | Manifests, version files, news, maintenance state |

A single host serves everything: `/` is the landing page, `/api/*` the public
API, `/admin/api/*` and `/admin/ui/*` the admin side, `/content/*` and
`/downloads/*` the builds, served by nginx directly. Only nginx is exposed.

![Admin UI: uploading a launcher build](docs/images/admin-builds.png)

## Installer

One 57 MB executable: NSIS wrapping a self-contained build with the entire .NET
runtime inside. It installs into the user profile (`%LOCALAPPDATA%\ChillHub`)
in a few seconds and **never asks for administrator rights**. WebView2 comes
from the system — Windows 11 always has it — and on the rare machine without it
the bundled bootstrapper fetches the runtime itself.

User data lives outside the install directory, in `%APPDATA%\ChillHub`.
Anywhere else and the config would end up inside the update package, which
means an endless self-update loop.

Neither the manifests nor the executables are signed, and that is a deliberate
choice. Authenticity rests on TLS, and SmartScreen warns on install until the
download reputation builds up.

## Tests

504 tests on the client (xUnit) and 363 on the server (`go test -race`, 78%
coverage). A red run stops the deployment.

One seam gets its own guarantee: the server hashes files with a Go library and
the client with a C# one, and if the two implementations ever drifted apart
nothing would report an error — every installed launcher would simply decide
that no file matches and re-download whole games. So the same reference vector
is pinned on both sides:
`server/internal/adminapi/builds/hashvector_test.go` and
`launcher/tests/ChillHub.Tests/HashVectorTests.cs`.

The other seam is the launcher manifest against the updater's preserve rules —
any overlap produces an endless self-update loop. Checked by
`dotnet run --project updater/tests/ManifestPreserveCheck`.

## Development

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

## Deployment

Four targets, each deployable on its own: landing page, public API, admin
server and admin UI. The installer is built in CI only and lands in a GitHub
Release.

```bash
dk deploy chillhub-api      # public API only
dk deploy --all --dry-run   # show the plan, touch nothing
dk rollback chillhub-admin
```

From CI: Actions → Release → Run workflow, with the target selectable there. A
`v*` tag deploys everything and attaches the installer to a GitHub Release.

The admin password is stored as a hash in a systemd drop-in on the server and
set separately from deployment: it changes rarely, and carrying a secret
through every deploy is one more chance to lose it. Content — builds, news,
feedback — is snapshotted by `deploy/backup-content.sh` on a systemd timer;
that data exists nowhere else.

## Part of samoy.love

The domain reads as the owner's surname, Samoylov; the rest followed from
there. Every project lives on one host and ships through one pipeline.

| Project | What it is |
|---|---|
| [launcher.samoy.love](https://launcher.samoy.love) | This launcher |
| [snakes.samoy.love](https://snakes.samoy.love) | Browser territory-capture game — [tr0llex/snakes](https://github.com/tr0llex/snakes) |
| [metro.samoy.love](https://metro.samoy.love) | Offline PWA of the Moscow metro map — [tr0llex/metro-map](https://github.com/tr0llex/metro-map) |
| [status.samoy.love](https://status.samoy.love) | Service status: an on-host agent, a Telegram bot and an external watchdog — [tr0llex/status.samoy.love](https://github.com/tr0llex/status.samoy.love) |
| [samoy.love](https://samoy.love) | Personal site — [tr0llex/samoy.love](https://github.com/tr0llex/samoy.love) |
| — | [deploy-kit](https://github.com/tr0llex/deploy-kit): the shared release pipeline behind all of the above |

## Further reading

The specification for the API, the admin side and the client is in
[docs/spec.md](docs/spec.md) (Russian), service environment variables in
[docs/configuration.md](docs/configuration.md). Tasks and plans live in
[issues](https://github.com/tr0llex/chillhub/issues).

Contact: [alex@samoy.love](mailto:alex@samoy.love).
