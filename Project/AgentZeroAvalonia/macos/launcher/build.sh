#!/bin/sh
# Build the AppKit-linked launcher (see launcher.c) for the sandboxed bundle.
# Usage: build.sh <out-path>   e.g. build.sh publish-avalonia/launcher/AgentZeroLite
set -eu
out="${1:?output path}"
here="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$(dirname "$out")"
clang -O2 -arch arm64 -mmacosx-version-min=12.0 -framework AppKit -o "$out" "$here/launcher.c"
otool -L "$out" | grep -q AppKit.framework || { echo "[launcher] AppKit is not linked" >&2; exit 1; }
echo "[launcher] $out"
