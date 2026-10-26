#!/usr/bin/env bash
# Fails if an accepted ADR was rewritten instead of amended (ADR-0001). For every file under docs/adr/
# that changed between the branch's base and HEAD:
#   - added                 passes (check-adr-links.sh covers its number and its docs/INDEX.md line)
#   - deleted or renamed    fails: a decision is superseded by a new ADR, never removed
#   - modified              passes only when the old text is an exact prefix of the new text and what
#                           follows it is a dated '## Amendment (YYYY-MM-DD)' section, so an amendment
#                           is only ever appended at the end and nothing above it moves.
# Before the comparison both versions lose their header's Status, Superseded, Amended and Refines
# lines (the '- **Name**:' lines above the first '## ' heading, with their indented continuation
# lines) and the header's template directive comment lines (the if/endif pair that keeps an Amended
# line out of generated projects), CRLF is read as LF and trailing blank lines are ignored. Between
# the old text and the amendment heading only blank lines and directive comment lines may come.
#
# Usage:
#   check-adr-history.sh [<base-ref>]  committed changes since this branch left <base-ref>
#                                      (default origin/main; falls back to main, then HEAD~1,
#                                      saying so, when the ref does not exist here)
#   check-adr-history.sh --self-test   runs the rule against scenarios in a throwaway repository
# Escape hatches for a rewrite that is truly needed: the 'adr-rewrite' PR label (the CI job skips
# itself) or ADR_HISTORY_SKIP=1.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
self="$here/$(basename "${BASH_SOURCE[0]}")"
cd "$here/.."

report() {
  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then echo "::error::$*"; else echo "$*"; fi
}

has_commit() { git rev-parse --verify --quiet "$1^{commit}" >/dev/null; }

# normalise: one version of an ADR on stdin; prints "<line number><TAB><line>" for every line the
# comparison keeps, numbered as in the file.
normalise() {
  awk '
    { sub(/\r$/, "") }
    !body && /^## / { body = 1 }
    !body && /^[[:space:]]*<!--#(if[[:space:]]*\(.*\)|endif)[[:space:]]*-->[[:space:]]*$/ { dropping = 0; next }
    !body && /^- \*\*(Status|Superseded|Amended|Refines)\*\*:/ { dropping = 1; next }
    !body && dropping && /^[[:space:]]+[^[:space:]]/ { next }
    { dropping = 0; print NR "\t" $0 }
  '
}

