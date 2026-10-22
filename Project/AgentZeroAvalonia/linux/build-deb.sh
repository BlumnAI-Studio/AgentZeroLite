#!/usr/bin/env bash
# Package a linux-x64 publish folder as an Ubuntu/Debian .deb and a portable .tar.gz.
#
#   build-deb.sh <publish-dir> <out-dir> <version>
#
# Writes <out-dir>/AgentZeroLite-v<version>-linux-x64.deb and …-linux-x64.tar.gz. Needs dpkg-deb
# (any Debian/Ubuntu, the ubuntu-latest runner, or the azl-ubuntu-gui test image).
#
# Layout of the .deb:
#   /opt/agentzerolite/                 the self-contained publish (no .NET to install)
#   /usr/bin/agentzerolite              symlink → the app (GUI, and `agentzerolite -cli …`)
#   /usr/share/applications/agentzerolite.desktop
#   /usr/share/icons/hicolor/<n>x<n>/apps/agentzerolite.png
#
# Depends are the system libraries the publish does not carry: ICU and OpenSSL for .NET,
# fontconfig for Skia, and GTK 3 + WebKitGTK 4.1 (libsoup 3) for the terminal / code-view web
# view. The `a | b` alternatives cover Ubuntu 24.04 (t64 names), 22.04 and Debian 12. The emoji
# and CJK fonts are Recommends — the UI's icons are emoji and its second language is Korean.
set -euo pipefail
pub="${1:?usage: build-deb.sh <publish-dir> <out-dir> <version>}"
out="${2:?out-dir}"
ver="${3:?version}"
here="$(cd "$(dirname "$0")" && pwd)"
name="AgentZeroLite-v$ver-linux-x64"
mkdir -p "$out"
out="$(cd "$out" && pwd)"

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
root="$stage/root"

install -d "$root/opt/agentzerolite" "$root/usr/bin" "$root/usr/share/applications" "$root/DEBIAN"
cp -a "$pub/." "$root/opt/agentzerolite/"
# The Windows-only helper and debug symbols do not belong in a Linux package.
rm -f "$root/opt/agentzerolite/AgentZeroLite.ps1" "$root"/opt/agentzerolite/*.pdb
chmod 755 "$root/opt/agentzerolite/AgentZeroLite" "$root/opt/agentzerolite/AgentZeroLite.sh"
ln -s /opt/agentzerolite/AgentZeroLite "$root/usr/bin/agentzerolite"
install -m 644 "$here/agentzerolite.desktop" "$root/usr/share/applications/agentzerolite.desktop"
for s in 48 128 256; do
  install -d "$root/usr/share/icons/hicolor/${s}x${s}/apps"
  install -m 644 "$here/icons/agentzerolite-$s.png" "$root/usr/share/icons/hicolor/${s}x${s}/apps/agentzerolite.png"
done

size_kb=$(du -sk "$root" | cut -f1)
cat > "$root/DEBIAN/control" <<EOF
Package: agentzerolite
Version: $ver
Section: devel
Priority: optional
Architecture: amd64
Installed-Size: $size_kb
Maintainer: psmon <psmon@live.co.kr>
Homepage: https://psmon.github.io/AgentZeroLite/
Depends: libicu74 | libicu72 | libicu70, libssl3t64 | libssl3, libfontconfig1, libgtk-3-0t64 | libgtk-3-0, libwebkit2gtk-4.1-0, libsoup-3.0-0, libx11-6
Recommends: fonts-noto-color-emoji, fonts-noto-cjk, xdg-utils, git
Description: Multi-tab terminal for AI command-line tools
 AgentZero Lite runs Claude Code, Codex and any shell side by side in real terminal
 tabs with split panes and per-project workspaces, an assistant that can read and
 drive the terminals, a code view with the workspace's files and git log, and web
 plugins. Self-contained: no .NET runtime to install.
EOF

dpkg-deb --root-owner-group --build "$root" "$out/$name.deb" >/dev/null
echo "deb    $out/$name.deb  ($(du -h "$out/$name.deb" | cut -f1))"

# Portable archive: the same files under one folder, run in place.
tar_root="$stage/tar/$name"
install -d "$tar_root"
cp -a "$root/opt/agentzerolite/." "$tar_root/"
install -m 644 "$here/agentzerolite.desktop" "$here/icons/agentzerolite-256.png" "$tar_root/"
tar -C "$stage/tar" -czf "$out/$name.tar.gz" "$name"
echo "tar.gz $out/$name.tar.gz  ($(du -h "$out/$name.tar.gz" | cut -f1))"
