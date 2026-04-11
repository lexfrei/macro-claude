.DEFAULT_GOAL := help

SHELL := /bin/bash
.SHELLFLAGS := -o errexit -o nounset -o pipefail -c

# Directories
HOOKS_DIR           := hooks
PLUGIN_DIR          := plugin
VSCODE_EXT_DIR      := vscode-extension

.PHONY: help
help: ## Show this help
	@awk 'BEGIN {FS = ":.*##"; printf "Available targets:\n"} /^[a-zA-Z_-]+:.*?##/ { printf "  \033[36m%-24s\033[0m %s\n", $$1, $$2 }' $(MAKEFILE_LIST)

.PHONY: lint
lint: lint-shell lint-plugin lint-vscode ## Run all linters

.PHONY: lint-shell
lint-shell: ## Run shellcheck over all bash hooks
	@shellcheck --severity=style --shell=bash --external-sources --enable=all $(HOOKS_DIR)/*.sh

.PHONY: lint-plugin
lint-plugin: ## Run dotnet format + build with analyzers (strict)
	@if [ -d "$(PLUGIN_DIR)" ] && [ -n "$$(find $(PLUGIN_DIR) -name '*.csproj' -print -quit)" ]; then \
		cd $(PLUGIN_DIR) && dotnet format --verify-no-changes --severity info && dotnet build --configuration Release --no-incremental ; \
	else \
		echo "plugin/: no csproj yet, skipping" ; \
	fi

.PHONY: lint-vscode
lint-vscode: ## Run eslint over vscode-extension
	@if [ -f "$(VSCODE_EXT_DIR)/package.json" ]; then \
		cd $(VSCODE_EXT_DIR) && npm run lint && npm run typecheck ; \
	else \
		echo "$(VSCODE_EXT_DIR)/: no package.json yet, skipping" ; \
	fi

.PHONY: format
format: format-shell format-plugin format-vscode ## Auto-format all sources

.PHONY: format-shell
format-shell: ## Format bash scripts with shfmt if available
	@if command -v shfmt >/dev/null 2>&1 ; then \
		shfmt --indent 2 --case-indent --simplify --write $(HOOKS_DIR)/*.sh ; \
	else \
		echo "shfmt not installed — skipping. brew install shfmt to enable." ; \
	fi

.PHONY: format-plugin
format-plugin: ## Apply dotnet format to plugin sources
	@if [ -d "$(PLUGIN_DIR)" ] && [ -n "$$(find $(PLUGIN_DIR) -name '*.csproj' -print -quit)" ]; then \
		cd $(PLUGIN_DIR) && dotnet format ; \
	fi

.PHONY: format-vscode
format-vscode: ## Apply prettier + eslint --fix to extension sources
	@if [ -f "$(VSCODE_EXT_DIR)/package.json" ]; then \
		cd $(VSCODE_EXT_DIR) && npm run format && npm run lint:fix ; \
	fi

.PHONY: install-hooks
install-hooks: ## Merge session-monitor.sh into ~/.claude/settings.json
	@bash $(HOOKS_DIR)/install.sh

.PHONY: clean
clean: ## Remove build artefacts
	@find $(PLUGIN_DIR) -type d -name 'bin' -print0 2>/dev/null | xargs -0 -I {} rm -rf -- {}
	@find $(PLUGIN_DIR) -type d -name 'obj' -print0 2>/dev/null | xargs -0 -I {} rm -rf -- {}
	@rm -rf -- $(VSCODE_EXT_DIR)/dist $(VSCODE_EXT_DIR)/node_modules $(VSCODE_EXT_DIR)/*.vsix 2>/dev/null || true
