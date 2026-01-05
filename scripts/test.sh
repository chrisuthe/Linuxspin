#!/usr/bin/env bash
# =============================================================================
# Sendspin Linux Client - Test Runner Script
# =============================================================================
# This script runs the test suite for the Sendspin Linux client.
# It supports code coverage, filtering, and various output formats.
#
# Usage:
#   ./test.sh                    # Run all tests
#   ./test.sh --coverage         # Run with code coverage
#   ./test.sh --filter "Audio"   # Filter tests by name
#   ./test.sh --watch            # Watch mode for development
#
# Author: Sendspin Team
# =============================================================================

set -euo pipefail

# =============================================================================
# Configuration
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
SOLUTION_FILE="$REPO_ROOT/SendspinClient.Linux.sln"
TEST_PROJECT="$REPO_ROOT/src/SendspinClient.Linux.Tests/SendspinClient.Linux.Tests.csproj"
RESULTS_DIR="$REPO_ROOT/artifacts/test-results"

# Default options
CONFIGURATION="Debug"
FILTER=""
COVERAGE=false
VERBOSE=false
WATCH=false
NO_BUILD=false
OUTPUT_FORMAT="console"  # console, trx, html

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

# =============================================================================
# Helper Functions
# =============================================================================

info() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

success() {
    echo -e "${GREEN}[PASS]${NC} $1"
}

warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

error() {
    echo -e "${RED}[FAIL]${NC} $1"
}

usage() {
    cat << EOF
Usage: $(basename "$0") [OPTIONS]

Test Options:
  -c, --configuration <cfg>  Test configuration (Debug|Release). Default: Debug
  -f, --filter <pattern>     Filter tests by name pattern
  --no-build                Skip building before testing

Coverage:
  --coverage               Generate code coverage report
  --coverage-format <fmt>  Coverage format: opencover, cobertura. Default: opencover

Output:
  -v, --verbose            Verbose test output
  --format <fmt>          Output format: console, trx, html. Default: console
  -o, --output <path>      Results output directory

Development:
  --watch                  Watch for changes and re-run tests
  --list                   List available tests without running

Examples:
  $(basename "$0")                          Run all tests
  $(basename "$0") --coverage              Run with coverage report
  $(basename "$0") --filter "Audio"        Run tests matching "Audio"
  $(basename "$0") --watch --filter Unit   Watch mode for unit tests

EOF
}

# =============================================================================
# Argument Parsing
# =============================================================================

COVERAGE_FORMAT="opencover"

while [[ $# -gt 0 ]]; do
    case $1 in
        -c|--configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        -f|--filter)
            FILTER="$2"
            shift 2
            ;;
        --no-build)
            NO_BUILD=true
            shift
            ;;
        --coverage)
            COVERAGE=true
            shift
            ;;
        --coverage-format)
            COVERAGE_FORMAT="$2"
            shift 2
            ;;
        -v|--verbose)
            VERBOSE=true
            shift
            ;;
        --format)
            OUTPUT_FORMAT="$2"
            shift 2
            ;;
        -o|--output)
            RESULTS_DIR="$2"
            shift 2
            ;;
        --watch)
            WATCH=true
            shift
            ;;
        --list)
            dotnet test "$TEST_PROJECT" --list-tests
            exit 0
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            error "Unknown option: $1"
            usage
            exit 1
            ;;
    esac
done

# =============================================================================
# Validation
# =============================================================================

if ! command -v dotnet &> /dev/null; then
    error ".NET SDK not found. Please install .NET 8.0 SDK"
    exit 1
fi

if [[ ! -f "$TEST_PROJECT" ]]; then
    error "Test project not found: $TEST_PROJECT"
    exit 1
fi

# Create results directory
mkdir -p "$RESULTS_DIR"

# =============================================================================
# Build Test Arguments
# =============================================================================

TEST_ARGS=(
    test "$TEST_PROJECT"
    --configuration "$CONFIGURATION"
    --results-directory "$RESULTS_DIR"
)

