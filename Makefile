.PHONY: deploy deploy-nobuild nginx-reload services-restart smoke lint

# Defaults can be overridden: make deploy BRANCH=main
BRANCH ?= main
REPO_DIR ?= $(CURDIR)
DEPLOY_SCRIPT := $(REPO_DIR)/scripts/deploy.sh

# Defaults for Windows deploy helper
COOKIE_DOMAIN ?= launcher.samoy.love
COOKIE_SECURE ?= true

# Host, key path and secrets are deliberately NOT set here: this file is tracked
# by git, and everything written in it ends up in the repository history for
# good. Put your own values in deploy.local.mk (gitignored, see
# deploy.local.mk.example) or pass them on the command line.
#
# This used to hold the production IP, the path to the private SSH key, the
# admin password and a JWT secret of "a-very-long-random-secret". The latter was
# not merely a placeholder — the production admin service was actually running
# on it, so anyone who could read the repo could mint valid admin sessions.
-include deploy.local.mk

ADMIN_USER ?= admin

# One command to deploy everything
deploy:
	@chmod +x "$(DEPLOY_SCRIPT)"
	@echo "[make] Deploying branch=$(BRANCH) repo=$(REPO_DIR)"
	@bash "$(DEPLOY_SCRIPT)" --branch "$(BRANCH)" --repo-dir "$(REPO_DIR)"

# Same, but skip rebuilding Go binaries (only configs/static)
deploy-nobuild:
	@chmod +x "$(DEPLOY_SCRIPT)"
	@echo "[make] Deploying (no-build) branch=$(BRANCH) repo=$(REPO_DIR)"
	@bash "$(DEPLOY_SCRIPT)" --branch "$(BRANCH)" --no-build --repo-dir "$(REPO_DIR)"

# Helpers (useful on server)
nginx-reload:
	sudo nginx -t && sudo systemctl reload nginx

services-restart:
	sudo systemctl restart chillhub-api.service chillhub-admin.service

# Базовый адрес для смоук-тестов; переопределяется: make smoke SITE_BASE=...
SITE_BASE ?= https://launcher.samoy.love

# И26: ЦЕЛЬ smoke БОЛЬШЕ НЕ ЗЕЛЁНАЯ ВСЕГДА.
#
# Раньше каждая строка заканчивалась на `|| true`, а `curl -I` без -f и так
# возвращает 0 на любом HTTP-ответе, включая 404 и 502. То есть цель НЕ МОГЛА
# завершиться неуспехом ни при каких обстоятельствах: она печатала заголовки и
# всегда рапортовала об успехе. Проверка, которая не может провалиться, — это
# не проверка, а видимость проверки, и она хуже отсутствующей: на неё
# полагаются.
#
# Теперь коды ответов сверяются с ожидаемыми, а цель возвращает ненулевой код,
# если хоть одна проверка не прошла. Набор проверок держим согласованным со
# смоук-тестами в scripts/deploy.sh и .github/workflows/deploy.yml.
#
# Никаких -k: непроверенный сертификат делает главный реальный отказ (протухший
# Let's Encrypt) невидимым — см. подробный комментарий на эту тему в deploy.sh.
smoke:
	@FAIL=0; \
	code(){ curl -s --max-time 10 -o /dev/null -w '%{http_code}' "$$1"; }; \
	expect(){ \
	  _url="$$1"; _name="$$2"; _want="$$3"; _got=$$(code "$$1"); \
	  case " $$_want " in \
	    *" $$_got "*) echo "[smoke] PASS $$_name ($$_got) $$_url" ;; \
	    *) echo "[smoke] FAIL $$_name -> $$_got (ожидалось: $$_want) $$_url"; FAIL=1 ;; \
	  esac; \
	}; \
	expect "$(SITE_BASE)/" "лендинг" "200"; \
	expect "$(SITE_BASE)/styles.css" "статика лендинга" "200"; \
	expect "$(SITE_BASE)/admin/ui/login.html" "страница входа" "200"; \
	expect "$(SITE_BASE)/admin/api/health" "health админки" "200"; \
	expect "$(SITE_BASE)/manifests/launcher/latest.json" "latest.json" "200"; \
	expect "$(SITE_BASE)/assets/ping.txt" "новостные ассеты" "200"; \
	_dl=$$(curl -s --max-time 10 -o /dev/null -w '%{http_code}' -I "$(SITE_BASE)/downloads/ChillHub-Setup.exe"); \
	if [ "$$_dl" = "200" ]; then echo "[smoke] PASS установщик на лендинге (200)"; \
	else echo "[smoke] FAIL установщик на лендинге -> $$_dl (кнопка «Скачать» отдаёт ошибку)"; FAIL=1; fi; \
	expect "$(SITE_BASE)/admin/" "/admin/ закрыт" "401 302"; \
	expect "$(SITE_BASE)/admin/ui/admin.js" "admin.js закрыт" "401 302"; \
	if [ "$$FAIL" -ne 0 ]; then echo "[smoke] ЕСТЬ ПРОВАЛЫ"; exit 1; fi; \
	echo "[smoke] все проверки пройдены"

