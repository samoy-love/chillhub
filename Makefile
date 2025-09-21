.PHONY: deploy deploy-nobuild nginx-reload services-restart smoke

# Defaults can be overridden: make deploy BRANCH=main
BRANCH ?= main
REPO_DIR ?= $(shell git rev-parse --show-toplevel 2>/dev/null || pwd)
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
