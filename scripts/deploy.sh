#!/usr/bin/env bash
# =============================================================================
# Sendspin Linux Client - Deployment Script for Fedora Test Machine
# =============================================================================
# This script deploys the built application to a remote Linux machine for testing.
# It uses rsync for efficient incremental file transfers.
#
# Usage:
#   ./deploy.sh <hostname>           Deploy to remote host
#   ./deploy.sh <hostname> --run     Deploy and run
#   ./deploy.sh <hostname> --watch   Watch mode for development
#
# Author: Sendspin Team
# =============================================================================

set -euo pipefail

# =============================================================================
# Configuration
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
ARTIFACTS_DIR="$REPO_ROOT/artifacts/linux-x64"

# Default options
TARGET_HOST="${1:-${SENDSPIN_DEPLOY_HOST:-}}"
TARGET_USER="${SENDSPIN_DEPLOY_USER:-$USER}"
TARGET_PATH="${SENDSPIN_DEPLOY_PATH:-~/sendspin}"
SSH_PORT="${SENDSPIN_DEPLOY_PORT:-22}"
SSH_KEY="${SENDSPIN_DEPLOY_KEY:-}"

RUN_AFTER=false
ATTACH=false
KILL_EXISTING=false
DEBUG_MODE=false
WATCH_MODE=false
DRY_RUN=false

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
GRAY='\033[0;90m'
NC='\033[0m'

# =============================================================================
# Helper Functions
# =============================================================================

info() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

success() {
    echo -e "${GREEN}[OK]${NC} $1"
}

warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

error() {
    echo -e "${RED}[ERROR]${NC} $1"
    exit 1
}

debug_msg() {
    echo -e "${GRAY}  > $1${NC}"
}

usage() {
    cat << EOF
Usage: $(basename "$0") [HOSTNAME] [OPTIONS]

Deployment:
  HOSTNAME                 Target hostname or IP (or set SENDSPIN_DEPLOY_HOST)
  -u, --user <user>        SSH username (default: current user)
  -p, --path <path>        Remote deployment path (default: ~/sendspin)
  --port <port>            SSH port (default: 22)
  -k, --key <file>         SSH private key file

Actions:
  -r, --run                Run application after deployment
  -a, --attach             Attach to application output (implies --run)
  --kill                   Kill existing process before deployment
  --debug                  Deploy debug build and setup remote debugging

Development:
  -w, --watch              Watch for changes and auto-deploy
  --dry-run               Show what would be deployed

Source:
  -s, --source <path>      Source directory (default: artifacts/linux-x64)

Environment Variables:
  SENDSPIN_DEPLOY_HOST     Default target hostname
  SENDSPIN_DEPLOY_USER     Default SSH username
  SENDSPIN_DEPLOY_PATH     Default remote path
  SENDSPIN_DEPLOY_PORT     Default SSH port
  SENDSPIN_DEPLOY_KEY      Default SSH key file

Examples:
  $(basename "$0") fedora-test.local
  $(basename "$0") 192.168.1.50 --run --attach
  $(basename "$0") dev-server --kill --run
  $(basename "$0") --watch

EOF
}

# =============================================================================
# Argument Parsing
# =============================================================================

# Skip first arg if it looks like a hostname (doesn't start with -)
shift_host=false
if [[ -n "${1:-}" ]] && [[ "${1:-}" != -* ]]; then
    TARGET_HOST="$1"
    shift_host=true
fi

# Parse remaining arguments
ARGS=("$@")
if $shift_host; then
    ARGS=("${@:2}")
fi

while [[ ${#ARGS[@]} -gt 0 ]]; do
    case ${ARGS[0]} in
        -u|--user)
            TARGET_USER="${ARGS[1]}"
            ARGS=("${ARGS[@]:2}")
            ;;
        -p|--path)
            TARGET_PATH="${ARGS[1]}"
            ARGS=("${ARGS[@]:2}")
            ;;
        --port)
            SSH_PORT="${ARGS[1]}"
            ARGS=("${ARGS[@]:2}")
            ;;
        -k|--key)
            SSH_KEY="${ARGS[1]}"
            ARGS=("${ARGS[@]:2}")
            ;;
        -s|--source)
            ARTIFACTS_DIR="${ARGS[1]}"
            ARGS=("${ARGS[@]:2}")
            ;;
        -r|--run)
            RUN_AFTER=true
            ARGS=("${ARGS[@]:1}")
            ;;
        -a|--attach)
            ATTACH=true
            RUN_AFTER=true
            ARGS=("${ARGS[@]:1}")
            ;;
        --kill)
            KILL_EXISTING=true
            ARGS=("${ARGS[@]:1}")
            ;;
        --debug)
            DEBUG_MODE=true
            ARGS=("${ARGS[@]:1}")
            ;;
        -w|--watch)
            WATCH_MODE=true
            ARGS=("${ARGS[@]:1}")
            ;;
        --dry-run)
            DRY_RUN=true
            ARGS=("${ARGS[@]:1}")
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            error "Unknown option: ${ARGS[0]}"
            ;;
    esac