# ============
# Linting
# ============
.PHONY: lint lint-web lint-go lint-dotnet

# Aggregate lint that runs all available checks (like CI)
lint: lint-web lint-go lint-dotnet
	@echo.
	@echo ✅ All lint stages finished (see logs above for any issues).

# Web: HTMLHint, Stylelint, ESLint (landing + admin_ui)
lint-web:
	@echo.
	@echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
	@echo 🌐 Web lint (HTMLHint, Stylelint, ESLint)
	@echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
#
# И26: ВЕРСИИ ЗАКРЕПЛЕНЫ И ОШИБКИ НЕ ИГНОРИРУЮТСЯ.
#
# Было `@- npx -y htmlhint ...` — то есть, во-первых, `-` в начале рецепта
# заставляет make игнорировать код возврата (локальный lint не мог упасть
# никогда), во-вторых, `npx -y <пакет>` без версии тянет свежий с npm.
#
# Второе хуже первого: локальный прогон и CI использовали РАЗНЫЕ версии
# линтеров. Расхождение проявлялось самым неприятным образом — «у меня всё
# чисто», а PR краснеет; либо наоборот, локально ругается на то, чего CI не
# видит. Версии здесь совпадают с пинами в .github/workflows/lint.yml,
# и менять их надо в двух местах ОДНОВРЕМЕННО (список — в шапке lint.yml).
	@echo [lint:web] HTMLHint (landing + server/admin_ui)
	npx -y htmlhint@1.9.2 "landing/**/*.html" "server/admin_ui/**/*.html"
	@echo [lint:web] Stylelint (landing + server/admin_ui)
	npx -y stylelint@17.14.1 "landing/**/*.css" "server/admin_ui/**/*.css"
	@echo [lint:web] ESLint (landing + server/admin_ui)
	npx -y eslint@10.8.0 "landing/**/*.js" "server/admin_ui/**/*.js"

# Go: Prefer golangci-lint like CI; fallback to vet/fmt if unavailable
lint-go:
	@echo.
	@echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
	@echo 💼 Go lint (server)
	@echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
	@echo [lint:go] golangci-lint (if installed)
# `-` оставлен намеренно ТОЛЬКО здесь: golangci-lint может быть не установлен
# локально, и «не установлен» — это не «код плохой». В CI он обязателен и
# закреплён версией (lint.yml).
	@- cmd /C "where golangci-lint >NUL 2>&1 && ( cd server && golangci-lint run ) || echo [lint:go] golangci-lint not found - skipping"
	@echo [lint:go] Running go vet
# И26: без `-`. go vet есть в любой установке Go, и в CI этот шаг блокирующий —
# локально он обязан вести себя так же.
	( cd server && go vet ./... )
	@echo [lint:go] Files needing gofmt (if any)
	@- ( cd server && gofmt -l . )

# .NET: Build and code style check (non-blocking style check like CI)
lint-dotnet:
	@echo.
	@echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
	@echo 🧩 .NET lint (launcher)
	@echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
	@echo [lint:dotnet] Restore ^& Build (if dotnet is installed)
	@- ( cd launcher/ChillHub && dotnet restore && dotnet build --no-restore -c Debug /p:UseAppHost=false )
	@echo [lint:dotnet] Code style check (dotnet format) — non-blocking
	@- ( cd launcher/ChillHub && dotnet format --verbosity minimal )

