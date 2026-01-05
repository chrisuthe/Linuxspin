#!/bin/bash
# =============================================================================
# Sendspin Linux Client - AppImage Build Script
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
mkdir -p "${BUILD_DIR}/usr/share/icons/hicolor/256x256/apps"
mkdir -p "${BUILD_DIR}/usr/share/metainfo"

# Copy application files
echo "Copying application files..."
cp -r "${PROJECT_ROOT}/publish/linux-x64/"* "${BUILD_DIR}/usr/bin/"

# Make main executable runnable
chmod +x "${BUILD_DIR}/usr/bin/SendspinClient.Linux"

# Copy AppRun script
cp "${PROJECT_ROOT}/packaging/appimage/AppRun" "${BUILD_DIR}/"
chmod +x "${BUILD_DIR}/AppRun"

# Copy desktop file to root (required by AppImage)
cp "${PROJECT_ROOT}/packaging/appimage/sendspin.desktop" "${BUILD_DIR}/sendspin.desktop"
cp "${PROJECT_ROOT}/packaging/appimage/sendspin.desktop" "${BUILD_DIR}/usr/share/applications/"

# Create a placeholder icon if none exists
ICON_SRC="${PROJECT_ROOT}/src/SendspinClient.Linux/Assets/sendspin.png"
if [ -f "${ICON_SRC}" ]; then
    cp "${ICON_SRC}" "${BUILD_DIR}/sendspin.png"
    cp "${ICON_SRC}" "${BUILD_DIR}/usr/share/icons/hicolor/256x256/apps/sendspin.png"
else
    echo "Warning: No icon found, creating placeholder..."
    # Create a simple placeholder icon using ImageMagick if available
    if command -v convert &> /dev/null; then
        convert -size 256x256 xc:#6366f1 -fill white -gravity center \
                -pointsize 72 -annotate 0 "S" "${BUILD_DIR}/sendspin.png"
        cp "${BUILD_DIR}/sendspin.png" "${BUILD_DIR}/usr/share/icons/hicolor/256x256/apps/"
    else
        echo "  (install ImageMagick for auto-generated placeholder icon)"
        # Create minimal 1x1 PNG as fallback
        echo -ne '\x89PNG\r\n\x1a\n\x00\x00\x00\rIHDR\x00\x00\x00\x01\x00\x00\x00\x01\x08\x02\x00\x00\x00\x90wS\xde\x00\x00\x00\x0cIDATx\x9cc\xf8\x0f\x00\x00\x01\x01\x00\x05\x18\xd8N\x00\x00\x00\x00IEND\xaeB`\x82' > "${BUILD_DIR}/sendspin.png"
    fi
fi

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