done

# =============================================================================
# Configuration File Support
# =============================================================================

CONFIG_FILE="$REPO_ROOT/.deploy.json"
if [[ -f "$CONFIG_FILE" ]]; then
    info "Loading configuration from .deploy.json"

    # Parse JSON with jq if available, otherwise use grep/sed
    if command -v jq &> /dev/null; then
        [[ -z "$TARGET_HOST" ]] && TARGET_HOST=$(jq -r '.host // empty' "$CONFIG_FILE" 2>/dev/null)
        [[ "$TARGET_USER" == "$USER" ]] && TARGET_USER=$(jq -r '.user // empty' "$CONFIG_FILE" 2>/dev/null || echo "$USER")
        TARGET_PATH=$(jq -r '.path // "~/sendspin"' "$CONFIG_FILE" 2>/dev/null)
        SSH_PORT=$(jq -r '.port // 22' "$CONFIG_FILE" 2>/dev/null)
    fi
fi

# =============================================================================
# Validation
# =============================================================================

if [[ -z "$TARGET_HOST" ]]; then
    error "Target host is required. Specify hostname or set SENDSPIN_DEPLOY_HOST"
fi

# Find source directory
if [[ ! -d "$ARTIFACTS_DIR" ]]; then
    # Try debug build
    DEBUG_DIR="$REPO_ROOT/src/SendspinClient.Linux/bin/Debug/net8.0/linux-x64"
    if [[ -d "$DEBUG_DIR" ]]; then
        ARTIFACTS_DIR="$DEBUG_DIR"
        warn "Using debug build from: $ARTIFACTS_DIR"
    else
        error "Source directory not found: $ARTIFACTS_DIR"
    fi
fi

# Check for rsync
if ! command -v rsync &> /dev/null; then
    error "rsync is required. Install with: sudo dnf install rsync"
fi

# Build SSH options
SSH_OPTS=(-o "StrictHostKeyChecking=accept-new" -o "ConnectTimeout=10" -p "$SSH_PORT")
if [[ -n "$SSH_KEY" ]]; then
    SSH_OPTS+=(-i "$SSH_KEY")
fi

# =============================================================================
# SSH Helper Function
# =============================================================================

run_ssh() {
    local cmd="$1"
    local no_throw="${2:-false}"

    debug_msg "ssh ${TARGET_USER}@${TARGET_HOST} \"$cmd\""

    if $DRY_RUN; then
        echo "  [DRY RUN] Would execute: $cmd"
        return 0
    fi

    if $no_throw; then
        ssh "${SSH_OPTS[@]}" "${TARGET_USER}@${TARGET_HOST}" "$cmd" 2>/dev/null || true
    else
        ssh "${SSH_OPTS[@]}" "${TARGET_USER}@${TARGET_HOST}" "$cmd"
    fi
}

# =============================================================================
# Test Connection
# =============================================================================

info "Testing SSH connection to ${TARGET_USER}@${TARGET_HOST}..."

if ! ssh "${SSH_OPTS[@]}" "${TARGET_USER}@${TARGET_HOST}" "echo 'OK'" &>/dev/null; then
    error "Cannot connect to ${TARGET_USER}@${TARGET_HOST}"
fi

success "SSH connection established"

# =============================================================================
# Kill Existing Process
# =============================================================================

if $KILL_EXISTING || $RUN_AFTER || $DEBUG_MODE; then
    info "Checking for existing sendspin process..."
    run_ssh "pkill -f 'sendspin|SendspinClient' 2>/dev/null || true" true
fi

# =============================================================================
# Deploy Files
# =============================================================================