# compare OLD NEW: two normalise outputs. Prints 0 when NEW is OLD plus an appended amendment (or
# nothing), otherwise the line of the new file where it stops being that.
compare() {
  awk '
    function blank(s) { return s ~ /^[[:space:]]*$/ }
    function directive(s) { return s ~ /^[[:space:]]*<!--#(if[[:space:]]*\(.*\)|endif)[[:space:]]*-->[[:space:]]*$/ }
    {
      tab = index($0, "\t")
      num = substr($0, 1, tab - 1)
      text = substr($0, tab + 1)
    }
    FILENAME == ARGV[1] { old[++olds] = text; next }
    { new[++news] = text; at[news] = num }
    END {
      while (olds > 0 && blank(old[olds])) olds--
      while (news > 0 && blank(new[news])) news--
      for (i = 1; i <= olds; i++) {
        if (i > news) { print (news > 0 ? at[news] + 1 : 1); exit }
        if (old[i] != new[i]) { print at[i]; exit }
      }
      first = 0
      for (i = olds + 1; i <= news; i++) {
        if (blank(new[i])) continue
        if (!first) first = at[i]
        if (directive(new[i])) continue
        if (new[i] ~ /^## Amendment \([0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]\)/) { print 0; exit }
        print at[i]; exit
      }
      print first
    }
  ' "$1" "$2"
}

self_test() {
  local cases=0 failed=0 h='#' adr=docs/adr/0001-an-example.md
  tmp="$(mktemp -d)"   # global: the EXIT trap reads it after this function returns
  trap 'rm -rf "$tmp"' EXIT
  unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE GIT_OBJECT_DIRECTORY GIT_ALTERNATE_OBJECT_DIRECTORIES
  git -c init.defaultBranch=main init -q --template= "$tmp/repo"
  cd "$tmp/repo"
  git symbolic-ref HEAD refs/heads/main
  git config user.name "adr-history self-test"
  git config user.email "adr-history@example.invalid"
  git config commit.gpgsign false
  git config core.autocrlf false
  git config core.hooksPath "$tmp/no-hooks"
  mkdir -p scripts docs/adr .git/info
  cp "$self" scripts/check-adr-history.sh
  printf '/scripts/\n' > .git/info/exclude

  commit() { git add -A && git commit -q --no-verify -m "$1"; }
  rewrite() { awk -v h="$h" "$1" "$adr" > "$adr.new" && mv "$adr.new" "$adr"; }
  amend() { printf '%s\n' '' "## Amendment (2026-10-26): $1" '' 'The decision stands; this says more.' >> "$adr"; }
  # expect NAME pass|fail BASE [TEXT]: runs the check on HEAD against BASE; TEXT must be in its output.
  expect() {
    local name="$1" want="$2" base="$3" text="${4:-}" out got
    cases=$((cases + 1))
    if out="$(GITHUB_ACTIONS='' ADR_HISTORY_SKIP='' bash scripts/check-adr-history.sh "$base" 2>&1)"; then
      got=pass
    else
      got=fail
    fi
    if [ "$got" != "$want" ]; then
      echo "self-test case '$name': expected $want, got $got:"; printf '%s\n' "$out" | sed 's/^/  /'
      failed=$((failed + 1))
    elif [ -n "$text" ] && ! grep -qF -- "$text" <<< "$out"; then
      echo "self-test case '$name': the output does not say '$text':"; printf '%s\n' "$out" | sed 's/^/  /'
      failed=$((failed + 1))
    fi
  }

  printf '%s\n' '# ADR-0001: An example decision' '' '- **Status**: accepted' '- **Date**: 2026-01-01' '' \
    '## Context' '' 'Something forces a choice.' '' '## Decision' '' 'We choose one thing.' '' \
    '## Consequences' '' 'The thing is easy now.' > "$adr"
  commit "an accepted ADR"

  git checkout -q -b amend main
  rewrite '{ print } /^- \*\*Date\*\*:/ { print "- **Amended**: 2026-10-26: a clarification (see *Amendment* below)" }'
  amend "a clarification"
  commit "an appended amendment"
  expect "an appended amendment" pass main

  git checkout -q -b edit main
  rewrite '{ sub(/^We choose one thing\.$/, "We choose another thing."); print }'
  commit "a line under Decision edited"
  expect "a line under Decision edited" fail main "(first differing line 12)"

  git checkout -q -b delete main
  git rm -q "$adr"
  commit "the ADR deleted"
  expect "the ADR deleted" fail main "never deleted or renamed"

  git checkout -q -b supersede main
  rewrite '{ sub(/^- \*\*Status\*\*: accepted$/, "- **Status**: Superseded by ADR-0002"); print }'
  commit "the Status line changed"
  expect "the Status line changed" pass main

  git checkout -q -b add main
  printf '%s\n' '# ADR-0100: A project decision' '' '- **Status**: accepted' '' '## Context' '' 'New.' \
    > docs/adr/0100-a-project-decision.md
  commit "a new ADR"
  expect "a new ADR" pass main

  git checkout -q -b wrapped main
  rewrite '{ print } /^- \*\*Date\*\*:/ { print "<!--" h "if (TemplateSource) -->"
    print "- **Amended**: 2026-10-26: for the template only (see *Amendment* below)"; print "<!--" h "endif -->" }'
  printf '%s\n' "<!--${h}if (TemplateSource) -->" '' '## Amendment (2026-10-26): for the template only' '' \
    'Only the template says this.' "<!--${h}endif -->" >> "$adr"
  commit "an amendment inside a directive block"
  expect "an amendment inside a directive block" pass main

  git checkout -q -b middle main
  rewrite '{ print } NR == 12 { print ""; print "## Amendment (2026-10-26): in the middle"; print ""; print "Inserted." }'
  commit "an amendment heading in the middle"
  expect "an amendment heading in the middle" fail main "(first differing line 14)"

  git checkout -q -b unheaded main
  printf '%s\n' '' 'More text, with no heading.' >> "$adr"
  commit "text appended without a heading"
  expect "text appended without a heading" fail main "(first differing line 18)"

  git checkout -q -b second amend
  amend "a second clarification"
  commit "a second amendment"
  expect "a second amendment after the first" pass amend

  git checkout -q -b crlf main
  amend "with Windows line endings"
  rewrite '{ printf "%s\r\n", $0 }'
  commit "CRLF line endings and an appended amendment"
  expect "CRLF line endings and an appended amendment" pass main

  if [ "$failed" -ne 0 ]; then
    echo "adr-history self-test failed: $failed of $cases cases"
    exit 1
  fi
  echo "adr-history self-test passed: $cases cases"
}

if [ "${1:-}" = "--self-test" ]; then
  self_test
  exit 0
fi

if [ "${ADR_HISTORY_SKIP:-0}" = "1" ]; then
  echo "adr-history check skipped (ADR_HISTORY_SKIP=1)."
  exit 0
fi

base="${1:-origin/main}"
if ! has_commit "$base"; then
  fallback=""
  if has_commit main && [ "$(git rev-parse main)" != "$(git rev-parse HEAD)" ]; then
    fallback=main
  elif has_commit HEAD~1; then
    fallback=HEAD~1
  fi
  if [ -z "$fallback" ]; then
    echo "adr-history: '$base' does not exist here and there is no main branch or parent commit to compare against; nothing to check."
    exit 0
  fi
  echo "adr-history: '$base' does not exist here (no remote fetched?); comparing against '$fallback' instead."
  base="$fallback"
fi

if ! fork="$(git merge-base "$base" HEAD)"; then
  report "adr-history: '$base' and HEAD share no history; there is nothing to compare against."
  exit 1
fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
git diff -z -M --no-color --no-ext-diff --name-status "$fork" HEAD -- docs/adr/ > "$work/changes"

fail=0
while IFS= read -r -d '' status; do
  IFS= read -r -d '' path
  case "$status" in
    R*|C*) IFS= read -r -d '' _ ;;
  esac
  case "$status" in
    A|C*) ;;
    D|R*)
      report "$path: an ADR is never deleted or renamed; supersede it with a new ADR"
      fail=1 ;;
    M|T)
      git cat-file blob "$fork:$path" | normalise > "$work/old"
      git cat-file blob "HEAD:$path" | normalise > "$work/new"
      line="$(compare "$work/old" "$work/new")"
      if [ "$line" != "0" ]; then
        report "$path: changed outside an appended '## Amendment (YYYY-MM-DD)' section (first differing line $line); add a dated amendment at the end or a superseding ADR"
        fail=1
      fi ;;
    *)
      report "$path: unexpected change ($status) to an ADR"
      fail=1 ;;
  esac
done < "$work/changes"

if [ "$fail" -ne 0 ]; then
  echo "An accepted ADR changes only by a dated amendment appended at its end, and a reversal is a new ADR (ADR-0001)."
  echo "If a rewrite is truly needed, apply the 'adr-rewrite' label / set ADR_HISTORY_SKIP=1."
  exit 1
fi

echo "adr-history check passed."
