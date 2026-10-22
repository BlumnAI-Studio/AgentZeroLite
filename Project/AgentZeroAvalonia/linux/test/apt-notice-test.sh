#!/usr/bin/env bash
# Reproduce the "_apt couldn't access … pkgAcquire::Run (13: Permission denied)" notice and check
# that Home/install-linux.sh avoids it. Runs in a fresh ubuntu:24.04 with a desktop-like user
# whose home is 0750 (Ubuntu's default since 21.04). Uses the live GitHub release.
#
# Measured: part A does NOT reproduce the notice inside Docker (apt in a container does not take
# the _apt sandbox path the way a desktop install does), so this only proves B installs and runs
# from /tmp; the notice itself was reported on a real Ubuntu desktop with the .deb in ~/Downloads.
#
#   apt-notice-test.sh            (from the repo root; B runs the working copy's installer)
#   LIVE=1 apt-notice-test.sh     (B runs the published one-liner from GitHub Pages)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../../.." && pwd)"
to_docker_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else echo "$1"; fi; }

script='
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq sudo curl ca-certificates >/dev/null 2>&1
useradd -m -s /bin/bash az && chmod 750 /home/az && echo "az ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/az
url=$(curl -fsSL "https://api.github.com/repos/psmon/AgentZeroLite/releases?per_page=30" | grep -o "https://[^\"]*/v[0-9][^\"]*-linux-x64\.deb" | head -1)
echo "== A: sudo apt install ./<deb in ~/Downloads>  (home 0750)"
su az -c "mkdir -p ~/Downloads && curl -fsSL -o ~/Downloads/app.deb $url"
su az -c "cd ~/Downloads && sudo apt install -y ./app.deb" > /tmp/a.log 2>&1 || true
grep -E "_apt|Permission denied|pkgAcquire" /tmp/a.log || echo "(no notice)"
apt-get remove -y -qq agentzerolite >/dev/null 2>&1
if [ "$LIVE" = 1 ]; then
  echo "== B: the published one-liner (curl … install-linux.sh | bash)"
  su az -c "cd ~ && curl -fsSL https://psmon.github.io/AgentZeroLite/Home/install-linux.sh | bash" > /tmp/b.log 2>&1
else
  echo "== B: install-linux.sh (working copy)"
  su az -c "bash /repo/Home/install-linux.sh" > /tmp/b.log 2>&1
fi
grep -E "_apt|Permission denied|pkgAcquire" /tmp/b.log || echo "(no notice)"
tail -3 /tmp/b.log
su az -c "agentzerolite -cli version" | head -1
'
MSYS_NO_PATHCONV=1 docker run --rm -e LIVE="${LIVE:-0}" -v "$(to_docker_path "$repo"):/repo:ro" ubuntu:24.04 bash -c "$script"
