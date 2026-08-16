#!/usr/bin/env bash
# Cross-compiles the Phase 1-6 native test library for Windows x64 using mingw-w64.
# Output: tests/native/example.dll
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
    echo "error: x86_64-w64-mingw32-gcc not found. Install mingw-w64." >&2
    exit 1
fi

x86_64-w64-mingw32-gcc -shared -O2 -o example.dll example.c
echo "built example.dll"
