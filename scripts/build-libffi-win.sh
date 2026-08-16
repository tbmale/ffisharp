#!/usr/bin/env bash
# Cross-compiles libffi for Windows x64 using mingw-w64.
# Output: runtimes/win-x64/native/libffi-8.dll
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${TMPDIR:-/tmp}/ffisharp-libffi"
SRC_DIR="$WORK/libffi-3.8.0"
TARBALL="$WORK/libffi-3.8.0.tar.gz"
VERSION="3.8.0"

if ! command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
    echo "error: x86_64-w64-mingw32-gcc not found. Install mingw-w64." >&2
    exit 1
fi

mkdir -p "$WORK"
if [ ! -d "$SRC_DIR" ]; then
    curl -sSL -o "$TARBALL" "https://github.com/libffi/libffi/releases/download/v${VERSION}/libffi-${VERSION}.tar.gz"
    tar xzf "$TARBALL" -C "$WORK"
fi

cd "$SRC_DIR"
./configure --host=x86_64-w64-mingw32 --enable-shared --disable-static --disable-docs
make -j"$(nproc)"

mkdir -p "$ROOT/runtimes/win-x64/native"
cp "$SRC_DIR/x86_64-w64-mingw32/.libs/libffi-8.dll" "$ROOT/runtimes/win-x64/native/libffi-8.dll"
echo "built runtimes/win-x64/native/libffi-8.dll"
