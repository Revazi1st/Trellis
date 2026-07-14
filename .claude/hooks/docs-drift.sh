#!/usr/bin/env bash
# PostToolUse(Edit|Write): if a feature slice changed but its docs didn't, remind (non-blocking).
# Deterministic reminder only — the hard guarantee is scripts/check-docs-drift.sh in CI.
set -euo pipefail
cd "${CLAUDE_PROJECT_DIR:-.}"

changed="$(git status --porcelain 2>/dev/null || true)"
features_changed="$(printf '%s\n' "$changed" | grep -E 'src/.*/Features/' || true)"
docs_changed="$(printf '%s\n' "$changed" | grep -E 'docs/features/' || true)"

if [ -n "$features_changed" ] && [ -z "$docs_changed" ]; then
  cat <<'JSON'
{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"A feature slice under **/Features/** changed but no docs/features/*.md did. If this adds or changes a feature's behavior, update its doc under docs/features/ (see the add-feature skill). The CI docs-drift check enforces this on PRs."}}
JSON
fi
exit 0
