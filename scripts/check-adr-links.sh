#!/usr/bin/env bash
# Fails if any "ADR-NNNN" reference in the repo has no matching docs/adr/NNNN-*.md file,
# if any docs/adr/*.md is not listed in docs/INDEX.md (template ADRs 0001-0099 above the
# '## Project decisions' heading, project ADRs 0100 and up below it), if two ADRs share a number,
# if the template's own repository holds an ADR numbered 0100 or more, if a 'paths:' glob in
# .claude/rules/*.md matches zero files (a renamed directory silently unloads a rule), if a skill
# is missing from the Skills section of docs/INDEX.md (once that section exists), or if a relative
# link in a Markdown file points at a path that does not exist (a moved or renamed doc).
set -euo pipefail
# Under pipefail, "echo … | grep -q" is a race: grep exits on the first match, echo can get EPIPE and
# the pipeline fails although the match was found. Membership tests use here-strings instead.
cd "$(dirname "${BASH_SOURCE[0]}")/.."

fail=0

# --- every ADR reference resolves to a file ---
# The same prune list as the link check below: a git worktree under .claude/worktrees (an agent
# working on another branch) or a build folder must not make this checkout's references fail.
refs=$(grep -rhoE 'ADR-[0-9]{4}' --include='*.md' --include='*.cs' --include='*.props' \
  --exclude-dir=.git --exclude-dir=worktrees --exclude-dir=artifacts --exclude-dir=bin --exclude-dir=obj \
  --exclude-dir=node_modules --exclude-dir=TestResults . \
  | sed 's/ADR-//' | sort -u)

existing=$(find docs/adr -name '*.md' -exec basename {} \; | grep -oE '^[0-9]{4}' | sort -u)

for n in $refs; do
  if ! grep -qx "$n" <<< "$existing"; then
    echo "::error::ADR-$n is referenced but docs/adr/$n-*.md does not exist"
    fail=1
  fi
done

# --- every ADR file is listed in docs/INDEX.md, in the section its number belongs to ---
# Numbers 0001-0099 are the template's ADRs; a generated project numbers its own from 0100, so a
# template upgrade that brings new ADRs never collides with the project's (new-adr skill).
project_heading=$(grep -n '^## Project decisions' docs/INDEX.md | head -n 1 | cut -d: -f1) || true
if [ -z "$project_heading" ]; then
  echo "::error::docs/INDEX.md has no '## Project decisions' section (ADRs numbered 0100 and up are listed there)"
  fail=1
fi
for f in docs/adr/*.md; do
  listed=$(grep -nF "adr/$(basename "$f")" docs/INDEX.md | head -n 1 | cut -d: -f1) || true
  if [ -z "$listed" ]; then
    echo "::error::$f is not listed in docs/INDEX.md"
    fail=1
    continue
  fi
  n=$(basename "$f" | grep -oE '^[0-9]{4}') || continue
  [ -n "$project_heading" ] || continue
  if [ $((10#$n)) -ge 100 ] && [ "$listed" -lt "$project_heading" ]; then
    echo "::error::$f is a project ADR (0100 and up): list it under '## Project decisions' in docs/INDEX.md"
    fail=1
  elif [ $((10#$n)) -lt 100 ] && [ "$listed" -gt "$project_heading" ]; then
    echo "::error::$f is a template ADR (0001-0099): list it above '## Project decisions' in docs/INDEX.md"
    fail=1
  fi
done

# --- no two ADRs share a number; the template's own repository stays below 0100 ---
for n in $(find docs/adr -name '[0-9][0-9][0-9][0-9]-*.md' -exec basename {} \; | cut -c1-4 | sort | uniq -d); do
  echo "::error::more than one docs/adr/$n-*.md: number your own ADRs from 0100 (0001-0099 are the template's)"
  fail=1
done
if [ -f .template.config/template.json ]; then
  for f in docs/adr/[0-9][0-9][0-9][0-9]-*.md; do
    n=$(basename "$f" | cut -c1-4)
    if [ $((10#$n)) -ge 100 ]; then
      echo "::error::$f: the template's ADRs are numbered 0001-0099; 0100 and up belong to generated projects"
      fail=1
    fi
  done
fi

# --- every .claude/rules 'paths:' glob points at an existing, non-empty base directory ---
# (The failure mode this guards against is a renamed directory silently unloading a rule, so a
# simple existence probe on the pre-wildcard prefix is deliberately used instead of glob matching.)
for f in .claude/rules/*.md; do
  globs=$(sed -n 's/^paths:[[:space:]]*//p' "$f" | grep -oE '"[^"]+"' | tr -d '"') || true
  [ -n "$globs" ] && while IFS= read -r glob; do
    base="${glob%%\**}"   # longest prefix before the first wildcard
    base="${base%/}"
    if [ -n "$base" ] && [ ! -e "$base" ]; then
      echo "::error::$f paths glob '$glob' points at missing path '$base' — the rule will never load"
      fail=1
    fi
  done <<< "$globs"
  # docs/INDEX.md lists the rule with the globs its frontmatter declares, so an agent reading the
  # index knows when the rule loads.
  entry=$(grep -F "(../.claude/rules/$(basename "$f"))" docs/INDEX.md | head -n 1) || true
  if [ -z "$entry" ]; then
    echo "::error::$f is not listed under 'Layer rules' in docs/INDEX.md"
    fail=1
  elif [ -n "$globs" ]; then
    while IFS= read -r glob; do
      if ! grep -qF "\`$glob\`" <<< "$entry"; then
        echo "::error::docs/INDEX.md lists $f without its paths glob '$glob' (keep the entry in step with the frontmatter)"
        fail=1
      fi
    done <<< "$globs"
  fi
