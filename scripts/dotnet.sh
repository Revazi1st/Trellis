#!/usr/bin/env bash
# Runs the dotnet CLI inside the official .NET 10 SDK container.
# Use when no local .NET SDK is installed:  ./scripts/dotnet.sh build XTemplate.slnx
# NuGet packages persist in the named volume 'xtemplate-nuget' across runs.
# The host Docker socket is mounted so Testcontainers-based integration tests can
# start sibling containers; TESTCONTAINERS_HOST_OVERRIDE points them back at the host.
# Default image is the local build from scripts/sdk-image (SDK + docker CLI, needed by
# Aspire orchestration); build it once with:  docker build -t xtemplate-sdk:10.0 scripts/sdk-image
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

DOCKER_BIN="${DOCKER_BIN:-docker}"
if ! command -v "$DOCKER_BIN" >/dev/null 2>&1; then
  # Docker Desktop on macOS keeps its CLI out of the default PATH
  if [ -x "$HOME/.docker/bin/docker" ]; then
    DOCKER_BIN="$HOME/.docker/bin/docker"
  else
    echo "error: docker CLI not found (set DOCKER_BIN)" >&2
    exit 1
  fi
fi

TTY_ARGS=""
if [ -t 0 ] && [ -t 1 ]; then
  TTY_ARGS="-it"
fi

exec "$DOCKER_BIN" run --rm ${TTY_ARGS:+$TTY_ARGS} \
  -v "$REPO_ROOT":/workspace \
  -w /workspace \
  -v xtemplate-nuget:/root/.nuget/packages \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  "${XT_SDK_IMAGE:-xtemplate-sdk:10.0}" \
  dotnet "$@"
