#!/usr/bin/env bash
# Can each built-in agent row find its CLI on a stock Ubuntu? Installs the four tools the way the
# app's Settings installer does (npm -g under nvm for claude / codex / agent-one, the vendor script
# for netclaw) in a fresh ubuntu:24.04 user, then shows where each landed, which startup file put it
# on PATH, and whether the old (-l -c) and new (-l -i -c) row launches find it.
#
#   agent-cli-path-test.sh            (from the repo root)
set -euo pipefail

script='
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq curl ca-certificates git unzip util-linux >/dev/null 2>&1
useradd -m -s /bin/bash az
cp -a /home/az/.bashrc /tmp/bashrc.orig; cp -a /home/az/.profile /tmp/profile.orig
su - az -c "curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.3/install.sh | bash >/dev/null 2>&1"
su - az -c "bash -ic \"nvm install --lts >/dev/null 2>&1 && npm install -g @openai/codex @webnori/agent-one >/dev/null 2>&1\"" 2>/dev/null
echo "== netclaw vendor installer"
su - az -c "curl -fsSL https://releases.netclaw.dev/install.sh | bash" > /tmp/netclaw.log 2>&1 || { echo "netclaw install failed:"; tail -5 /tmp/netclaw.log; }
tail -4 /tmp/netclaw.log
echo "== startup files changed"
for f in .bashrc .profile .bash_profile .zshrc; do
  [ -f /home/az/$f ] || continue
  orig=/tmp/${f#.}.orig; [ -f "$orig" ] || orig=/dev/null
  diff -q "$orig" /home/az/$f >/dev/null 2>&1 || { echo "-- $f"; diff "$orig" /home/az/$f | grep "^>" | sed "s/^> /   /"; }
done
for tool in codex agent-one netclaw; do
  echo "== $tool"
  for form in "-l -c" "-l -i -c"; do
    r=$(su - az -c "cd ~ && script -qec \"bash $form \\\"command -v $tool || echo NOT-FOUND\\\"\" /dev/null" 2>/dev/null | tr -d "\r" | tail -1)
    printf "   bash %-9s -> %s\n" "$form" "$r"
  done
done
'
MSYS_NO_PATHCONV=1 docker run --rm ubuntu:24.04 bash -c "$script"
