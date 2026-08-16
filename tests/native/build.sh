#!/usr/bin/env bash
# Builds the native test library (example.so).
set -euo pipefail
cd "$(dirname "$0")"

# -fPIC is required for shared objects on x86-64/ARM64.
# __stdcall is a Windows-only attribute; define it to nothing on Linux so the
# shared test header/impl compile on non-x86 targets too.
gcc -shared -fPIC -O2 -D__stdcall= -D__cdecl= -o example.so example.c
echo "built example.so"
