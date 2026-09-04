#!/usr/bin/env python3
"""
Packs rendered PNGs into the Windows .ico and macOS .icns container formats.

Both formats are directories of image blobs with a small fixed-layout header, so packing
them needs no image library and no platform tool — which is the point. iconutil is macOS
only, and requiring it would mean a Linux contributor could not regenerate the icon set.

Called by scripts/generate-icons.sh; not intended to be run by hand.

Formats:
  .ico  — https://learn.microsoft.com/previous-versions/ms997538(v=msdn.10)
  .icns — https://en.wikipedia.org/wiki/Apple_Icon_Image_format
"""

import struct
import sys

# OSType per pixel size, in the order iconutil emits them. The pairs that repeat a size
# (32, 256, 512) are the @2x representations of the size below: a Retina Mac picks ic11
# for a 16pt slot, a non-Retina one picks icp5 for a 32pt slot, and both are 32 pixels.
ICNS_TYPES = [
    (b"icp4", 16),
    (b"icp5", 32),
    (b"ic11", 32),
    (b"ic12", 64),
    (b"ic07", 128),
    (b"ic13", 256),
    (b"ic08", 256),
    (b"ic14", 512),
    (b"ic09", 512),
    (b"ic10", 1024),
]


def read(path):
    with open(path, "rb") as handle:
        return handle.read()


def pack_ico(out_path, sources):
    """
    Writes an .ico holding each source PNG verbatim.

    sources is a list of (size, path). Sizes must be 256 or less; 256 is encoded as 0,
    which is the format's way of spelling it in a single byte.
    """
    entries = []
    blobs = []
    # Every entry is a fixed 16 bytes, so the first image starts after all of them.
    offset = 6 + 16 * len(sources)

    for size, path in sources:
        if not 1 <= size <= 256:
            raise ValueError(f"{size} is out of range for an .ico entry")
        blob = read(path)
        entries.append(
            struct.pack(
                "<BBBBHHII",
                size % 256,  # 256 is written as 0
                size % 256,
                0,  # not palettised
                0,  # reserved
                1,  # colour planes
                32,  # bits per pixel
                len(blob),
                offset,
            )
        )
        blobs.append(blob)
        offset += len(blob)

    with open(out_path, "wb") as handle:
        handle.write(struct.pack("<HHH", 0, 1, len(sources)))  # reserved, type 1 = icon
        for entry in entries:
            handle.write(entry)
        for blob in blobs:
            handle.write(blob)


def pack_icns(out_path, by_size):
    """
    Writes an .icns holding each source PNG verbatim, one chunk per OSType.

    by_size maps a pixel size to a rendered PNG path; a size named in ICNS_TYPES but
    missing from it is an error rather than a silently smaller icon.
    """
    chunks = []
    for ostype, size in ICNS_TYPES:
        if size not in by_size:
            raise ValueError(f"no {size}px render for {ostype.decode()}")
        blob = read(by_size[size])
        # The length a chunk declares includes its own 8-byte header.
        chunks.append(ostype + struct.pack(">I", len(blob) + 8) + blob)

    body = b"".join(chunks)
    with open(out_path, "wb") as handle:
        handle.write(b"icns" + struct.pack(">I", len(body) + 8) + body)


def main(argv):
    if len(argv) < 3:
        raise SystemExit("usage: pack-icons.py <ico|icns> <out> <size>=<png> ...")

    kind, out_path = argv[1], argv[2]
    pairs = []
    for arg in argv[3:]:
        size, _, path = arg.partition("=")
        pairs.append((int(size), path))

    if kind == "ico":
        pack_ico(out_path, pairs)
    elif kind == "icns":
        pack_icns(out_path, dict(pairs))
    else:
        raise SystemExit(f"unknown container {kind!r}")


if __name__ == "__main__":
    main(sys.argv)
