#!/usr/bin/env bash
# Run a linux-x64 publish folder in the azl-ubuntu-gui container, showing on this desktop.
#
#   run-gui.sh <publish-dir> [--smoke [seconds]]
#   run-gui.sh <publish-dir> --terminal [seconds]
#
# --terminal goes one step further: it seeds a workspace (~/proj) into the app's database,
# opens a terminal tab through -cli, types into it and reads the screen back — the PTY, the
# WebKitGTK renderer and the screen snapshot round trip, end to end.
#
# Without --smoke the app runs until its window is closed (WSLg puts it on the Windows desktop).
# With --smoke it starts the GUI, waits, saves out/linux-gui.png (the app's window) plus the app log, lists the windows it mapped, and exits — a check a script
# can run. The container is a stock Ubuntu 24.04 with bash and no zsh.
#
# Display: WSLg through Docker Desktop on Windows 11 (/run/desktop/mnt/host/wslg); on a Linux
# host the local X socket (/tmp/.X11-unix) with DISPLAY passed through.
set -euo pipefail
pub="${1:?usage: run-gui.sh <publish-dir> [--smoke [seconds]]}"
mode="${2:-}"; wait_s="${3:-25}"
here="$(cd "$(dirname "$0")" && pwd)"
out="$here/out"; mkdir -p "$out"

to_docker_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else echo "$1"; fi; }
pub_abs="$(cd "$pub" && pwd)"

if [ -d /run/desktop ] || [ -n "${WSL_DISTRO_NAME:-}" ] || command -v cygpath >/dev/null 2>&1; then
  x11=(-v /run/desktop/mnt/host/wslg/.X11-unix:/tmp/.X11-unix -e DISPLAY=:0)
else
  x11=(-v /tmp/.X11-unix:/tmp/.X11-unix -e "DISPLAY=${DISPLAY:-:0}")
fi

script='
set -e
cp -r /pub /tmp/az && chmod +x /tmp/az/AgentZeroLite /tmp/az/AgentZeroLite.sh
cd ~
az=/tmp/az/AgentZeroLite
if [ "$MODE" = terminal ]; then
  # First start creates and migrates the database; then one workspace is added.
  # (a fresh account: ~/.local/share does not exist yet — the app must create it, not write to ~)
  $az > /out/linux-gui.log 2>&1 & pid=$!; sleep 12; kill $pid; wait $pid 2>/dev/null || true
  [ -d ~/AgentZeroLite ] && echo "!! data landed in ~/AgentZeroLite (LocalApplicationData was empty)"
  mkdir -p ~/proj && echo "hello from ubuntu" > ~/proj/README.md
  db=~/.local/share/AgentZeroLite/agentZeroLite.db
  # \047 = a single quote (this whole script is a single-quoted string on the host side)
  sqlite3 "$db" "$(printf "INSERT INTO CliGroups (DirectoryPath, DisplayName, SortOrder, ActiveTabIndex) VALUES (\047%s/proj\047, \047proj\047, 0, 0);" "$HOME")"
  sqlite3 "$db" "SELECT Name, ExePath, Arguments FROM CliDefinitions;" | sed "s/^/  cli: /"
  $az >> /out/linux-gui.log 2>&1 & pid=$!; sleep 10
  echo "== layout add"; $az -cli layout add || true
  sleep 8
  echo "== terminal-send"; $az -cli terminal-send 0 0 "echo AZ_LINUX_OK \$SHELL; uname -sr" || true
  sleep 3
  echo "== terminal-read"; $az -cli terminal-read 0 0 --last 600 || true
  echo "== code open"; $az -cli code open README.md || true
  sleep "$WAIT"
  wid=$(xdotool search --name "AgentZero Lite" | head -1 || true)
  [ -n "$wid" ] && import -window "$wid" /out/linux-gui.png && echo "== saved out/linux-gui.png"
  cp ~/.local/share/AgentZeroLite/logs/app-log.txt /out/linux-app-log.txt 2>/dev/null || true
  kill $pid 2>/dev/null || true
elif [ "$SMOKE" = 1 ]; then
  /tmp/az/AgentZeroLite > /out/linux-gui.log 2>&1 &
  pid=$!
  sleep "$WAIT"
  echo "== windows"; xwininfo -root -tree | grep -i "agentzero" | head -5 || true
  echo "== cli status"; /tmp/az/AgentZeroLite -cli status || true
  # WSLg is rootless (no root window image), so capture the app window itself.
  wid=$(xdotool search --name "AgentZero Lite" | head -1 || true)
  if [ -n "$wid" ]; then import -window "$wid" /out/linux-gui.png && echo "== saved out/linux-gui.png ($wid)"; fi
  cp ~/.local/share/AgentZeroLite/logs/app-log.txt /out/linux-app-log.txt 2>/dev/null || true
  kill $pid 2>/dev/null || true
else
  exec /tmp/az/AgentZeroLite
fi
'
MSYS_NO_PATHCONV=1 docker run --rm \
  "${x11[@]}" \
  -e SMOKE="$([ "$mode" = --smoke ] && echo 1 || echo 0)" -e WAIT="$wait_s" \
  -e MODE="$([ "$mode" = --terminal ] && echo terminal || echo gui)" \
  -v "$(to_docker_path "$pub_abs"):/pub:ro" -v "$(to_docker_path "$out"):/out" \
  --shm-size=512m \
  azl-ubuntu-gui bash -c "$script"
