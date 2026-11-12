#!/usr/bin/env bash
# Fails if code changed without the doc that describes it changing too (ADR-0014):
#   - a feature's code                                         -> docs/features/<feature>.md
#   - authentication, authorization (the role grants), hardening, identity or the Auth feature
#                                                              -> docs/threat-model.md
#   - the UI's stylesheets and layout                          -> docs/design-system.md
#
# "A feature's code" is every folder the feature docs describe, mapped to ONE doc by its name
# (kebab-cased: Invoices -> invoices.md, ExchangeRates -> exchange-rates.md):
#   src/**/Features/<Feature>/**                       Application use cases and API endpoints
#   src/Core/*.Domain/<Feature>/**                     the aggregate (Domain/Common: see below)
#   src/Presentation/Api/*.Api.Contracts/<Feature>/**  the wire contracts
#   src/Infrastructure/*.Infrastructure/ExternalApis/<Integration>/**
#   src/Infrastructure/*.Persistence/{Configurations,Repositories}/<Entity>{Configuration,Repository}.cs
#                                                      the feature whose Domain folder holds <Entity>.cs
#   src/Presentation/UI/*.UI.Common/Pages/<Page>.razor the feature whose singular name the page
#                                                      name contains (InvoiceDetail -> Invoices)
# Shared code the feature docs cover but no single feature owns (Domain/Common, Contracts/Common,
# a page or entity no feature claims) needs some docs/features/*.md change.
# A feature doc this change touches or owes, for a feature with an aggregate (a src/Core/*.Domain/<Feature>/
# folder), carries the H2s the add-feature skill names, so every decision has the same place in every
# doc: The domain in plain words, Try it, Where the code lives, Domain, Application, Persistence, API,
# UI (with the UI), Tests, Endpoints. An integration's doc (exchange-rates.md) or a cross-cutting one
# (auth.md) has no aggregate folder and keeps its own shape.
# The design system is one doc for the files that are the system, not a feature's:
#   src/Presentation/UI/*.UI.Common/{wwwroot,Layout}/**  -> docs/design-system.md
#
# Usage:
#   check-docs-drift.sh [<base-ref>]   committed changes since this branch left <base-ref>
#                                      (default origin/main; falls back to main, then HEAD~1,
#                                      saying so, when the ref does not exist here)
#   check-docs-drift.sh --working-tree uncommitted changes, staged or not, untracked included
#                                      (the Claude Code PostToolUse reminder in .claude/hooks/)
# Escape hatches for changes that genuinely alter no behaviour: the 'docs-not-needed' PR label
# (the CI job skips itself) or DOCS_DRIFT_SKIP=1.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

if [ "${DOCS_DRIFT_SKIP:-0}" = "1" ]; then
  echo "docs-drift check skipped (DOCS_DRIFT_SKIP=1)."
  exit 0
fi

report() {
  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then echo "::error::$*"; else echo "$*"; fi
}

has_commit() { git rev-parse --verify --quiet "$1^{commit}" >/dev/null; }

if [ "${1:-}" = "--working-tree" ]; then
  # Porcelain v1: "XY path" or "XY old -> new"; the new path is the one that matters.
  changed="$(git status --porcelain --untracked-files=all | cut -c4- | sed 's/.* -> //')"
else
  base="${1:-origin/main}"
  if ! has_commit "$base"; then
    fallback=""
    if has_commit main && [ "$(git rev-parse main)" != "$(git rev-parse HEAD)" ]; then
      fallback=main
    elif has_commit HEAD~1; then
      fallback=HEAD~1
    fi
    if [ -z "$fallback" ]; then
      echo "docs-drift: '$base' does not exist here and there is no main branch or parent commit to compare against; nothing to check."
      exit 0
    fi
    echo "docs-drift: '$base' does not exist here (no remote fetched?); comparing against '$fallback' instead."
    base="$fallback"
  fi
  changed="$(git diff --name-only "$base"...HEAD)"
fi

kebab() { printf '%s' "$1" | sed -E 's/([a-z0-9])([A-Z])/\1-\2/g' | tr '[:upper:]' '[:lower:]'; }

# The feature folders that exist now (Application features plus Domain aggregates), for pages.
known_features="$( (find src -path '*/Features/*' -prune -type d -name '[A-Z]*' 2>/dev/null || true;
                    find src/Core -path '*.Domain/*' -prune -type d 2>/dev/null || true) \
  | sed 's|.*/||' | grep -vxE 'Common|bin|obj' | sort -u || true)"

# The features with an aggregate, whose docs carry the headings below.
domain_features="$( (find src/Core -path '*.Domain/*' -prune -type d 2>/dev/null || true) \
  | sed 's|.*/||' | grep -vxE 'Common|bin|obj' | sort -u || true)"

# The H2s of such a doc, one per line; a heading may go on after its name ("## API (`src/…`)").
required_headings='The domain in plain words
Try it
Where the code lives
Domain
Application
Persistence
API
Tests
Endpoints'
#if (IncludeUI)
required_headings="$required_headings
UI"
#endif

