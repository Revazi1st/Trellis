#!/usr/bin/env bash
# Exports the running API's OpenAPI document to docs/api/openapi.v1.json with stable formatting.
# The committed file is the API's public contract artifact (ADR-0013): CI regenerates it against
# the running app and fails on any diff, so wire-contract changes are always deliberate and
# reviewable. Regenerate locally with the app running (see the run-app skill), then commit.
# Usage: ./scripts/export-openapi.sh [http://localhost:5171]
set -euo pipefail

BASE_URL="${1:-http://localhost:5171}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

mkdir -p "$REPO_ROOT/docs/api"
curl -fsS "$BASE_URL/openapi/v1.json" | python3 -m json.tool > "$REPO_ROOT/docs/api/openapi.v1.json"
echo "wrote docs/api/openapi.v1.json"
