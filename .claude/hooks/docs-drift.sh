#!/usr/bin/env bash
# PostToolUse(Edit|Write): if code changed without the doc that describes it, remind (non-blocking).
# The code-to-doc mapping lives in one place, scripts/check-docs-drift.sh, run here over the
# uncommitted changes; CI runs the same script over the whole branch, which is the hard guarantee.
set -uo pipefail
cd "${CLAUDE_PROJECT_DIR:-.}" || exit 0

script=scripts/check-docs-drift.sh
[ -f "$script" ] || exit 0

output="$(GITHUB_ACTIONS='' bash "$script" --working-tree 2>/dev/null)" && exit 0
case "$output" in
  *"Update the docs named above"*) ;;
  *) exit 0 ;; # not a drift finding (no git, say): stay silent rather than nag
esac

# JSON-escape the report: backslashes and quotes, then newlines as \n.
escaped="$(printf '%s\n' "$output" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | awk 'BEGIN { ORS = "\\n" } { print }')"
printf '{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"%s"}}\n' \
  "Docs drift in the uncommitted changes (scripts/check-docs-drift.sh):\\n${escaped}The CI docs-drift check enforces this on PRs; the add-feature skill shows a feature doc's shape."
exit 0
