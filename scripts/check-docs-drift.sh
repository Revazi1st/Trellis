#!/usr/bin/env bash
# Fails if code changed on this branch without the doc that describes it changing too:
#   - a feature slice (src/**/Features/**)                       -> docs/features/*.md
#   - authentication, hardening, identity or the Auth feature    -> docs/threat-model.md
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
fail=0

# require_doc <code-path regex> <doc-path regex> <what changed>
require_doc() {
  local code_changed doc_changed
  code_changed="$(printf '%s\n' "$changed" | grep -E "$1" || true)"
  doc_changed="$(printf '%s\n' "$changed" | grep -E "$2" || true)"

  if [ -n "$code_changed" ] && [ -z "$doc_changed" ]; then
    echo "::error::$3 changed without a $4 update:"
    printf '%s\n' "$code_changed"
    fail=1
  fi
}

require_doc 'src/.*/Features/' '^docs/features/' "Feature slices" "docs/features/*.md"
require_doc 'src/.*/(Authentication|Hardening|Identity|Features/Auth)/' '^docs/threat-model\.md$' \
  "Authentication, hardening or identity code" "docs/threat-model.md"

if [ "$fail" -ne 0 ]; then
  echo "Update the docs named above, or apply the 'docs-not-needed' label / set DOCS_DRIFT_SKIP=1."
  exit 1
fi

echo "docs-drift check passed."
