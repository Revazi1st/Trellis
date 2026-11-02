#!/usr/bin/env bash
# PostToolUse(Edit|Write): if code changed without the doc that describes it, remind (non-blocking).
# The code-to-doc mapping lives in one place, scripts/check-docs-drift.sh, run here over the
# uncommitted changes of the checkout Claude works in (the input's cwd, a worktree included, when it
# holds the script; the project folder otherwise); CI runs the same script over the whole branch,
# which is the hard guarantee.
# A session hears a given report once: the hook keeps the session id and a checksum of the last report
# it sent in the git folder (git rev-parse --git-path claude-hooks/docs-drift.last) and stays silent
# while both are unchanged; a clean run deletes the record, and without git it reminds every time.
# scripts/check-hooks.sh has a case for each.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd)" || exit 0
# shellcheck source=common.sh
. "$here/common.sh" 2>/dev/null || exit 0

read_input || true
session="$(json_string session_id)"
project_root || exit 0
work_root
dir="$PROJECT_DIR"
[ -n "$WORK_DIR" ] && [ -f "$WORK_DIR/scripts/check-docs-drift.sh" ] && dir="$WORK_DIR"
cd "$dir" 2>/dev/null || exit 0

script=scripts/check-docs-drift.sh
[ -f "$script" ] || exit 0
marker="$(git rev-parse --git-path claude-hooks/docs-drift.last 2>/dev/null)" || marker=""

if output="$(GITHUB_ACTIONS='' bash "$script" --working-tree 2>/dev/null)"; then
  [ -z "$marker" ] || rm -f "$marker" 2>/dev/null
  exit 0
fi
case "$output" in
  *"Update the docs named above"*) ;;
  *) exit 0 ;; # not a drift finding (no git, say): stay silent rather than nag
esac

if [ -n "$marker" ]; then
  key="$session $(printf '%s' "$output" | cksum)"
  if [ -f "$marker" ] && [ "$(cat "$marker" 2>/dev/null)" = "$key" ]; then
    exit 0
  fi
  mkdir -p "$(dirname "$marker")" 2>/dev/null && printf '%s\n' "$key" > "$marker" 2>/dev/null
fi

message="Docs drift in the uncommitted changes (scripts/check-docs-drift.sh):
$output
The CI docs-drift check enforces this on PRs; the add-feature skill shows a feature doc's shape."
printf '{"hookSpecificOutput":{"hookEventName":"PostToolUse","additionalContext":"%s"}}\n' "$(json_escape "$message")"
exit 0
