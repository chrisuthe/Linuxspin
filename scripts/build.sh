#!/usr/bin/env bash
# =============================================================================
# Sendspin Linux Client - Native Linux Build Script
# =============================================================================
# This script builds the Sendspin Linux client on a Linux development machine
# or CI environment. It supports all packaging formats (AppImage, .deb, Flatpak).
#
# Usage:
#   ./build.sh                    # Quick debug build
#   ./build.sh --release          # Release build
#   ./build.sh --publish          # Create publishable artifacts
#   ./build.sh --appimage         # Build AppImage package
#   ./build.sh --all              # Build all package formats
#
# Author: Sendspin Team
# Requires: .NET 8.0 SDK
# =============================================================================

set -euo pipefail

# =============================================================================
# Configuration
# =============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(dirname "$SCRIPT_DIR")"
SOLUTION_FILE="$REPO_ROOT/SendspinClient.Linux.sln"
MAIN_PROJECT="$REPO_ROOT/src/SendspinClient.Linux/SendspinClient.Linux.csproj"
ARTIFACTS_DIR="$REPO_ROOT/artifacts"

# Default options
CONFIGURATION="Debug"
RUNTIME="linux-x64"
SELF_CONTAINED=false
PUBLISH=false
SINGLE_FILE=false
CLEAN=false
RUN_TESTS=false
BUILD_APPIMAGE=false
BUILD_DEB=false
BUILD_FLATPAK=false
BUILD_ALL=false
VERBOSE=false

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

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

usage() {
    cat << EOF
Usage: $(basename "$0") [OPTIONS]

Build Options:
  -c, --configuration <cfg>  Build configuration (Debug|Release). Default: Debug
  -r, --runtime <rid>        Runtime identifier (linux-x64|linux-arm64). Default: linux-x64
  --release                  Shortcut for --configuration Release
  --self-contained          Create self-contained deployment
  --clean                   Clean before building
  -p, --publish             Create publishable output
  --single-file            Create single-file executable (requires --publish)
  -o, --output <path>       Custom output path for artifacts

Testing:
  -t, --test                Run unit tests after build

Packaging:
  --appimage               Build AppImage package
  --deb                    Build .deb package
  --flatpak                Build Flatpak package
  --all                    Build all package formats

Other:
  -v, --verbose            Verbose output
  -h, --help               Show this help message

Examples:
  $(basename "$0")                         Quick debug build
  $(basename "$0") --release --test        Release build with tests
  $(basename "$0") --publish --appimage    Create AppImage
  $(basename "$0") --all                   Build all packages

EOF
}

# =============================================================================
# Argument Parsing
# =============================================================================

while [[ $# -gt 0 ]]; do
    case $1 in
        -c|--configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        -r|--runtime)
            RUNTIME="$2"
            shift 2
            ;;
        --release)
            CONFIGURATION="Release"
            shift
            ;;
        --self-contained)
            SELF_CONTAINED=true
            shift
            ;;
        --clean)
            CLEAN=true
            shift
            ;;
        -p|--publish)
            PUBLISH=true
            CONFIGURATION="Release"
            shift
            ;;
        --single-file)
            SINGLE_FILE=true
            shift
            ;;
        -o|--output)
            ARTIFACTS_DIR="$2"
            shift 2
            ;;
        -t|--test)
            RUN_TESTS=true
            shift
            ;;
        --appimage)
            BUILD_APPIMAGE=true
            PUBLISH=true
            CONFIGURATION="Release"
            shift
            ;;
        --deb)
            BUILD_DEB=true
            PUBLISH=true
            CONFIGURATION="Release"
            shift
            ;;
        --flatpak)
            BUILD_FLATPAK=true
            PUBLISH=true
            CONFIGURATION="Release"
            shift
            ;;
        --all)
            BUILD_ALL=true
            PUBLISH=true
            CONFIGURATION="Release"
            shift
            ;;
        -v|--verbose)
            VERBOSE=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        *)
            error "Unknown option: $1"
            ;;
    esac
done

# Handle --all flag
if $BUILD_ALL; then
    BUILD_APPIMAGE=true
    BUILD_DEB=true
    BUILD_FLATPAK=true
fi

# Set self-contained default for Release
if [[ "$CONFIGURATION" == "Release" ]] && ! $SELF_CONTAINED; then
    SELF_CONTAINED=true
fi

# Verbose mode
if $VERBOSE; then
    VERBOSITY="normal"
else
    VERBOSITY="minimal"
fi

# =============================================================================
# Validation
# =============================================================================

