#!/usr/bin/env bash
# Runs the dotnet CLI inside the official .NET 10 SDK container.
# Use when no local .NET SDK is installed:  ./scripts/dotnet.sh build Trellis.slnx
# NuGet packages persist in the named volume 'trellis-nuget' across runs.
# Default image is the local build from scripts/sdk-image (SDK + docker CLI, needed by
# Aspire orchestration); build it once with:  docker build -t trellis-sdk:10.0 scripts/sdk-image
#
# Testcontainers-based integration tests start SIBLING containers through the engine's socket and
# reach their published ports via host.docker.internal (TESTCONTAINERS_HOST_OVERRIDE). Docker
# Desktop resolves that name on its own; Linux Docker Engine only with the --add-host mapping below.
#
# Which engine socket is mounted (docs/development.md, "Docker engines other than Docker Desktop"):
#   TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE=<path>  that path: the socket as the ENGINE sees it
#   macOS                                         /var/run/docker.sock: the engine runs in a VM
#                                                 (Docker Desktop, Colima, OrbStack, Podman machine)
#                                                 and resolves mount sources there, not on the Mac
#   DOCKER_HOST=unix://<path>                     <path> (rootless Docker, Podman on Linux)
#   DOCKER_HOST=tcp://...                         no mount; DOCKER_HOST is passed through
#   DOCKER_HOST unset                             /var/run/docker.sock
# Every other TESTCONTAINERS_* variable set here is forwarded, so Testcontainers' own recipes
# (TESTCONTAINERS_RYUK_DISABLED=true for rootless Podman, say) apply unchanged.
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

ENGINE_ARGS=(--add-host=host.docker.internal:host-gateway)
case "${DOCKER_HOST:-}" in
  tcp://*)
    # The docker CLI and Testcontainers inside the container talk to the engine directly. The
    # endpoint must be reachable from a container (not 127.0.0.1); Testcontainers derives the host
    # of published ports from DOCKER_HOST unless TESTCONTAINERS_HOST_OVERRIDE says otherwise.
    ENGINE_ARGS+=(-e DOCKER_HOST)
    for name in TESTCONTAINERS_HOST_OVERRIDE TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE; do
      if [ -n "${!name:-}" ]; then ENGINE_ARGS+=(-e "$name"); fi
    done
    ;;
  "" | unix://*)
    socket="${TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE:-}"
    if [ -z "$socket" ]; then
      if [ "$(uname -s)" = "Darwin" ]; then
        socket=/var/run/docker.sock
      else
        socket="${DOCKER_HOST#unix://}"
        socket="${socket:-/var/run/docker.sock}"
      fi
    fi
    # Inside the container the socket is always /var/run/docker.sock; the override tells
    # Testcontainers which ENGINE-side path to mount into its resource reaper (Ryuk).
    ENGINE_ARGS+=(
      -v "$socket":/var/run/docker.sock
      -e TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE="$socket"
      -e TESTCONTAINERS_HOST_OVERRIDE="${TESTCONTAINERS_HOST_OVERRIDE:-host.docker.internal}"
    )
    ;;
  *)
    echo "error: DOCKER_HOST='$DOCKER_HOST' is not supported by this script; use unix:// or tcp:// (docs/development.md)" >&2
    exit 1
    ;;
esac
for name in $(compgen -e); do
  case "$name" in
    TESTCONTAINERS_HOST_OVERRIDE | TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE) ;; # handled above
    TESTCONTAINERS_*) ENGINE_ARGS+=(-e "$name") ;;
  esac
done

exec "$DOCKER_BIN" run --rm ${TTY_ARGS:+$TTY_ARGS} \
  -v "$REPO_ROOT":/workspace \
  -w /workspace \
  -v trellis-nuget:/root/.nuget/packages \
  "${ENGINE_ARGS[@]}" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e DOTNET_NOLOGO=1 \
  ${UPDATE_OPENAPI_SNAPSHOT:+-e UPDATE_OPENAPI_SNAPSHOT=1} \
  "${TRELLIS_SDK_IMAGE:-trellis-sdk:10.0}" \
  dotnet "$@"
