#!/usr/bin/env bash
# =============================================================================
# Sendspin Player - Icon Generation
# =============================================================================
# Regenerates every icon in packaging/icons/ from the two SVG masters. The
# generated files are committed, so this script is the source of truth for HOW
# they are made and the committed files are the source of truth for WHAT ships.
#
# They are committed rather than built because a plain `dotnet build` needs the
# .ico for the Windows head, and requiring a rasterizer would make a build fail
# on a clean machine that has no reason to own one.
#
# Run this after editing packaging/icons/sendspin.svg or sendspin-menubar.svg,
# and commit everything it writes. CI checks packaging/icons/.source-hash and
# fails if a master changed without the set being regenerated.
#
# Usage:
#   ./scripts/generate-icons.sh
#
# Requires: rsvg-convert (librsvg) and python3. Developed against librsvg
# 2.62.3; a different version may shift edge antialiasing by a step, which is
# why CI compares input hashes rather than output bytes.
#
# Author: Sendspin Team
# =============================================================================

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ICONS_DIR="$REPO_ROOT/packaging/icons"
MASTER="$ICONS_DIR/sendspin.svg"
MENUBAR_MASTER="$ICONS_DIR/sendspin-menubar.svg"

# The freedesktop icon theme sizes. 22 and 24 are both here on purpose: panels ask
# for one or the other and neither scales the other cleanly.
HICOLOR_SIZES=(16 22 24 32 48 64 128 256 512)

# What Windows picks between. 256 is the one Explorer's large-icon views use.
ICO_SIZES=(16 24 32 48 64 128 256)

# Every pixel size any container needs, so each is rendered from the vector once.
RENDER_SIZES=(16 22 24 32 48 64 128 256 512 1024)

if ! command -v rsvg-convert &> /dev/null; then
    echo "error: rsvg-convert not found (brew install librsvg / apt install librsvg2-bin)" >&2
    exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "Rendering $(basename "$MASTER") at ${#RENDER_SIZES[@]} sizes..."
for size in "${RENDER_SIZES[@]}"; do
    rsvg-convert --width="$size" --height="$size" \
        --output="$WORK/$size.png" "$MASTER"
done

# ---------------------------------------------------------------------------
# The freedesktop hicolor theme, which is what every Linux desktop reads.
# ---------------------------------------------------------------------------
echo "Writing the hicolor tree..."
for size in "${HICOLOR_SIZES[@]}"; do
    dir="$ICONS_DIR/hicolor/${size}x${size}/apps"
    mkdir -p "$dir"
    cp "$WORK/$size.png" "$dir/io.sendspin.client.png"
done

# The scalable entry is the master itself: a desktop that can render SVG prefers it
# over every raster size, at any scale factor.
mkdir -p "$ICONS_DIR/hicolor/scalable/apps"
cp "$MASTER" "$ICONS_DIR/hicolor/scalable/apps/io.sendspin.client.svg"

# ---------------------------------------------------------------------------
# Windows and macOS containers.
# ---------------------------------------------------------------------------
echo "Packing sendspin.ico..."
ico_args=()
for size in "${ICO_SIZES[@]}"; do
    ico_args+=("$size=$WORK/$size.png")
done
python3 "$REPO_ROOT/scripts/pack-icons.py" ico "$ICONS_DIR/sendspin.ico" "${ico_args[@]}"

echo "Packing sendspin.icns..."
icns_args=()
for size in 16 32 64 128 256 512 1024; do
    icns_args+=("$size=$WORK/$size.png")
done
python3 "$REPO_ROOT/scripts/pack-icons.py" icns "$ICONS_DIR/sendspin.icns" "${icns_args[@]}"

# ---------------------------------------------------------------------------
# The macOS menu bar silhouette. NSImage.ImageNamed resolves the @2x file itself,
# so both representations ship and AppKit picks per display.
# ---------------------------------------------------------------------------
echo "Rendering the menu bar template..."
rsvg-convert --width=22 --height=22 \
    --output="$ICONS_DIR/sendspin-menubar.png" "$MENUBAR_MASTER"
rsvg-convert --width=44 --height=44 \
    --output="$ICONS_DIR/sendspin-menubar@2x.png" "$MENUBAR_MASTER"

# ---------------------------------------------------------------------------
# The drift guard.
#
# This records the INPUTS, not the outputs. A regenerate-and-diff check would be
# checking that two rasterizer builds agree, which they do not have to, and would
# then fail for a reason no contributor could act on. Hashing the masters and the
# generator catches the thing that actually goes wrong: a master edited without
# the committed set being regenerated.
# ---------------------------------------------------------------------------
echo "Writing .source-hash..."

# sha256sum on Linux, shasum on macOS, which has no sha256sum. The two write the same
# format, so a file written on either platform verifies on the other.
if command -v sha256sum &> /dev/null; then
    sha256() { sha256sum "$@"; }
else
    sha256() { shasum -a 256 "$@"; }
fi

(
    cd "$REPO_ROOT"
    sha256 \
        packaging/icons/sendspin.svg \
        packaging/icons/sendspin-menubar.svg \
        scripts/generate-icons.sh \
        scripts/pack-icons.py
) > "$ICONS_DIR/.source-hash"

echo "Done. Commit everything under packaging/icons/."
