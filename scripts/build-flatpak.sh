#!/bin/bash
# =============================================================================
# Sendspin Linux Client - Flatpak Build Script
# =============================================================================
# Run this script on Linux (Fedora) after cross-compiling from Windows
#
# Usage:
#   ./scripts/build-flatpak.sh
#
# Requirements:
#   - flatpak-builder
#   - org.freedesktop.Platform//23.08 and Sdk installed
#   - Published app in publish/linux-x64/
# =============================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
FLATPAK_DIR="${PROJECT_ROOT}/packaging/flatpak"
OUTPUT_DIR="${PROJECT_ROOT}/dist"
APP_ID="io.sendspin.client"
APP_VERSION="${APP_VERSION:-1.0.0}"

echo "=========================================="
echo "Building Sendspin Flatpak v${APP_VERSION}"
echo "=========================================="

# Check if published app exists
if [ ! -d "${PROJECT_ROOT}/publish/linux-x64" ]; then
    echo "Error: Published app not found at publish/linux-x64/"
    echo "Run 'dotnet publish -c Release -r linux-x64 --self-contained' first"
    exit 1
fi

# Check for flatpak-builder
if ! command -v flatpak-builder &> /dev/null; then
    echo "Error: flatpak-builder not found"
    echo "Install with: sudo dnf install flatpak-builder"
    exit 1
fi

# Check for required runtime
if ! flatpak info org.freedesktop.Platform//23.08 &> /dev/null; then
    echo "Installing Freedesktop Platform 23.08..."
    flatpak install -y flathub org.freedesktop.Platform//23.08 org.freedesktop.Sdk//23.08
fi

# Create output directory
mkdir -p "${OUTPUT_DIR}"

# Build Flatpak
echo "Building Flatpak..."
cd "${FLATPAK_DIR}"

flatpak-builder --user --force-clean --repo=repo build-dir "${APP_ID}.yml"

# Create bundle
echo "Creating Flatpak bundle..."
flatpak build-bundle repo "${OUTPUT_DIR}/Sendspin-${APP_VERSION}.flatpak" "${APP_ID}"

echo ""
echo "=========================================="
echo "Flatpak created successfully!"
echo "Output: ${OUTPUT_DIR}/Sendspin-${APP_VERSION}.flatpak"
echo ""
echo "To install:"
echo "  flatpak install --user ${OUTPUT_DIR}/Sendspin-${APP_VERSION}.flatpak"
echo ""
echo "To run:"
echo "  flatpak run ${APP_ID}"
echo "=========================================="
