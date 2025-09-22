.PHONY: deploy deploy-nobuild nginx-reload services-restart smoke lint

# Defaults can be overridden: make deploy BRANCH=main
BRANCH ?= main
REPO_DIR ?= $(CURDIR)
DEPLOY_SCRIPT := $(REPO_DIR)/scripts/deploy.sh

# Defaults for Windows deploy helper
COOKIE_DOMAIN ?= launcher.samoy.love
COOKIE_SECURE ?= true
# Default remote connection and auth for deploy-win (can be overridden)
HOST ?= 207.127.93.34
USER ?= ubuntu
KEY ?= C:\Users\Alexey Samoylov\Desktop\server access\oracle 2025-09-21.key
JWT ?= a-very-long-random-secret
ADMIN_USER ?= admin
ADMIN_PLAIN ?= kek2
DOWNLOADS_DIR ?= C:\Users\Alexey Samoylov\Desktop\Launcher Project\scripts\generated_downloads

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

smoke:
	curl -I https://launcher.samoy.love/admin/ || true
	curl -I https://launcher.samoy.love/admin/ui/admin.js || true
	curl -I https://launcher.samoy.love/admin/api/health || true
	curl -I https://launcher.samoy.love/admin/api/games || true
	curl -fsSL https://launcher.samoy.love/manifests/launcher/latest.json || true
	curl -fsSL https://launcher.samoy.love/assets/ping.txt || true

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
	@echo [lint:web] HTMLHint (landing + server/admin_ui)
	@- npx -y htmlhint "landing/**/*.html" "server/admin_ui/**/*.html"
	@echo [lint:web] Stylelint (landing + server/admin_ui)
	@- npx -y stylelint "landing/**/*.css" "server/admin_ui/**/*.css"
	@echo [lint:web] ESLint (landing + server/admin_ui)
	@- npx -y eslint "landing/**/*.js" "server/admin_ui/**/*.js"

# Go: Prefer golangci-lint like CI; fallback to vet/fmt if unavailable
lint-go:
	@echo.
	@echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
	@echo 💼 Go lint (server)
	@echo ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
	@echo [lint:go] golangci-lint (if installed)
	@- cmd /C "where golangci-lint >NUL 2>&1 && ( cd server && golangci-lint run ) || echo [lint:go] golangci-lint not found - skipping"
	@echo [lint:go] Running go vet
	@- ( cd server && go vet ./... )
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
deploy-win:
	@echo Deploying to $(HOST) as $(USER) using PowerShell script
	powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/deploy-win.ps1" \
	 -SshHost "$(HOST)" \
	 -SshUser "$(USER)" \
	 -KeyPath "$(KEY)" \
	 -Branch "$(BRANCH)" \
	 -JwtSecret "$(JWT)" \
	 -AdminUser "$(ADMIN_USER)" \
	 -AdminPasswordBcrypt "$(ADMIN_BCRYPT)" \
	 -AdminPasswordPlain "$(ADMIN_PLAIN)" \
	 -CookieDomain "$(COOKIE_DOMAIN)" \
	 -CookieSecure "$(COOKIE_SECURE)" \
	 -DownloadsDir "$(DOWNLOADS_DIR)" \
	 -Parallel "$(or $(PARALLEL),8)" \
	 $(if $(START_AT_REMOTE),-StartAtRemote) \
	 $(if $(FAIL_ON_MISMATCH),-FailOnManifestMismatch)
