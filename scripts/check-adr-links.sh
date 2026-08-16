#!/usr/bin/env bash
# Fails if any "ADR-NNNN" reference in the repo has no matching docs/adr/NNNN-*.md file,
# if any docs/adr/*.md is not listed in docs/INDEX.md, or if a 'paths:' glob in
# .claude/rules/*.md matches zero files (a renamed directory silently unloads a rule).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

fail=0

# --- every ADR reference resolves to a file ---
refs=$(grep -rhoE 'ADR-[0-9]{4}' --include='*.md' --include='*.cs' --include='*.props' . \
  | grep -v '/.git/' | sed 's/ADR-//' | sort -u)

existing=$(find docs/adr -name '*.md' -exec basename {} \; | grep -oE '^[0-9]{4}' | sort -u)

for n in $refs; do
  if ! echo "$existing" | grep -qx "$n"; then
    echo "::error::ADR-$n is referenced but docs/adr/$n-*.md does not exist"
    fail=1
  fi
done

# --- every ADR file is listed in docs/INDEX.md ---
for f in docs/adr/*.md; do
  if ! grep -qF "adr/$(basename "$f")" docs/INDEX.md; then
    echo "::error::$f is not listed in docs/INDEX.md"
    fail=1
  fi
done

# --- every .claude/rules 'paths:' glob matches at least one tracked file ---
for f in .claude/rules/*.md; do
  globs=$(sed -n 's/^paths:[[:space:]]*//p' "$f" | grep -oE '"[^"]+"' | tr -d '"') || true
  [ -n "$globs" ] && while IFS= read -r glob; do
    # glob → extended regex: '**/' crosses directories, '*' does not, '{a,b}' alternates.
    re="${glob//./\\.}"
    re="${re//\*\*\//$'\001'}"
    re="${re//\*\*/$'\002'}"
    re="${re//\*/[^/]*}"
    re="${re//$'\001'/(.*\/)?}"
    re="${re//$'\002'/.*}"
    re="${re//\{/(}"
    re="${re//\}/)}"
    re="${re//,/|}"
    if ! git ls-files --cached --others --exclude-standard | grep -qE "^${re}$"; then
      echo "::error::$f paths glob '$glob' matches no files — the rule will never load"
      fail=1
    fi
  done <<< "$globs"
done

if [ "$fail" -eq 0 ]; then
  echo "ADR link check passed — references resolve, INDEX is complete, rules globs match files."
fi
exit "$fail"
