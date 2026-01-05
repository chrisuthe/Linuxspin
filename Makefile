# =============================================================================
# Sendspin Linux Client - Makefile
# =============================================================================
# Common build targets for development and CI/CD.
#
# Usage:
#   make              # Build debug
#   make release      # Build release
#   make test         # Run tests
#   make appimage     # Build AppImage
#   make deploy       # Deploy to test machine
#
# Author: Sendspin Team
# =============================================================================

.PHONY: all build release clean test coverage publish appimage deb flatpak \
        deploy run help restore check-env install-tools

# =============================================================================
# Configuration
# =============================================================================

# Project settings
PROJECT_NAME := SendspinClient.Linux
SOLUTION := SendspinClient.Linux.sln
MAIN_PROJECT := src/SendspinClient.Linux/SendspinClient.Linux.csproj
TEST_PROJECT := src/SendspinClient.Linux.Tests/SendspinClient.Linux.Tests.csproj

# Build settings
CONFIGURATION ?= Debug
RUNTIME ?= linux-x64
ARTIFACTS_DIR := artifacts
VERSION ?= 1.0.0

# Deployment settings (override with environment or command line)
DEPLOY_HOST ?= $(SENDSPIN_DEPLOY_HOST)
DEPLOY_USER ?= $(USER)
DEPLOY_PATH ?= ~/sendspin

