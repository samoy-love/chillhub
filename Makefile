.PHONY: deploy deploy-nobuild nginx-reload services-restart smoke lint

# Defaults can be overridden: make deploy BRANCH=main
BRANCH ?= main
REPO_DIR ?= $(CURDIR)
DEPLOY_SCRIPT := $(REPO_DIR)/scripts/deploy.sh

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
	@echo "\n✅ All lint stages finished (see logs above for any issues)."

# Web: HTMLHint, Stylelint, ESLint (landing + admin_ui)
lint-web:
	@echo "\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
	@echo "🌐 Web lint (HTMLHint, Stylelint, ESLint)"
	@echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
	@echo "[lint:web] HTMLHint (landing + server/admin_ui)"
	- npx -y htmlhint "landing/**/*.html" "server/admin_ui/**/*.html"
	@echo "[lint:web] Stylelint (landing + server/admin_ui)"
	- npx -y stylelint "landing/**/*.css" "server/admin_ui/**/*.css"
	@echo "[lint:web] ESLint (landing + server/admin_ui)"
	- npx -y eslint "landing/**/*.js" "server/admin_ui/**/*.js"

# Go: Prefer golangci-lint like CI; fallback to vet/fmt if unavailable
lint-go:
	@echo "\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
	@echo "💼 Go lint (server)"
	@echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
	@if command -v golangci-lint >/dev/null 2>&1; then \
		echo "[lint:go] golangci-lint (server)"; \
		( cd server && golangci-lint run ); \
	else \
		echo "[lint:go] golangci-lint not found — falling back to 'go vet' and 'gofmt -l'"; \
		go -C server vet ./... || true; \
		echo "[lint:go] Files needing gofmt (if any):"; \
		go -C server fmt -l ./... || true; \
	fi

# .NET: Build and code style check (non-blocking style check like CI)
lint-dotnet:
	@echo "\n━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
	@echo "🧩 .NET lint (launcher)"
	@echo "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━"
	@if command -v dotnet >/dev/null 2>&1; then \
		echo "[lint:dotnet] Restore & Build"; \
		( cd launcher/ChillHub && dotnet restore && dotnet build --no-restore -c Debug ); \
		echo "[lint:dotnet] Code style check (dotnet format) — non-blocking"; \
		( cd launcher/ChillHub && dotnet format --verbosity minimal ) || true; \
	else \
		echo "[lint:dotnet] Skipped (.NET SDK not found)"; \
	fi
