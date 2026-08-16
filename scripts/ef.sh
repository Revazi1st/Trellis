#!/usr/bin/env bash
# Runs dotnet-ef inside the SDK container (tool pinned in .config/dotnet-tools.json).
# Example: ./scripts/ef.sh migrations add AddCustomers --project src/Infrastructure/Trellis.Persistence
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

DOCKER_BIN="${DOCKER_BIN:-docker}"
if ! command -v "$DOCKER_BIN" >/dev/null 2>&1; then
  if [ -x "$HOME/.docker/bin/docker" ]; then
    DOCKER_BIN="$HOME/.docker/bin/docker"
  else
    echo "error: docker CLI not found (set DOCKER_BIN)" >&2
    exit 1
  fi
fi

# Same image as dotnet.sh (a second multi-GB image buys nothing); quoted "$@" so arguments
# containing spaces (e.g. migration names) survive the inner shell.
exec "$DOCKER_BIN" run --rm \
  -v "$REPO_ROOT":/workspace \
  -w /workspace \
  -v trellis-nuget:/root/.nuget/packages \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  "${TRELLIS_SDK_IMAGE:-trellis-sdk:10.0}" \
  sh -c 'dotnet tool restore >/dev/null && dotnet dotnet-ef "$@"' ef "$@"
