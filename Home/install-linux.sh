#!/usr/bin/env bash
# AgentZero Lite — install or update on Ubuntu / Debian (x64, desktop).
#
#   curl -fsSL https://psmon.github.io/AgentZeroLite/Home/install-linux.sh | bash
#
# Finds the newest app release (v<digits> tag — the repo also releases agent-one), downloads its
# AgentZeroLite-v<ver>-linux-x64.deb into a temporary folder apt's sandbox user can read, and
# installs it with apt so the dependencies (WebKitGTK, GTK 3, ICU …) come along.
#
# Why not ~/Downloads: since Ubuntu 21.04 home folders are 0750, so apt's download user (_apt)
# cannot read a .deb there and apt prints
#   "Download is performed unsandboxed as root as file '…' couldn't be accessed by user '_apt'.
#    - pkgAcquire::Run (13: Permission denied)"
# The install still works, but the message reads like a failure. /tmp is world-readable.
set -euo pipefail

repo="psmon/AgentZeroLite"
arch="$(dpkg --print-architecture 2>/dev/null || echo unknown)"
if [ "$arch" != "amd64" ]; then
  echo "AgentZero Lite for Linux is built for amd64 (x64); this machine is '$arch'." >&2
  exit 1
fi
command -v apt-get >/dev/null || { echo "apt-get not found — this installer is for Ubuntu / Debian." >&2; exit 1; }
command -v curl >/dev/null || { echo "curl is required (sudo apt install curl)." >&2; exit 1; }

echo "Looking up the latest release…"
url="$(curl -fsSL "https://api.github.com/repos/$repo/releases?per_page=30" \
  | grep -o '"browser_download_url": *"[^"]*/v[0-9][^"]*-linux-x64\.deb"' \
  | head -1 | sed 's/.*"\(https[^"]*\)"/\1/')"
if [ -z "$url" ]; then
  echo "No Linux .deb found on the latest releases: https://github.com/$repo/releases" >&2
  exit 1
fi

tmp="$(mktemp -d /tmp/agentzerolite.XXXXXX)"
trap 'rm -rf "$tmp"' EXIT
chmod 755 "$tmp"
deb="$tmp/$(basename "$url")"
echo "Downloading $(basename "$url")…"
curl -fL --progress-bar -o "$deb" "$url"
chmod 644 "$deb"

echo "Installing (sudo)…"
sudo apt-get install -y "$deb"

echo
echo "Installed: $(dpkg-query -W -f='${Package} ${Version}' agentzerolite)"
echo "Start it from the app menu (AgentZero Lite) or run: agentzerolite"