# ============
# Local run helpers
# ============
.PHONY: run-prod run-local
# Equivalent to:
#  ./scripts/run-dev.ps1 -Env prod  -SetClientConfig -ContentRoot (Resolve-Path ./content)
#  ./scripts/run-dev.ps1 -Env local -SetClientConfig -ContentRoot (Resolve-Path ./content)
run-prod:
	powershell -NoProfile -ExecutionPolicy Bypass -Command "& { ./scripts/run-dev.ps1 -Env prod -SetClientConfig -ContentRoot (Resolve-Path ./content) }"

run-local:
	powershell -NoProfile -ExecutionPolicy Bypass -Command "& { ./scripts/run-dev.ps1 -Env local -SetClientConfig -ContentRoot (Resolve-Path ./content) }"

# ============
# Windows remote deploy helper
# ============
.PHONY: deploy-win
# Usage:
# make deploy-win HOST=your.vps.host USER=ubuntu KEY="C:/Users/you/.ssh/id_rsa" [BRANCH=main] [JWT=...] [ADMIN_USER=admin] [ADMIN_BCRYPT=...] [ADMIN_PLAIN=...] [COOKIE_DOMAIN=launcher.samoy.love] [COOKIE_SECURE=true] [DOWNLOADS_DIR=C:/path/downloads]
#
# И14: СЕКРЕТЫ ПЕРЕДАЮТСЯ СКРИПТУ ЧЕРЕЗ ОКРУЖЕНИЕ, А НЕ АРГУМЕНТАМИ.
#
# Раньше JWT, ADMIN_BCRYPT и ADMIN_PLAIN подставлялись прямо в командную строку
# powershell. Такая строка видна в списке процессов кому угодно на машине:
# Диспетчер задач и `Get-CimInstance Win32_Process | select CommandLine`
# показывают её без прав администратора, и висит она там всё время деплоя.
#
# Вдобавок у рецепта не было префикса @, поэтому make ПЕЧАТАЛ всю команду
# целиком — секреты уезжали ещё и в терминал, а оттуда в историю оболочки.
# Префикс @ добавлен вместе с переходом на окружение.
#
# Значения JWT/ADMIN_BCRYPT/ADMIN_PLAIN по-прежнему берутся из deploy.local.mk
# (он в .gitignore) — меняется только способ доставки до скрипта.
deploy-win:
ifeq ($(strip $(HOST)),)
	$(error HOST is not set. Put it in deploy.local.mk or pass HOST=... on the command line)
endif
ifeq ($(strip $(USER)),)
	$(error USER is not set. Put it in deploy.local.mk or pass USER=... on the command line)
endif
ifeq ($(strip $(KEY)),)
	$(error KEY is not set. Put it in deploy.local.mk or pass KEY=... on the command line)
endif
	@echo Deploying to $(HOST) as $(USER) using PowerShell script
	@CHILLHUB_JWT_SECRET="$(JWT)" \
	 CHILLHUB_ADMIN_PASSWORD_BCRYPT="$(ADMIN_BCRYPT)" \
	 CHILLHUB_ADMIN_PASSWORD="$(ADMIN_PLAIN)" \
	 powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/deploy-win.ps1" \
	 -SshHost "$(HOST)" \
	 -SshUser "$(USER)" \
	 -KeyPath "$(KEY)" \
	 -Branch "$(BRANCH)" \
	 -AdminUser "$(ADMIN_USER)" \
	 -CookieDomain "$(COOKIE_DOMAIN)" \
	 -CookieSecure "$(COOKIE_SECURE)" \
	 -DownloadsDir "$(DOWNLOADS_DIR)" \
	 -Parallel "$(or $(PARALLEL),8)" \
	 $(if $(START_AT_REMOTE),-StartAtRemote) \
	 $(if $(FAIL_ON_MISMATCH),-FailOnManifestMismatch)