# feature_of <path>: prints the feature that owns the path, "*" when the feature docs cover it but
# no single feature does, nothing when no feature doc covers it.
feature_of() {
  local path="$1" name entity home feature singular
  case "$path" in
    src/*/Features/*/*)
      name="${path#*/Features/}"; printf '%s\n' "${name%%/*}" ;;
    src/Core/*.Domain/Common/*)
      printf '*\n' ;;
    src/Core/*.Domain/*/*)
      name="${path#src/Core/*.Domain/}"; printf '%s\n' "${name%%/*}" ;;
    src/Presentation/Api/*.Api.Contracts/Common/*)
      printf '*\n' ;;
    src/Presentation/Api/*.Api.Contracts/*/*)
      name="${path#src/Presentation/Api/*.Api.Contracts/}"; printf '%s\n' "${name%%/*}" ;;
    src/Infrastructure/*.Infrastructure/ExternalApis/*/*)
      name="${path#*/ExternalApis/}"; printf '%s\n' "${name%%/*}" ;;
    src/Infrastructure/*.Persistence/Configurations/*Configuration.cs | \
    src/Infrastructure/*.Persistence/Repositories/*Repository.cs)
      entity="$(basename "$path" .cs)"; entity="${entity%Configuration}"; entity="${entity%Repository}"
      home="$(find src/Core -path "*.Domain/*/$entity.cs" 2>/dev/null | head -n 1 || true)"
      if [ -n "$home" ]; then
        home="${home#src/Core/*.Domain/}"; printf '%s\n' "${home%%/*}"
      else
        printf '*\n'
      fi ;;
    src/Presentation/UI/*.UI.Common/Pages/*)
      name="$(basename "$path")"; name="${name%%.*}"
      for feature in $known_features; do
        singular="${feature%s}"
        case "$name" in *"$singular"*) printf '%s\n' "$feature"; return ;; esac
      done
      printf '*\n' ;;
  esac
}

fail=0
feature_doc_changed="$(printf '%s\n' "$changed" | grep -E '^docs/features/[^/]+\.md$' || true)"

# Group the changed code paths by feature ("feature<TAB>path"), then check each feature's doc.
owned="$(printf '%s\n' "$changed" | while IFS= read -r path; do
  [ -n "$path" ] || continue
  feature="$(feature_of "$path")"
  [ -n "$feature" ] && printf '%s\t%s\n' "$feature" "$path"
done || true)"

for feature in $(printf '%s\n' "$owned" | cut -f1 | grep -vx '\*' | sort -u); do
  doc="docs/features/$(kebab "$feature").md"
  if ! grep -qxF "$doc" <<< "$changed"; then
    if [ -f "$doc" ]; then
      report "The $feature feature changed without a $doc update:"
    else
      report "The $feature feature changed but has no $doc (add one: the add-feature skill shows the shape):"
    fi
    printf '%s\n' "$owned" | awk -F '\t' -v f="$feature" '$1 == f { print "  " $2 }'
    fail=1
  fi
done

shared="$(printf '%s\n' "$owned" | awk -F '\t' '$1 == "*" { print "  " $2 }')"
if [ -n "$shared" ] && [ -z "$feature_doc_changed" ]; then
  report "Code the feature docs describe changed without any docs/features/*.md update:"
  printf '%s\n' "$shared"
  fail=1
fi

# The feature docs in scope: every one this change touches, and the doc of every feature whose code changed.
scoped_docs="$( (printf '%s\n' "$feature_doc_changed"
  printf '%s\n' "$owned" | cut -f1 | grep -vx '\*' | sort -u | while IFS= read -r feature; do
    if [ -n "$feature" ]; then printf 'docs/features/%s.md\n' "$(kebab "$feature")"; fi
  done) | grep -v '^$' | sort -u || true)"

for doc in $scoped_docs; do
  [ -f "$doc" ] || continue   # a missing doc is reported above
  owner=""
  for feature in $domain_features; do
    if [ "docs/features/$(kebab "$feature").md" = "$doc" ]; then owner="$feature"; fi
  done
  [ -n "$owner" ] || continue   # no aggregate: an integration's or a cross-cutting doc keeps its own shape
  missing="$(printf '%s\n' "$required_headings" | while IFS= read -r heading; do
    grep -qE "^## ${heading}( |\$)" "$doc" || printf '  ## %s\n' "$heading"
  done)"
  if [ -n "$missing" ]; then
    report "$doc lacks headings a feature with an aggregate carries (the add-feature skill names them, in invoices.md's order):"
    printf '%s\n' "$missing"
    fail=1
  fi
done

design_changed="$(printf '%s\n' "$changed" | grep -E '^src/Presentation/UI/[^/]+\.UI\.Common/(wwwroot|Layout)/' || true)"
if [ -n "$design_changed" ] && ! grep -qxF 'docs/design-system.md' <<< "$changed"; then
  report "The UI's stylesheets or layout changed without a docs/design-system.md update:"
  printf '%s\n' "$design_changed" | sed 's/^/  /'
  fail=1
fi

security_changed="$(printf '%s\n' "$changed" | grep -E '^src/.*/(Authentication|Authorization|Hardening|Identity|Features/Auth)/' || true)"
if [ -n "$security_changed" ] && ! grep -qxF 'docs/threat-model.md' <<< "$changed"; then
  report "Authentication, authorization, hardening or identity code changed without a docs/threat-model.md update:"
  printf '%s\n' "$security_changed" | sed 's/^/  /'
  fail=1
fi

if [ "$fail" -ne 0 ]; then
  echo "Update the docs named above, or apply the 'docs-not-needed' label / set DOCS_DRIFT_SKIP=1 if behaviour did not change."
  exit 1
fi

echo "docs-drift check passed."