done

# --- every skill is listed in docs/INDEX.md, once the index has a Skills section ---
# Each .claude/skills/<name>/SKILL.md appears there as a (../.claude/skills/<name>/SKILL.md) link,
# so an agent reading the index finds every skill. A listed skill that no longer exists is a broken
# link, which the next check reports.
if grep -q '^## Skills' docs/INDEX.md; then
  for f in .claude/skills/*/SKILL.md; do
    [ -e "$f" ] || continue
    if ! grep -qF "(../$f)" docs/INDEX.md; then
      echo "::error::$f is not listed under '## Skills' in docs/INDEX.md"
      fail=1
    fi
  done
fi

# --- every relative Markdown link resolves ---
# Inline links and images, [text](path) and ![alt](path), outside fenced code blocks and inline code
# spans. URLs (any scheme), protocol-relative and same-page #anchor links are skipped, and so is the
# #anchor part of a path: the file must exist, its headings are not checked. A path that starts
# with / is taken from the repository root, as GitHub renders it. .template.config/consumer/ holds
# the files a generated project gets at its root, so their links resolve only there and are checked
# there (Template CI runs this script in every generated project).
md_links() {  # prints "line<TAB>target" for each link in one Markdown file
  awk '
    {
      text = $0
      if (fence != "") {
        if (match(text, /^[[:space:]]*(```+|~~~+)[[:space:]]*$/)) {
          marker = substr(text, RSTART, RLENGTH)
          gsub(/[[:space:]]/, "", marker)
          if (substr(marker, 1, 1) == substr(fence, 1, 1) && length(marker) >= length(fence)) fence = ""
        }
        next
      }
      if (match(text, /^[[:space:]]*(```+|~~~+)/)) {
        fence = substr(text, RSTART, RLENGTH)
        gsub(/[[:space:]]/, "", fence)
        next
      }
      gsub(/`[^`]*`/, "", text)
      while (match(text, /\]\([^)]*\)/)) {
        printf "%d\t%s\n", FNR, substr(text, RSTART + 2, RLENGTH - 3)
        text = substr(text, RSTART + RLENGTH)
      }
    }
  ' "$1"
}

md_files=$(find . \( -path ./.git -o -path ./.claude/worktrees -o -path ./.template.config/consumer \
    -o -path ./artifacts -o -name node_modules -o -name bin -o -name obj -o -name TestResults \) -prune \
    -o -type f -name '*.md' -print | sed 's|^\./||' | sort)

while IFS= read -r f; do
  [ -n "$f" ] || continue
  while IFS=$'\t' read -r line target; do
    [ -n "$line" ] || continue
    case "$target" in
      '<'*) target="${target#<}"; target="${target%%>*}" ;;   # <path with spaces>
      *) target="${target%%[[:space:]]*}" ;;                  # drop a "title"
    esac
    case "$target" in
      '' | '#'* | '//'*) continue ;;
    esac
    if [[ "$target" =~ ^[A-Za-z][A-Za-z0-9+.-]*: ]]; then
      continue   # https:, mailto: and every other scheme
    fi
    path="${target%%#*}"
    path="${path%%\?*}"
    path="${path//%20/ }"
    case "$path" in
      /*) resolved=".$path" ;;
      *) resolved="$(dirname "$f")/$path" ;;
    esac
    if [ ! -e "$resolved" ]; then
      echo "::error file=$f,line=$line::link target '$target' does not exist (looked for ${resolved#./})"
      fail=1
    fi
  done <<< "$(md_links "$f")"
done <<< "$md_files"

if [ "$fail" -eq 0 ]; then
  echo "ADR link check passed — references resolve, INDEX is complete, rules globs match files, relative links resolve."
fi
exit "$fail"