if $NO_BUILD; then
    TEST_ARGS+=(--no-build)
fi

if $VERBOSE; then
    TEST_ARGS+=(--logger "console;verbosity=detailed")
else
    TEST_ARGS+=(--logger "console;verbosity=normal")
fi

if [[ -n "$FILTER" ]]; then
    TEST_ARGS+=(--filter "$FILTER")
fi

# Output format
case "$OUTPUT_FORMAT" in
    trx)
        TEST_ARGS+=(--logger "trx;LogFileName=test-results.trx")
        ;;
    html)
        TEST_ARGS+=(--logger "html;LogFileName=test-results.html")
        ;;
    console)
        # Default, no additional args needed
        ;;
esac

# Coverage
if $COVERAGE; then
    TEST_ARGS+=(--collect:"XPlat Code Coverage")
    TEST_ARGS+=(--)
    TEST_ARGS+=(DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format="$COVERAGE_FORMAT")
fi

# =============================================================================
# Run Tests
# =============================================================================

run_tests() {
    info "Running tests..."
    info "  Configuration: $CONFIGURATION"
    [[ -n "$FILTER" ]] && info "  Filter: $FILTER"
    $COVERAGE && info "  Coverage: enabled ($COVERAGE_FORMAT)"
    echo ""

    if dotnet "${TEST_ARGS[@]}"; then
        echo ""
        success "All tests passed!"
        return 0
    else
        echo ""
        error "Some tests failed!"
        return 1
    fi
}

# =============================================================================
# Watch Mode
# =============================================================================

if $WATCH; then
    info "Starting watch mode..."
    info "Watching for changes in: $REPO_ROOT/src"
    info "Press Ctrl+C to stop"
    echo ""

    # Check for inotify-tools or fswatch
    if command -v inotifywait &> /dev/null; then
        WATCH_CMD="inotifywait"
    elif command -v fswatch &> /dev/null; then
        WATCH_CMD="fswatch"
    else
        error "Watch mode requires inotify-tools or fswatch"
        info "Install with: sudo dnf install inotify-tools"
        exit 1
    fi

    # Initial run
    run_tests || true

    # Watch loop
    while true; do
        echo ""
        info "Waiting for changes..."

        if [[ "$WATCH_CMD" == "inotifywait" ]]; then
            inotifywait -r -e modify,create,delete \
                --include '.*\.cs$' \
                "$REPO_ROOT/src" 2>/dev/null
        else
            fswatch -1 -r --include '\.cs$' "$REPO_ROOT/src"
        fi

        echo ""
        info "Changes detected, re-running tests..."
        sleep 0.5  # Debounce

        # Re-run tests (don't exit on failure in watch mode)
        NO_BUILD=false  # Always rebuild in watch mode
        run_tests || true
    done
else
    # Single run
    run_tests
    TEST_EXIT_CODE=$?

    # Coverage report
    if $COVERAGE && [[ -d "$RESULTS_DIR" ]]; then
        echo ""
        info "Coverage reports:"

        find "$RESULTS_DIR" -name "coverage.*" -type f | while read -r file; do
            info "  $file"
        done

        # Try to generate HTML report if reportgenerator is available
        if command -v reportgenerator &> /dev/null; then
            COVERAGE_FILE=$(find "$RESULTS_DIR" -name "coverage.${COVERAGE_FORMAT}.xml" -type f | head -1)
            if [[ -n "$COVERAGE_FILE" ]]; then
                info "Generating HTML coverage report..."
                reportgenerator \
                    -reports:"$COVERAGE_FILE" \
                    -targetdir:"$RESULTS_DIR/coverage-report" \
                    -reporttypes:Html

                success "HTML report: $RESULTS_DIR/coverage-report/index.html"
            fi
        else
            info "Install reportgenerator for HTML coverage reports:"
            info "  dotnet tool install -g dotnet-reportgenerator-globaltool"
        fi
    fi

    exit $TEST_EXIT_CODE
fi