# Colors for output
BLUE := \033[0;34m
GREEN := \033[0;32m
YELLOW := \033[1;33m
RED := \033[0;31m
NC := \033[0m

# =============================================================================
# Default Target
# =============================================================================

all: build

# =============================================================================
# Help
# =============================================================================

help:
	@echo ""
	@echo "$(BLUE)Sendspin Linux Client - Build Targets$(NC)"
	@echo ""
	@echo "$(GREEN)Build:$(NC)"
	@echo "  make              Build debug configuration"
	@echo "  make build        Build debug configuration"
	@echo "  make release      Build release configuration"
	@echo "  make clean        Clean all build artifacts"
	@echo "  make restore      Restore NuGet packages"
	@echo ""
	@echo "$(GREEN)Testing:$(NC)"
	@echo "  make test         Run all tests"
	@echo "  make coverage     Run tests with code coverage"
	@echo "  make test-watch   Run tests in watch mode"
	@echo ""
	@echo "$(GREEN)Publishing:$(NC)"
	@echo "  make publish      Create publishable artifacts"
	@echo "  make publish-all  Publish for all architectures"
	@echo ""
	@echo "$(GREEN)Packaging:$(NC)"
	@echo "  make appimage     Build AppImage package"
	@echo "  make deb          Build .deb package"
	@echo "  make flatpak      Build Flatpak package"
	@echo "  make packages     Build all packages"
	@echo ""
	@echo "$(GREEN)Deployment:$(NC)"
	@echo "  make deploy       Deploy to test machine"
	@echo "  make deploy-run   Deploy and run"
	@echo "  make run-remote   Run on remote machine"
	@echo ""
	@echo "$(GREEN)Development:$(NC)"
	@echo "  make watch        Watch for changes and rebuild"
	@echo "  make format       Format source code"
	@echo "  make lint         Run code analysis"
	@echo ""
	@echo "$(YELLOW)Configuration:$(NC)"
	@echo "  CONFIGURATION     Build config (Debug|Release). Default: Debug"
	@echo "  RUNTIME           Target runtime. Default: linux-x64"
	@echo "  DEPLOY_HOST       Remote hostname for deployment"
	@echo "  DEPLOY_USER       Remote username. Default: current user"
	@echo "  VERSION           Package version. Default: 1.0.0"
	@echo ""
	@echo "$(YELLOW)Examples:$(NC)"
	@echo "  make release"
	@echo "  make test CONFIGURATION=Release"
	@echo "  make deploy DEPLOY_HOST=fedora.local"
	@echo "  make appimage VERSION=1.2.3"
	@echo ""

# =============================================================================
# Environment Check
# =============================================================================

check-env:
	@command -v dotnet >/dev/null 2>&1 || { echo "$(RED)Error: dotnet not found$(NC)"; exit 1; }
	@echo "$(GREEN)Environment OK$(NC)"

# =============================================================================
# Build Targets
# =============================================================================

restore:
	@echo "$(BLUE)Restoring packages...$(NC)"
	dotnet restore $(SOLUTION) --runtime $(RUNTIME)

build: restore
	@echo "$(BLUE)Building $(CONFIGURATION) for $(RUNTIME)...$(NC)"
	dotnet build $(SOLUTION) \
		--configuration $(CONFIGURATION) \
		--runtime $(RUNTIME) \
		--no-restore

release:
	@$(MAKE) build CONFIGURATION=Release

clean:
	@echo "$(BLUE)Cleaning...$(NC)"
	dotnet clean $(SOLUTION) --configuration Debug 2>/dev/null || true
	dotnet clean $(SOLUTION) --configuration Release 2>/dev/null || true
	rm -rf $(ARTIFACTS_DIR)
	rm -rf src/*/bin src/*/obj
	@echo "$(GREEN)Clean complete$(NC)"

# =============================================================================
# Test Targets
# =============================================================================

test: build
	@echo "$(BLUE)Running tests...$(NC)"
	dotnet test $(TEST_PROJECT) \
		--configuration $(CONFIGURATION) \
		--no-build \
		--logger "console;verbosity=normal"

coverage: build
	@echo "$(BLUE)Running tests with coverage...$(NC)"
	dotnet test $(TEST_PROJECT) \
		--configuration $(CONFIGURATION) \
		--no-build \
		--collect:"XPlat Code Coverage" \
		--results-directory $(ARTIFACTS_DIR)/test-results \
		-- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover
	@echo "$(GREEN)Coverage report: $(ARTIFACTS_DIR)/test-results$(NC)"

test-watch:
	@echo "$(BLUE)Starting test watch mode...$(NC)"
	./scripts/test.sh --watch

# =============================================================================
# Publish Targets
# =============================================================================

publish: restore
	@echo "$(BLUE)Publishing $(CONFIGURATION) for $(RUNTIME)...$(NC)"
	@mkdir -p $(ARTIFACTS_DIR)/$(RUNTIME)
	dotnet publish $(MAIN_PROJECT) \
		--configuration Release \
		--runtime $(RUNTIME) \
		--self-contained true \
		--output $(ARTIFACTS_DIR)/$(RUNTIME) \
		-p:PublishSingleFile=true \
		-p:IncludeNativeLibrariesForSelfExtract=true \
		-p:EnableCompressionInSingleFile=true \
		-p:Version=$(VERSION)
	@chmod +x $(ARTIFACTS_DIR)/$(RUNTIME)/sendspin 2>/dev/null || \
	 chmod +x $(ARTIFACTS_DIR)/$(RUNTIME)/SendspinClient.Linux 2>/dev/null || true
	@echo "$(GREEN)Published to: $(ARTIFACTS_DIR)/$(RUNTIME)$(NC)"

publish-all:
	@$(MAKE) publish RUNTIME=linux-x64
	@$(MAKE) publish RUNTIME=linux-arm64

# =============================================================================
# Packaging Targets
# =============================================================================

appimage: publish
	@echo "$(BLUE)Building AppImage...$(NC)"
	APP_VERSION=$(VERSION) ./scripts/build-appimage.sh
	@echo "$(GREEN)AppImage: dist/Sendspin-*.AppImage$(NC)"

deb: publish
	@echo "$(BLUE)Building .deb package...$(NC)"
	@echo "$(YELLOW)Note: .deb packaging not yet implemented$(NC)"

flatpak: publish
	@echo "$(BLUE)Building Flatpak...$(NC)"
	APP_VERSION=$(VERSION) ./scripts/build-flatpak.sh
	@echo "$(GREEN)Flatpak: dist/Sendspin-*.flatpak$(NC)"

packages: publish
	@echo "$(BLUE)Building all packages...$(NC)"
	APP_VERSION=$(VERSION) ./scripts/build-packages.sh --all
	@echo ""
	@echo "$(GREEN)Packages built:$(NC)"
	@ls -la dist/*.AppImage dist/*.flatpak 2>/dev/null || true

# =============================================================================
# Deployment Targets
# =============================================================================

deploy: publish
ifndef DEPLOY_HOST
	$(error DEPLOY_HOST is not set. Use: make deploy DEPLOY_HOST=hostname)
endif
	@echo "$(BLUE)Deploying to $(DEPLOY_USER)@$(DEPLOY_HOST)...$(NC)"
	./scripts/deploy.sh $(DEPLOY_HOST) \
		--user $(DEPLOY_USER) \
		--path $(DEPLOY_PATH) \
		--source $(ARTIFACTS_DIR)/$(RUNTIME)

deploy-run: publish
ifndef DEPLOY_HOST
	$(error DEPLOY_HOST is not set. Use: make deploy-run DEPLOY_HOST=hostname)
endif
	@echo "$(BLUE)Deploying and running on $(DEPLOY_USER)@$(DEPLOY_HOST)...$(NC)"
	./scripts/deploy.sh $(DEPLOY_HOST) \
		--user $(DEPLOY_USER) \
		--path $(DEPLOY_PATH) \
		--source $(ARTIFACTS_DIR)/$(RUNTIME) \
		--kill --run --attach

run-remote:
ifndef DEPLOY_HOST
	$(error DEPLOY_HOST is not set. Use: make run-remote DEPLOY_HOST=hostname)
endif
	@echo "$(BLUE)Running on $(DEPLOY_USER)@$(DEPLOY_HOST)...$(NC)"
	ssh $(DEPLOY_USER)@$(DEPLOY_HOST) "cd $(DEPLOY_PATH) && ./sendspin"

# =============================================================================
# Development Targets
# =============================================================================

watch:
	@echo "$(BLUE)Watching for changes...$(NC)"
	dotnet watch --project $(MAIN_PROJECT) build

format:
	@echo "$(BLUE)Formatting code...$(NC)"
	dotnet format $(SOLUTION)

lint:
	@echo "$(BLUE)Running code analysis...$(NC)"
	dotnet build $(SOLUTION) \
		--configuration Release \
		-p:TreatWarningsAsErrors=true \
		-p:EnforceCodeStyleInBuild=true

# =============================================================================
# CI/CD Helpers
# =============================================================================

ci-build:
	@$(MAKE) clean
	@$(MAKE) build CONFIGURATION=Release
	@$(MAKE) test CONFIGURATION=Release

ci-package:
	@$(MAKE) clean
	@$(MAKE) packages

# Generate version from git
version:
	@echo "$(shell git describe --tags --always --dirty 2>/dev/null || echo $(VERSION))"

# =============================================================================
# Tool Installation
# =============================================================================

install-tools:
	@echo "$(BLUE)Installing development tools...$(NC)"
	dotnet tool install -g dotnet-reportgenerator-globaltool 2>/dev/null || true
	dotnet tool install -g dotnet-format 2>/dev/null || true
	@echo "$(GREEN)Tools installed$(NC)"
