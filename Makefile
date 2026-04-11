.DEFAULT_GOAL := help

SHELL := /bin/bash
.SHELLFLAGS := -o errexit -o nounset -o pipefail -c

# Directories
HOOKS_DIR           := hooks
PLUGIN_DIR          := plugin
PLUGIN_CSPROJ       := $(PLUGIN_DIR)/MacroClaudePlugin/src/MacroClaudePlugin.csproj
PLUGIN_TESTS_CSPROJ := $(PLUGIN_DIR)/MacroClaudePlugin.Tests/MacroClaudePlugin.Tests.csproj
PLUGIN_RELEASE_DIR  := $(PLUGIN_DIR)/MacroClaudePlugin/bin/Release
VSCODE_EXT_DIR      := vscode-extension
DIST_DIR            := dist

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

.PHONY: test
test: ## Run xunit tests against the plugin pure logic
	@dotnet test $(PLUGIN_TESTS_CSPROJ) --nologo

.PHONY: install-hooks
install-hooks: ## Merge session-monitor.sh into ~/.claude/settings.json
	@bash $(HOOKS_DIR)/install.sh

# ----------------------------------------------------------------
# Release targets — build distributable artefacts in $(DIST_DIR).
# ----------------------------------------------------------------

.PHONY: release
release: release-plugin release-vsix ## Build both .lplug4 and .vsix for a release

.PHONY: release-plugin
release-plugin: ## Build macropad plugin .lplug4 (requires macOS + LPS)
	@mkdir -p $(DIST_DIR)
	@dotnet build $(PLUGIN_CSPROJ) --configuration Release --nologo
	@dotnet logiplugintool pack $(PLUGIN_RELEASE_DIR) $(DIST_DIR)/MacroClaudePlugin.lplug4
	@dotnet logiplugintool verify $(DIST_DIR)/MacroClaudePlugin.lplug4
	@dotnet logiplugintool metadata $(DIST_DIR)/MacroClaudePlugin.lplug4 | head -20
	@echo "built: $(DIST_DIR)/MacroClaudePlugin.lplug4"

.PHONY: release-vsix
release-vsix: ## Build VS Code companion extension .vsix
	@mkdir -p $(DIST_DIR)
	@cd $(VSCODE_EXT_DIR) && npm ci && npm run compile
	@cd $(VSCODE_EXT_DIR) && npx vsce package --no-dependencies --out ../$(DIST_DIR)/
	@ls -la $(DIST_DIR)/macro-claude-bridge-*.vsix

.PHONY: release-upload
release-upload: ## Upload $(DIST_DIR)/* to the current GitHub release (needs gh + $(TAG))
	@test -n "$(TAG)" || (echo "usage: make release-upload TAG=v1.0.0" && exit 1)
	@gh release upload "$(TAG)" $(DIST_DIR)/MacroClaudePlugin.lplug4 $(DIST_DIR)/macro-claude-bridge-*.vsix --clobber

.PHONY: clean
clean: ## Remove build artefacts
	@find $(PLUGIN_DIR) -type d -name 'bin' -print0 2>/dev/null | xargs -0 -I {} rm -rf -- {}
	@find $(PLUGIN_DIR) -type d -name 'obj' -print0 2>/dev/null | xargs -0 -I {} rm -rf -- {}
	@rm -rf -- $(VSCODE_EXT_DIR)/dist $(VSCODE_EXT_DIR)/node_modules $(VSCODE_EXT_DIR)/*.vsix 2>/dev/null || true
	@rm -rf -- $(DIST_DIR) 2>/dev/null || true
