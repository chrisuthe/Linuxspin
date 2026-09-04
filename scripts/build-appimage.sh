#!/bin/bash
# =============================================================================
# Sendspin Player - AppImage Build Script
# =============================================================================
# Run this script on Linux (Fedora) after cross-compiling from Windows
#
# Usage:
#   ./scripts/build-appimage.sh
#
# Requirements:
#   - appimagetool (will be downloaded if not present)
#   - Published app in publish/linux-x64/
# =============================================================================

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
BUILD_DIR="${PROJECT_ROOT}/build/appimage"
OUTPUT_DIR="${PROJECT_ROOT}/dist"
APP_NAME="Sendspin"
APP_VERSION="${APP_VERSION:-1.0.0}"

echo "=========================================="
echo "Building Sendspin AppImage v${APP_VERSION}"
echo "=========================================="

# Check if published app exists
if [ ! -d "${PROJECT_ROOT}/publish/linux-x64" ]; then
    echo "Error: Published app not found at publish/linux-x64/"
    echo "Run 'dotnet publish -c Release -r linux-x64 --self-contained' first"
    exit 1
fi

# Download appimagetool if not present
APPIMAGETOOL="${PROJECT_ROOT}/tools/appimagetool-x86_64.AppImage"
if [ ! -f "${APPIMAGETOOL}" ]; then
    echo "Downloading appimagetool..."
    mkdir -p "${PROJECT_ROOT}/tools"
    wget -q "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage" \
         -O "${APPIMAGETOOL}"
    chmod +x "${APPIMAGETOOL}"
fi

# Clean and create build directory
echo "Preparing AppDir structure..."
rm -rf "${BUILD_DIR}"
mkdir -p "${BUILD_DIR}/usr/bin"
mkdir -p "${BUILD_DIR}/usr/lib"
mkdir -p "${BUILD_DIR}/usr/share/applications"
mkdir -p "${BUILD_DIR}/usr/share/metainfo"

# Copy application files
echo "Copying application files..."
cp -r "${PROJECT_ROOT}/publish/linux-x64/"* "${BUILD_DIR}/usr/bin/"

# Make main executable runnable
chmod +x "${BUILD_DIR}/usr/bin/Sendspin.Player"

# Copy AppRun script
cp "${PROJECT_ROOT}/packaging/appimage/AppRun" "${BUILD_DIR}/"
chmod +x "${BUILD_DIR}/AppRun"

# The checked-in desktop file, the same one CI and build.sh install. appimagetool reads the
# copy at the AppDir root; the one under applications/ is what lands on the user's system.
cp "${PROJECT_ROOT}/packaging/io.sendspin.client.desktop" \
   "${BUILD_DIR}/io.sendspin.client.desktop"
cp "${PROJECT_ROOT}/packaging/io.sendspin.client.desktop" \
   "${BUILD_DIR}/usr/share/applications/"

# The committed icon theme, every size. There is no fallback for a missing icon because
# there is no case for one: the set is generated from packaging/icons/sendspin.svg and
# committed, so an absent file means a broken checkout, which install exits on.
echo "Installing icons..."
for dir in "${PROJECT_ROOT}"/packaging/icons/hicolor/*/apps; do
    size_dir="$(basename "$(dirname "${dir}")")"
    install -Dm644 "${dir}"/io.sendspin.client.* \
        -t "${BUILD_DIR}/usr/share/icons/hicolor/${size_dir}/apps"
done

# appimagetool wants the Icon= key's file at the AppDir root as well.
cp "${PROJECT_ROOT}/packaging/icons/hicolor/256x256/apps/io.sendspin.client.png" \
   "${BUILD_DIR}/io.sendspin.client.png"

# Create output directory
mkdir -p "${OUTPUT_DIR}"

# Build AppImage
echo "Building AppImage..."
ARCH=x86_64 "${APPIMAGETOOL}" "${BUILD_DIR}" "${OUTPUT_DIR}/Sendspin-${APP_VERSION}-x86_64.AppImage"

echo ""
echo "=========================================="
echo "AppImage created successfully!"
echo "Output: ${OUTPUT_DIR}/Sendspin-${APP_VERSION}-x86_64.AppImage"
echo ""
echo "To run:"
echo "  chmod +x ${OUTPUT_DIR}/Sendspin-${APP_VERSION}-x86_64.AppImage"
echo "  ${OUTPUT_DIR}/Sendspin-${APP_VERSION}-x86_64.AppImage"
echo "=========================================="
