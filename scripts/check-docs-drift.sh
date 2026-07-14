#!/usr/bin/env bash
# Fails if a feature slice changed on this branch without a corresponding docs/features change.
# The hard guarantee behind the soft PostToolUse reminder. Override with the 'docs-not-needed' PR label
# (wire that into the workflow) or by setting DOCS_DRIFT_SKIP=1.
set -euo pipefail

if [ "${DOCS_DRIFT_SKIP:-0}" = "1" ]; then
  echo "docs-drift check skipped (DOCS_DRIFT_SKIP=1)."
  exit 0
fi

base="${1:-origin/main}"
range="$(git merge-base "$base" HEAD)...HEAD"

changed="$(git diff --name-only "$range")"
features_changed="$(printf '%s\n' "$changed" | grep -E 'src/.*/Features/' || true)"
docs_changed="$(printf '%s\n' "$changed" | grep -E 'docs/features/' || true)"

if [ -n "$features_changed" ] && [ -z "$docs_changed" ]; then
  echo "::error::Feature slices changed without a docs/features update:"
  printf '%s\n' "$features_changed"
  echo "Update the relevant docs/features/*.md, or apply the 'docs-not-needed' label / set DOCS_DRIFT_SKIP=1."
  exit 1
fi

echo "docs-drift check passed."
