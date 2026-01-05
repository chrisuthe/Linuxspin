#!/bin/bash
# =============================================================================
# Sendspin Linux Client - Build All Packages
# =============================================================================
# Builds both AppImage and Flatpak packages
#
# Usage:
#   ./scripts/build-packages.sh [--appimage] [--flatpak] [--all]
#
# Options:
#   --appimage    Build AppImage only
#   --flatpak     Build Flatpak only
#   --all         Build all packages (default)
# =============================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

BUILD_APPIMAGE=false
BUILD_FLATPAK=false

# Parse arguments
if [ $# -eq 0 ]; then
    BUILD_APPIMAGE=true
    BUILD_FLATPAK=true
else
    for arg in "$@"; do
        case $arg in
            --appimage)
                BUILD_APPIMAGE=true
                ;;
            --flatpak)
                BUILD_FLATPAK=true
                ;;
            --all)
                BUILD_APPIMAGE=true
                BUILD_FLATPAK=true
                ;;
            *)
                echo "Unknown option: $arg"
                echo "Usage: $0 [--appimage] [--flatpak] [--all]"
                exit 1
                ;;
        esac
    done
fi

echo "=========================================="
echo "Sendspin Package Builder"
echo "=========================================="
echo ""

# Build AppImage
if [ "$BUILD_APPIMAGE" = true ]; then
    echo ">>> Building AppImage..."
    "${SCRIPT_DIR}/build-appimage.sh"
    echo ""
fi

# Build Flatpak
if [ "$BUILD_FLATPAK" = true ]; then
    echo ">>> Building Flatpak..."
    "${SCRIPT_DIR}/build-flatpak.sh"
    echo ""
fi

echo "=========================================="
echo "All requested packages built!"
echo "Check the dist/ directory for output files."
echo "=========================================="
