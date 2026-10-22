#!/usr/bin/env bash
# Install the .deb into a fresh ubuntu:24.04 container the way a user would (apt resolves the
# Depends), then check the installed app: CLI self-test headless, the .desktop entry, and — when
# an X display is reachable (WSLg / X11) — that the GUI maps its window from /usr/bin.
#
#   install-test.sh <path/to/AgentZeroLite-v<ver>-linux-x64.deb>
set -euo pipefail
deb="${1:?usage: install-test.sh <deb>}"
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/out"; mkdir -p "$out"
to_docker_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else echo "$1"; fi; }
deb_abs="$(cd "$(dirname "$deb")" && pwd)/$(basename "$deb")"

if command -v cygpath >/dev/null 2>&1; then
  x11=(-v /run/desktop/mnt/host/wslg/.X11-unix:/tmp/.X11-unix -e DISPLAY=:0)
else
  x11=(-v /tmp/.X11-unix:/tmp/.X11-unix -e "DISPLAY=${DISPLAY:-:0}")
fi

script='
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
echo "== apt install ./agentzerolite.deb"
apt-get install -y -qq /pkg/app.deb >/tmp/apt.log 2>&1 || { tail -30 /tmp/apt.log; exit 1; }
dpkg -s agentzerolite | grep -E "^(Version|Status|Installed-Size):"
apt-get install -y -qq --no-install-recommends desktop-file-utils x11-utils xdotool imagemagick >/dev/null 2>&1
echo "== desktop-file-validate"; desktop-file-validate /usr/share/applications/agentzerolite.desktop && echo ok
useradd -m -s /bin/bash az
su az -c "cd ~ && agentzerolite -cli version && agentzerolite -cli selftest all"
echo "== GUI from /usr/bin/agentzerolite"
su az -c "cd ~ && DISPLAY=:0 agentzerolite > /tmp/gui.log 2>&1 &"
sleep 20
wid=$(su az -c "DISPLAY=:0 xdotool search --name \"AgentZero Lite\"" | head -1 || true)
if [ -n "$wid" ]; then
  echo "window $wid mapped"; DISPLAY=:0 import -window "$wid" /out/linux-installed.png && echo "saved out/linux-installed.png"
else
  echo "no window"; tail -20 /tmp/gui.log
fi
ls -d /home/az/.local/share/AgentZeroLite && echo "data under ~/.local/share/AgentZeroLite"
'
MSYS_NO_PATHCONV=1 docker run --rm "${x11[@]}" --shm-size=512m \
  -v "$(to_docker_path "$deb_abs"):/pkg/app.deb:ro" -v "$(to_docker_path "$out"):/out" \
  ubuntu:24.04 bash -c "$script"
