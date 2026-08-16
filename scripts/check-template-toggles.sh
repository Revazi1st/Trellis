#!/usr/bin/env bash
# Fails if the template toggle surface drifts: every boolean parameter in
# .template.config/template.json must be exposed in dotnetcli.host.json (CLI flag) AND
# ide.host.json (VS/Rider checkbox), and every '#if (X' symbol in template sources must
# be a symbol declared in template.json (a typo'd symbol silently strips code).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

fail=0

# --- every boolean parameter is exposed in both host files ---
bool_params=$(awk '
  /^  "symbols": \{/ { insym = 1; next }
  insym && /^  \}/   { insym = 0 }
  insym && /^    "/  { name = $1; gsub(/[":{]/, "", name) }
  insym && /"datatype": "bool"/ { print name }
' .template.config/template.json)

for p in $bool_params; do
  if ! grep -q "\"$p\"" .template.config/dotnetcli.host.json; then
    echo "::error::boolean symbol '$p' has no CLI flag in dotnetcli.host.json"
    fail=1
  fi
  if ! grep -q "\"id\": \"$p\"" .template.config/ide.host.json; then
    echo "::error::boolean symbol '$p' is not exposed in ide.host.json"
    fail=1
  fi
done

# --- every '#if (X' / '#if (!X' symbol in sources is a declared symbol ---
declared=$(awk '
  /^  "symbols": \{/ { insym = 1; next }
  insym && /^  \}/   { insym = 0 }
  insym && /^    "/  { name = $1; gsub(/[":{]/, "", name); print name }
' .template.config/template.json)

used=$( { find src tests tools -type f \( -name '*.cs' -o -name '*.csproj' -o -name '*.slnx' \) \
    -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/artifacts/*' -print0; \
    find . -maxdepth 1 -name '*.slnx' -print0; } \
  | xargs -0 grep -hoE '#if[[:space:]]*\([^)]*' 2>/dev/null \
  | grep -oE '[A-Za-z_][A-Za-z0-9_]*' | grep -vx 'if' | sort -u) || true

for s in $used; do
  if ! echo "$declared" | grep -qx "$s"; then
    echo "::error::'#if ($s' is used in template sources but '$s' is not declared in template.json"
    fail=1
  fi
done

if [ "$fail" -eq 0 ]; then
  echo "template-toggle check passed — hosts cover all booleans, all #if symbols are declared."
fi
exit "$fail"
