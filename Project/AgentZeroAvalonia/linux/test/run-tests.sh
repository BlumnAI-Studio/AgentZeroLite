#!/usr/bin/env bash
# Run the headless suites (ZeroCommon.Tests, AgentZeroAvalonia.Tests) on Linux in the .NET SDK
# container — what the CI linux job runs, without pushing a tag. Source is copied in (bin/obj
# left behind) so a Windows build in the worktree does not leak into the Linux one.
#
#   run-tests.sh            (from the repo root)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../../../.." && pwd)"
to_docker_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else echo "$1"; fi; }

script='
set -e
mkdir -p /src && cd /repo
tar --exclude=bin --exclude=obj --exclude=node_modules --exclude=out --exclude=_cache \
    -cf - Project global.json | tar -C /src -xf -
cd /src
dotnet test Project/ZeroCommon.Tests/ZeroCommon.Tests.csproj -c Release --filter "$FILTER" 2>&1 | grep -E "\[FAIL\]|Failed |error|Passed!|Failed!|통과|실패" | head -60
dotnet test Project/AgentZeroAvalonia.Tests/AgentZeroAvalonia.Tests.csproj -c Release 2>&1 | grep -E "\[FAIL\]|Failed |error|Passed!|Failed!|통과|실패" | head -40
'
MSYS_NO_PATHCONV=1 docker run --rm -e FILTER="${FILTER:-FullyQualifiedName!~Mp3ScannerTests&FullyQualifiedName!~SshCommandBuilderTests&FullyQualifiedName!~WhisperModelStoreTests}" \
  -v "$(to_docker_path "$repo"):/repo:ro" mcr.microsoft.com/dotnet/sdk:10.0 bash -c "$script"