info "Validating build environment..."

# Check .NET SDK
if ! command -v dotnet &> /dev/null; then
    error ".NET SDK not found. Please install .NET 8.0 SDK"
fi

DOTNET_VERSION=$(dotnet --version)
info "Found .NET SDK version: $DOTNET_VERSION"

if [[ ! "$DOTNET_VERSION" =~ ^8\. ]]; then
    warn ".NET 8.x SDK is recommended. Current version: $DOTNET_VERSION"
fi

# Verify solution exists
if [[ ! -f "$SOLUTION_FILE" ]]; then
    error "Solution file not found: $SOLUTION_FILE"
fi

success "Build environment validated"

# Print configuration
echo ""
info "Build Configuration:"
info "  Configuration: $CONFIGURATION"
info "  Runtime: $RUNTIME"
info "  Self-Contained: $SELF_CONTAINED"
info "  Publish: $PUBLISH"
info "  Single File: $SINGLE_FILE"
echo ""

# =============================================================================
# Clean (if requested)
# =============================================================================

if $CLEAN; then
    info "Cleaning build artifacts..."

    rm -rf "$ARTIFACTS_DIR"
    rm -rf "$REPO_ROOT"/src/*/bin
    rm -rf "$REPO_ROOT"/src/*/obj

    dotnet clean "$SOLUTION_FILE" --configuration "$CONFIGURATION" --verbosity minimal 2>/dev/null || true

    success "Clean complete"
fi

# =============================================================================
# Restore
# =============================================================================

info "Restoring NuGet packages..."

dotnet restore "$SOLUTION_FILE" \
    --runtime "$RUNTIME" \
    --verbosity "$VERBOSITY"

success "Packages restored"

# =============================================================================
# Build
# =============================================================================

info "Building solution..."

BUILD_ARGS=(
    build "$SOLUTION_FILE"
    --configuration "$CONFIGURATION"
    --runtime "$RUNTIME"
    --no-restore
    --verbosity "$VERBOSITY"
)

if $SELF_CONTAINED; then
    BUILD_ARGS+=(--self-contained true)
fi

dotnet "${BUILD_ARGS[@]}"

success "Build complete"

# =============================================================================
# Tests (if requested)
# =============================================================================

if $RUN_TESTS; then
    info "Running tests..."

    dotnet test "$SOLUTION_FILE" \
        --no-build \
        --configuration "$CONFIGURATION" \
        --logger "console;verbosity=normal" \
        --collect:"XPlat Code Coverage" \
        --results-directory "$ARTIFACTS_DIR/test-results"

    success "Tests complete"
fi

# =============================================================================
# Publish (if requested)
# =============================================================================

if $PUBLISH; then
    info "Publishing application..."

    OUTPUT_DIR="$ARTIFACTS_DIR/$RUNTIME"
    mkdir -p "$OUTPUT_DIR"

    PUBLISH_ARGS=(
        publish "$MAIN_PROJECT"
        --configuration "$CONFIGURATION"
        --runtime "$RUNTIME"
        --output "$OUTPUT_DIR"
        --verbosity "$VERBOSITY"
    )

    if $SELF_CONTAINED; then
        PUBLISH_ARGS+=(--self-contained true)
    else
        PUBLISH_ARGS+=(--self-contained false)
    fi

    if $SINGLE_FILE; then
        PUBLISH_ARGS+=(-p:PublishSingleFile=true)
        PUBLISH_ARGS+=(-p:IncludeNativeLibrariesForSelfExtract=true)
        PUBLISH_ARGS+=(-p:EnableCompressionInSingleFile=true)
    fi

    dotnet "${PUBLISH_ARGS[@]}"

    # Make binary executable
    chmod +x "$OUTPUT_DIR/sendspin" 2>/dev/null || \
    chmod +x "$OUTPUT_DIR/SendspinClient.Linux" 2>/dev/null || true

    # List published files
    info "Published files:"
    ls -lh "$OUTPUT_DIR" | while read -r line; do
        info "  $line"
    done

    success "Published to: $OUTPUT_DIR"
fi

# =============================================================================
# AppImage (if requested)
# =============================================================================