deploy() {
    info "Deploying to ${TARGET_USER}@${TARGET_HOST}:${TARGET_PATH}..."

    # Create remote directory
    run_ssh "mkdir -p $TARGET_PATH"

    # Build rsync command
    RSYNC_OPTS=(
        -avz
        --progress
        --delete
        --exclude '*.pdb'
        --exclude '*.xml'
        -e "ssh ${SSH_OPTS[*]}"
    )

    if $DRY_RUN; then
        RSYNC_OPTS+=(--dry-run)
    fi

    # Run rsync
    rsync "${RSYNC_OPTS[@]}" \
        "${ARTIFACTS_DIR}/" \
        "${TARGET_USER}@${TARGET_HOST}:${TARGET_PATH}/"

    # Set executable permissions
    info "Setting executable permissions..."
    run_ssh "chmod +x $TARGET_PATH/sendspin 2>/dev/null || chmod +x $TARGET_PATH/SendspinClient.Linux 2>/dev/null || true"

    success "Files deployed successfully"
}

deploy

# =============================================================================
# Debug Setup
# =============================================================================

if $DEBUG_MODE; then
    info "Setting up remote debugging..."

    # Check if vsdbg is installed
    VSDBG_CHECK=$(run_ssh "test -f ~/.vsdbg/vsdbg && echo 'found'" true)
    if [[ "$VSDBG_CHECK" != "found" ]]; then
        warn "vsdbg not found on remote. Installing..."
        run_ssh "curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v latest -l ~/.vsdbg"
    fi

    echo ""
    info "Remote debugging setup complete"
    info "To attach from VS Code, add this to launch.json:"
    cat << EOF

{
    "name": "Attach to Sendspin (Remote)",
    "type": "coreclr",
    "request": "attach",
    "processName": "sendspin",
    "pipeTransport": {
        "pipeCwd": "\${workspaceFolder}",
        "pipeProgram": "ssh",
        "pipeArgs": ["-p", "$SSH_PORT", "${TARGET_USER}@${TARGET_HOST}"],
        "debuggerPath": "~/.vsdbg/vsdbg"
    },
    "sourceFileMap": {
        "/home/$TARGET_USER/sendspin": "\${workspaceFolder}"
    }
}

EOF
fi

# =============================================================================
# Run Application
# =============================================================================

if $RUN_AFTER; then
    info "Starting application..."

    RUN_CMD="cd $TARGET_PATH && export DISPLAY=:0 && "
    RUN_CMD+="if [ -f ./sendspin ]; then ./sendspin; else ./SendspinClient.Linux; fi"

    if $ATTACH; then
        echo ""
        echo "=== Application Output ==="
        echo "(Press Ctrl+C to stop)"
        echo ""

        ssh "${SSH_OPTS[@]}" -t "${TARGET_USER}@${TARGET_HOST}" "$RUN_CMD"
    else
        # Run in background
        BG_CMD="cd $TARGET_PATH && nohup bash -c '$RUN_CMD' > /tmp/sendspin.log 2>&1 &"
        run_ssh "$BG_CMD"

        success "Application started in background"
        info "View logs: ssh ${TARGET_USER}@${TARGET_HOST} 'tail -f /tmp/sendspin.log'"
    fi
fi

# =============================================================================
# Watch Mode
# =============================================================================

if $WATCH_MODE; then
    info "Entering watch mode..."
    info "Watching for changes in: $ARTIFACTS_DIR"
    info "Press Ctrl+C to stop"
    echo ""

    # Check for inotifywait or fswatch
    if command -v inotifywait &> /dev/null; then
        WATCH_CMD="inotifywait"
    elif command -v fswatch &> /dev/null; then
        WATCH_CMD="fswatch"
    else
        error "Watch mode requires inotify-tools or fswatch"
    fi

    while true; do
        info "Waiting for changes..."

        if [[ "$WATCH_CMD" == "inotifywait" ]]; then
            inotifywait -r -e modify,create,delete "$ARTIFACTS_DIR" 2>/dev/null
        else
            fswatch -1 -r "$ARTIFACTS_DIR"
        fi

        echo ""
        info "Changes detected, redeploying..."
        sleep 0.5  # Debounce

        KILL_EXISTING=true
        RUN_AFTER=true
        deploy
    done
fi

# =============================================================================
# Summary
# =============================================================================

if ! $WATCH_MODE && ! $ATTACH; then
    echo ""
    success "Deployment complete!"
    echo ""
    info "Remote commands:"
    echo "  Run:    ssh ${TARGET_USER}@${TARGET_HOST} 'cd $TARGET_PATH && ./sendspin'"
    echo "  Logs:   ssh ${TARGET_USER}@${TARGET_HOST} 'tail -f /tmp/sendspin.log'"
    echo "  Kill:   ssh ${TARGET_USER}@${TARGET_HOST} 'pkill -f sendspin'"
    echo "  Shell:  ssh ${TARGET_USER}@${TARGET_HOST}"
fi

exit 0
