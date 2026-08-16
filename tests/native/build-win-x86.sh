#!/usr/bin/env bash
# Cross-compiles the native test library for Windows x86 (32-bit) using mingw-w64.
# Output: tests/native/example-x86.dll
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v i686-w64-mingw32-gcc >/dev/null 2>&1; then
    echo "error: i686-w64-mingw32-gcc not found. Install mingw-w64 (32-bit)." >&2
    exit 1
fi

i686-w64-mingw32-gcc -shared -O2 -o example-x86.dll example.c
echo "built example-x86.dll"