if $BUILD_APPIMAGE; then
    info "Building AppImage..."

    APPIMAGE_DIR="$ARTIFACTS_DIR/appimage"
    APPDIR="$APPIMAGE_DIR/AppDir"

    # Create AppDir structure
    mkdir -p "$APPDIR/usr/bin"
    mkdir -p "$APPDIR/usr/share/applications"
    mkdir -p "$APPDIR/usr/share/icons/hicolor/256x256/apps"
    mkdir -p "$APPDIR/usr/share/metainfo"

    # Copy binary
    cp -r "$ARTIFACTS_DIR/$RUNTIME"/* "$APPDIR/usr/bin/"
    chmod +x "$APPDIR/usr/bin/sendspin" 2>/dev/null || \
    chmod +x "$APPDIR/usr/bin/SendspinClient.Linux" 2>/dev/null || true

    # Create desktop entry
    cat > "$APPDIR/usr/share/applications/sendspin.desktop" << 'EOF'
[Desktop Entry]
Name=Sendspin
Comment=Synchronized multi-room audio playback
Exec=sendspin
Icon=sendspin
Type=Application
Categories=Audio;AudioVideo;Player;
Keywords=audio;music;sync;multiroom;
StartupNotify=true
Terminal=false
EOF

    # Copy icon if exists
    if [[ -f "$REPO_ROOT/src/SendspinClient.Linux/Assets/sendspin.png" ]]; then
        cp "$REPO_ROOT/src/SendspinClient.Linux/Assets/sendspin.png" \
           "$APPDIR/usr/share/icons/hicolor/256x256/apps/"
    fi

    # Create AppRun
    cat > "$APPDIR/AppRun" << 'EOF'
#!/bin/bash
SELF=$(readlink -f "$0")
HERE=${SELF%/*}
export PATH="${HERE}/usr/bin:${PATH}"
export LD_LIBRARY_PATH="${HERE}/usr/lib:${LD_LIBRARY_PATH}"
exec "${HERE}/usr/bin/sendspin" "$@" || exec "${HERE}/usr/bin/SendspinClient.Linux" "$@"
EOF
    chmod +x "$APPDIR/AppRun"

    # Symlinks
    ln -sf usr/share/applications/sendspin.desktop "$APPDIR/sendspin.desktop"
    ln -sf usr/share/icons/hicolor/256x256/apps/sendspin.png "$APPDIR/sendspin.png" 2>/dev/null || true

    # Download appimagetool if not present
    APPIMAGETOOL="$APPIMAGE_DIR/appimagetool"
    if [[ ! -f "$APPIMAGETOOL" ]]; then
        info "Downloading appimagetool..."
        wget -q -O "$APPIMAGETOOL" \
            "https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
        chmod +x "$APPIMAGETOOL"
    fi

    # Determine architecture
    case "$RUNTIME" in
        linux-x64) ARCH="x86_64" ;;
        linux-arm64) ARCH="aarch64" ;;
        *) ARCH="x86_64" ;;
    esac

    # Build AppImage
    ARCH="$ARCH" "$APPIMAGETOOL" "$APPDIR" "$ARTIFACTS_DIR/Sendspin-$ARCH.AppImage"
    chmod +x "$ARTIFACTS_DIR/Sendspin-$ARCH.AppImage"

    success "AppImage created: $ARTIFACTS_DIR/Sendspin-$ARCH.AppImage"
fi

# =============================================================================
# Debian Package (if requested)
# =============================================================================

if $BUILD_DEB; then
    info "Building .deb package..."

    # Determine architecture
    case "$RUNTIME" in
        linux-x64) DEB_ARCH="amd64" ;;
        linux-arm64) DEB_ARCH="arm64" ;;
        *) DEB_ARCH="amd64" ;;
    esac

    VERSION="${VERSION:-1.0.0}"
    PKG_NAME="sendspin"
    PKG_DIR="$ARTIFACTS_DIR/deb/${PKG_NAME}_${VERSION}_${DEB_ARCH}"

    # Create package structure
    mkdir -p "$PKG_DIR/DEBIAN"
    mkdir -p "$PKG_DIR/usr/bin"
    mkdir -p "$PKG_DIR/usr/share/applications"
    mkdir -p "$PKG_DIR/usr/share/icons/hicolor/256x256/apps"
    mkdir -p "$PKG_DIR/usr/share/doc/sendspin"

    # Copy binary
    cp -r "$ARTIFACTS_DIR/$RUNTIME"/* "$PKG_DIR/usr/bin/"
    chmod +x "$PKG_DIR/usr/bin/sendspin" 2>/dev/null || \
    chmod +x "$PKG_DIR/usr/bin/SendspinClient.Linux" 2>/dev/null || true

    # Create control file
    cat > "$PKG_DIR/DEBIAN/control" << EOF
Package: sendspin
Version: $VERSION
Section: sound
Priority: optional
Architecture: $DEB_ARCH
Depends: libx11-6, libfontconfig1, libfreetype6
Recommends: pipewire, pipewire-pulse
Maintainer: Sendspin Team <support@sendspin.io>
Description: Synchronized multi-room audio playback client
 Sendspin is a desktop client for synchronized multi-room audio
 playback. Play audio in perfect sync with other Sendspin clients
 across your network.
Homepage: https://github.com/your-org/Linuxspin
EOF

    # Create desktop file
    cat > "$PKG_DIR/usr/share/applications/sendspin.desktop" << 'EOF'
[Desktop Entry]
Name=Sendspin
Comment=Synchronized multi-room audio playback
Exec=/usr/bin/sendspin
Icon=sendspin
Type=Application
Categories=Audio;AudioVideo;Player;
Keywords=audio;music;sync;multiroom;
StartupNotify=true
Terminal=false
EOF

    # Copy icon if exists
    if [[ -f "$REPO_ROOT/src/SendspinClient.Linux/Assets/sendspin.png" ]]; then
        cp "$REPO_ROOT/src/SendspinClient.Linux/Assets/sendspin.png" \
           "$PKG_DIR/usr/share/icons/hicolor/256x256/apps/"
    fi

    # Build package
    dpkg-deb --build --root-owner-group "$PKG_DIR"
    mv "$PKG_DIR.deb" "$ARTIFACTS_DIR/sendspin_${VERSION}_${DEB_ARCH}.deb"

    success ".deb package created: $ARTIFACTS_DIR/sendspin_${VERSION}_${DEB_ARCH}.deb"
fi

# =============================================================================
# Flatpak (if requested)
# =============================================================================

if $BUILD_FLATPAK; then
    info "Building Flatpak..."

    # Check for flatpak-builder
    if ! command -v flatpak-builder &> /dev/null; then
        warn "flatpak-builder not found. Install with: sudo dnf install flatpak-builder"
        warn "Skipping Flatpak build"
    else
        FLATPAK_DIR="$ARTIFACTS_DIR/flatpak"
        mkdir -p "$FLATPAK_DIR"

        # Create manifest
        cat > "$FLATPAK_DIR/io.sendspin.client.yml" << EOF
app-id: io.sendspin.client
runtime: org.freedesktop.Platform
runtime-version: '23.08'
sdk: org.freedesktop.Sdk
command: sendspin
finish-args:
  - --share=ipc
  - --socket=x11
  - --socket=wayland
  - --socket=pulseaudio
  - --share=network
  - --device=dri
  - --filesystem=xdg-music:ro
  - --talk-name=org.freedesktop.Notifications
modules:
  - name: sendspin
    buildsystem: simple
    build-commands:
      - install -Dm755 sendspin /app/bin/sendspin || install -Dm755 SendspinClient.Linux /app/bin/sendspin
      - install -Dm644 sendspin.desktop /app/share/applications/io.sendspin.client.desktop
    sources:
      - type: dir
        path: ../$RUNTIME
EOF

        # Create desktop file for Flatpak
        cat > "$ARTIFACTS_DIR/$RUNTIME/sendspin.desktop" << 'EOF'
[Desktop Entry]
Name=Sendspin
Comment=Synchronized multi-room audio playback
Exec=sendspin
Icon=io.sendspin.client
Type=Application
Categories=Audio;AudioVideo;Player;
EOF

        # Build Flatpak
        cd "$FLATPAK_DIR"
        flatpak-builder --force-clean --repo=repo build-dir io.sendspin.client.yml
        flatpak build-bundle repo "$ARTIFACTS_DIR/sendspin.flatpak" io.sendspin.client

        success "Flatpak created: $ARTIFACTS_DIR/sendspin.flatpak"
    fi
fi

# =============================================================================
# Summary
# =============================================================================

echo ""
success "Build completed successfully!"
echo ""

info "Artifacts:"
if [[ -d "$ARTIFACTS_DIR" ]]; then
    find "$ARTIFACTS_DIR" -maxdepth 2 -type f \( -name "*.AppImage" -o -name "*.deb" -o -name "*.flatpak" -o -name "sendspin" -o -name "SendspinClient.Linux" \) 2>/dev/null | while read -r file; do
        size=$(ls -lh "$file" | awk '{print $5}')
        info "  $(basename "$file") ($size)"
    done
fi

echo ""
info "Next steps:"
echo "  Run:    $ARTIFACTS_DIR/$RUNTIME/sendspin"
echo "  Test:   ./scripts/build.sh --test"
echo "  Deploy: ./scripts/deploy.sh <hostname>"

exit 0
