#!/usr/bin/env bash
# Builds the Phase 1 native test library (example.so).
set -euo pipefail
cd "$(dirname "$0")"

# -fPIC is required for shared objects on x86-64/ARM64.
gcc -shared -fPIC -O2 -o example.so example.c
echo "built example.so"
