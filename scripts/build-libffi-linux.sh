#!/usr/bin/env bash
# Builds libffi for the native Linux x64 host and vendors the result.
# Output: runtimes/linux-x64/native/libffi.so.8
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${TMPDIR:-/tmp}/ffisharp-libffi-linux"
SRC_DIR="$WORK/libffi-3.8.0"
TARBALL="$WORK/libffi-3.8.0.tar.gz"
VERSION="3.8.0"

if ! command -v gcc >/dev/null 2>&1; then
    echo "error: gcc not found." >&2
    exit 1
fi

mkdir -p "$WORK"
if [ ! -d "$SRC_DIR" ]; then
    curl -sSL -o "$TARBALL" "https://github.com/libffi/libffi/releases/download/v${VERSION}/libffi-${VERSION}.tar.gz"
    tar xzf "$TARBALL" -C "$WORK"
fi

cd "$SRC_DIR"
./configure --enable-shared --disable-static --disable-docs
make -j"$(nproc)"

# The build produces libffi.so.8.x.y (SONAME libffi.so.8). Vendor it under its
# SONAME so dlopen("/abs/path/libffi.so.8") and name-based resolution both work.
SO="$(find "$SRC_DIR" -name 'libffi.so.8.*' -type f | head -n1)"
if [ -z "$SO" ]; then
    echo "error: could not locate built libffi.so.8.x" >&2
    exit 1
fi

mkdir -p "$ROOT/runtimes/linux-x64/native"
cp -L "$SO" "$ROOT/runtimes/linux-x64/native/libffi.so.8"
echo "built runtimes/linux-x64/native/libffi.so.8"
