#!/usr/bin/env python3
"""Offline sanity-check for the Compatibility Resolver signatures.

Usage:
    python tools/verify_compat_signatures.py "C:\\...\\pxgme.exe"

This script only reads the PE file. It does not attach to the game process.
"""
from __future__ import annotations
import struct
import sys
from pathlib import Path

PATTERNS = {
    "lua_pcall": "41 56 41 55 41 54 55 57 56 53 48 83 EC 20 45 31 F6 4C 8B 69 10 48 8B 71 28 41 0F B6 AD 91 00 00",
    "luaL_loadbufferx": "48 83 EC 48 48 8B 44 24 70 48 89 44 24 20 48 89 54 24 30 48 8D 15 ?? ?? ?? ?? 4C 89 44 24 38 4C 8D 44 24 30 E8 ?? ?? ?? ?? 48 83 C4 48 C3",
    "lua_interface_xref": "41 56 41 55 41 54 55 57 56 53 48 83 C4 80 4C 8B 25 ?? ?? ?? ?? 48 B8 67 5F 63 68 61 72 74 62 4C 89 E1",
    "map_xref": "83 FB 09 75 06 49 83 FC 01 75 22 48 8B 05 ?? ?? ?? ?? 8B 80 14 0B 00 00 49 39 C4 0F 82 ?? ?? ?? ?? 83 FB 2A",
    "game_candidates": "48 8D 0D ?? ?? ?? ?? 48 8D 5C 24 20 E8 ?? ?? ?? ?? 48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8D 05 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 48 89 DA 48 C7 44 24 20 00 00 00 00 66 48 0F 6E C0",
}


def parse_pattern(text: str):
    return [None if x in {"?", "??"} else int(x, 16) for x in text.split()]


def main(path: Path) -> int:
    data = path.read_bytes()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe+4] != b"PE\0\0":
        raise SystemExit("Not a PE file")
    sections_count = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    sections_off = pe + 24 + opt_size
    sections = []
    for i in range(sections_count):
        off = sections_off + i * 40
        name = data[off:off+8].rstrip(b"\0").decode(errors="replace")
        vsize, va, rawsize, rawoff = struct.unpack_from("<IIII", data, off + 8)
        chars = struct.unpack_from("<I", data, off + 36)[0]
        sections.append((name, va, vsize, rawoff, rawsize, chars))

    def off_to_rva(off: int) -> int:
        for _, va, vsize, rawoff, rawsize, _ in sections:
            if rawoff <= off < rawoff + rawsize:
                return va + off - rawoff
        raise ValueError(off)

    def scan(text: str):
        pat = parse_pattern(text)
        hits = []
        for _, va, _, rawoff, rawsize, chars in sections:
            if not (chars & 0x20000000):
                continue
            start, end = rawoff, min(len(data), rawoff + rawsize)
            first = next((i for i, b in enumerate(pat) if b is not None), 0)
            expected_first = pat[first]
            for off in range(start, end - len(pat) + 1):
                if expected_first is not None and data[off + first] != expected_first:
                    continue
                if all(b is None or data[off + i] == b for i, b in enumerate(pat)):
                    hits.append(off_to_rva(off))
        return hits

    print(path)
    for name, pattern in PATTERNS.items():
        hits = scan(pattern)
        print(f"{name:20} hits={len(hits)}  " + ", ".join(f"0x{x:X}" for x in hits[:12]))
    return 0


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("Usage: verify_compat_signatures.py <pxgme.exe>")
    raise SystemExit(main(Path(sys.argv[1])))
