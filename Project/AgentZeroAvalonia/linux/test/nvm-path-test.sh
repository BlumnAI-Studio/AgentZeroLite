#!/usr/bin/env bash
# Why a built-in agent row cannot find a CLI that a terminal can: a tool installed with npm under
# nvm is on PATH only after ~/.bashrc runs, and Ubuntu's ~/.bashrc returns at once for a shell
# that is not interactive. Installs nvm + Node + a global npm tool in a fresh ubuntu:24.04 user and
# compares the row's launch forms.
#
#   nvm-path-test.sh            (from the repo root)
set -euo pipefail

script='
set -e
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq >/dev/null
apt-get install -y -qq curl ca-certificates >/dev/null 2>&1
useradd -m -s /bin/bash az
su - az -c "curl -fsSL https://raw.githubusercontent.com/nvm-sh/nvm/v0.40.3/install.sh | bash >/dev/null 2>&1"
su - az -c "bash -ic \"nvm install --lts >/dev/null 2>&1 && npm install -g cowsay >/dev/null 2>&1\""
echo "== where the tool is"; su - az -c "bash -ic \"command -v cowsay\"" 2>/dev/null
for form in "-l -c" "-l -i -c"; do
  printf "== bash %s : " "$form"
  su - az -c "cd ~ && script -qec \"bash $form \\\"command -v cowsay || echo NOT-FOUND\\\"\" /dev/null" 2>/dev/null | tr -d "\r" | tail -1
done
'
MSYS_NO_PATHCONV=1 docker run --rm ubuntu:24.04 bash -c "$script"
