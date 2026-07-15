#!/usr/bin/env bash
# Fails if any "ADR-NNNN" reference in the repo has no matching docs/adr/NNNN-*.md file.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

missing=0
refs=$(grep -rhoE 'ADR-[0-9]{4}' --include='*.md' --include='*.cs' --include='*.props' . \
  | grep -v '/.git/' | sed 's/ADR-//' | sort -u)

existing=$(find docs/adr -name '*.md' -exec basename {} \; | grep -oE '^[0-9]{4}' | sort -u)

for n in $refs; do
  if ! echo "$existing" | grep -qx "$n"; then
    echo "::error::ADR-$n is referenced but docs/adr/$n-*.md does not exist"
    missing=1
  fi
done

if [ "$missing" -eq 0 ]; then
  echo "ADR link check passed — all ADR references resolve."
fi
exit "$missing"
