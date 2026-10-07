#!/usr/bin/env bash
# Fails if the template toggle surface drifts: every boolean parameter in
# .template.config/template.json must be exposed in dotnetcli.host.json (CLI flag) AND
# ide.host.json (VS/Rider checkbox), and every '#if (X' symbol in template sources must
# be a symbol declared in template.json (a typo'd symbol silently strips code).
#
# YAML files (workflows, compose, dependabot) get two more checks, because the template engine
# processes hash-comment conditionals in them: a conditional directive must sit alone on its line
# (the engine matches the token ANYWHERE in a line, so one inside a run: script or a comment
# silently drops the rest of the file from generated output), and the directives must balance.
# Files the template excludes from its output (template-ci.yml, release.yml) are not checked.
#
# Markdown and shell files get the mid-line check too: the engine's if token is "<!--#if" in
# .md and "#if" in .sh, matched anywhere in a line, so a token quoted in prose truncates the file.
#
# NOTE: template.json excludes this file from the template output (it is maintainer-only), so the
# engine never processes it. The regexes still write the closing/alternative directive tokens as
# '#(…|…)' alternations, which keeps the file safe should that exclusion ever be dropped.
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

# The symbol list is read by indentation; if template.json is ever reformatted, fail loudly
# instead of passing a check that compared against nothing.
if [ -z "$declared" ]; then
  echo "::error::read no symbols from .template.config/template.json (indentation changed?)"
  exit 1
fi

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

# --- YAML: directives alone on their line, balanced, parenthesised, declared symbols only ---
# Prints "ERR<TAB>line<TAB>message" and "SYM<TAB>line<TAB>symbol" records for one file.
yaml_conditionals() {
  awk '
    function report(msg) { printf "ERR\t%d\t%s\n", FNR, msg }
    {
      text = $0
      sub(/^[[:space:]]+/, "", text)
      directive = ""
      if (match(text, /^#(elseif|elif|else|endif|if)/)) {
        directive = substr(text, 2, RLENGTH - 1)
        rest = substr(text, RLENGTH + 1)
      } else {
        rest = text
      }
      if (rest ~ /#(if|elif|elseif|else|endif)/) {
        report("a template conditional token appears inside a line; the engine would drop the rest of the file from generated output. Keep conditionals on lines of their own and reword the text")
      }
      if (directive == "") next

      if (directive == "if" || directive == "elif" || directive == "elseif") {
        if (rest !~ /^[[:space:]]*\(.*\)[[:space:]]*$/) {
          report("write the condition in parentheses, for example: " "#" directive " (IncludeUI)")
        }
        cond = rest
        while (match(cond, /[A-Za-z_][A-Za-z0-9_]*/)) {
          printf "SYM\t%d\t%s\n", FNR, substr(cond, RSTART, RLENGTH)
          cond = substr(cond, RSTART + RLENGTH)
        }
      }
      if (directive == "if") { depth++; seen_else[depth] = 0; next }
      if (depth == 0) { report("the " directive " directive has no opening if directive"); next }
      if (directive == "else") {
        if (seen_else[depth]) report("a second else directive in the same block")
        seen_else[depth] = 1
      } else if (directive == "endif") {
        depth--
      } else if (seen_else[depth]) {
        report("the " directive " directive follows the else directive of its block")
      }
    }
    END { if (depth > 0) printf "ERR\t%d\t%s\n", FNR, depth " conditional block(s) left open at the end of the file" }
  ' "$1"
}

yaml_files=$(find . \( -path ./.git -o -path ./.claude/worktrees -o -path ./artifacts -o -name node_modules \
    -o -name bin -o -name obj -o -name TestResults \) -prune -o -type f \( -name '*.yml' -o -name '*.yaml' \) -print \
  | sed 's|^\./||' | sort)

# Source-only files (excluded from the template output) are never processed by the engine:
# an exact entry in template.json's exclude list, or a parent folder listed as "dir/**".
source_only() {
  local p="$1"
  grep -qF "\"$p\"" .template.config/template.json && return 0
  while [ "$p" != "." ]; do
    p=$(dirname "$p")
    grep -qF "\"$p/**\"" .template.config/template.json && return 0
  done
  return 1
}

for f in $yaml_files; do
  if source_only "$f"; then
    continue
  fi
  while IFS=$'\t' read -r kind line value; do
    [ -n "$kind" ] || continue
    if [ "$kind" = "ERR" ]; then
      echo "::error file=$f,line=$line::$value"
      fail=1
    elif ! echo "$declared" | grep -qx "$value"; then
      echo "::error file=$f,line=$line::template conditional uses '$value', which is not a symbol declared in template.json"
      fail=1
    fi
  done <<< "$(yaml_conditionals "$f")"
done

# --- Markdown and shell: a directive token may only open a line ---
text_files=$(find . \( -path ./.git -o -path ./.claude/worktrees -o -path ./artifacts -o -path ./eng -o -name node_modules \
    -o -name bin -o -name obj -o -name TestResults \) -prune -o -type f \( -name '*.md' -o -name '*.sh' \) -print \
  | sed 's|^\./||' | sort)

for f in $text_files; do
  if source_only "$f"; then
    continue
  fi
  case "$f" in
    *.md) token='<!--#(if|elseif|else|endif)' ;;
    *)    token='#(if|elseif|elif|else|endif)([^A-Za-z0-9_]|$)' ;;
  esac
  while IFS=: read -r line _; do
    [ -n "$line" ] || continue
    echo "::error file=$f,line=$line::a template conditional token appears inside a line; the engine would drop the rest of the generated file. Keep directives on lines of their own and describe the token in prose"
    fail=1
  done <<< "$(grep -nE "$token" "$f" | grep -vE "^[0-9]+:[[:space:]]*$token" || true)"
done

if [ "$fail" -eq 0 ]; then
  echo "template-toggle check passed — hosts cover all booleans, all #if symbols are declared, YAML/Markdown/shell conditionals are well-formed."
fi
exit "$fail"
