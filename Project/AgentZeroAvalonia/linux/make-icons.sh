#!/usr/bin/env bash
# Regenerate linux/icons/agentzerolite-<size>.png from the WPF host's agentzero.ico — the same
# source the Windows exe icon and macOS .icns come from. Needs ImageMagick (convert/identify).
# Run from the repo root:  bash Project/AgentZeroAvalonia/linux/make-icons.sh
set -euo pipefail
ico=Project/AgentZeroWpf/agentzero.ico
out=Project/AgentZeroAvalonia/linux/icons
mkdir -p "$out"
# The largest frame of the .ico is the best source for every size.
frame=$(identify -format '%p %w\n' "$ico" | sort -k2 -n | tail -1 | cut -d' ' -f1)
for s in 48 128 256; do
  convert "$ico[$frame]" -resize "${s}x${s}" "$out/agentzerolite-$s.png"
done
ls -la "$out"
