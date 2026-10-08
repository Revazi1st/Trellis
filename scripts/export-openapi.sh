#!/usr/bin/env bash
# Exports the running API's OpenAPI document to docs/api/openapi.v1.json, byte for byte as the server
# serialises it (plus the final newline every text file here has). The committed file is the API's
# public contract artifact (ADR-0013, ADR-0025): OpenApiSnapshotTests compares it with the document the
# in-process host serves on every test run, and CI regenerates it against the running app and fails on
# any diff, so wire-contract changes are always deliberate and reviewable.
#
# Regenerate either way, review the diff, commit:
#   ./scripts/export-openapi.sh [http://localhost:5171]      with the app running (see the run-app skill)
#   UPDATE_OPENAPI_SNAPSHOT=1 ./scripts/dotnet.sh test tests/Trellis.ApiTests
#                                                         no running app: the snapshot test writes the file
set -euo pipefail

BASE_URL="${1:-http://localhost:5171}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$REPO_ROOT/docs/api/openapi.v1.json"

mkdir -p "$REPO_ROOT/docs/api"
{ curl -fsS "$BASE_URL/openapi/v1.json"; echo; } > "$OUT"
echo "wrote docs/api/openapi.v1.json"
