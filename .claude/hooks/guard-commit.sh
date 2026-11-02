#!/usr/bin/env bash
# PreToolUse(Bash): before a command that runs git commit (git perhaps with options such as -C <dir>
# before the subcommand, anywhere in a chain of commands):
#   1. when gitleaks is installed, scans the staged changes with the repository's .gitleaks.toml and
#      denies the commit when it finds a secret, showing the redacted report; a scan that fails for
#      any other reason prints one line on stderr and the commit goes on (CI's secret scan remains);
#   2. reads the subject, the first line of the -m/--message value (quoted, or the first line of a
#      "$(cat <<'EOF' … EOF)" here-document), and asks the person when it is not a Conventional
#      Commits subject, type(scope): subject (AGENTS.md, Boundaries, Commits).
# A message from a file (-F), an editor, --amend without -m or a value only the shell can compute
# ("$MSG") is not read: the hook passes. Every other command is silent. It sees the command before it
# runs, so "git add … && git commit" is scanned as staged before the add. scripts/check-hooks.sh has a
# case for every decision.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd)" || exit 0
# shellcheck source=common.sh
. "$here/common.sh" 2>/dev/null || exit 0

read_input || exit 0
case "$INPUT" in
  *commit*) ;;
  *) exit 0 ;;
esac
command="$(json_string command)"
[ -n "$command" ] || exit 0

# Prints "none" (no git commit here), "pass" (no subject to check) or "subject:<the subject>".
read -r -d '' parse <<'AWK'
BEGIN { RS = "\001"; q = "'" }
function first_line(s) { sub(/\n.*/, "", s); sub(/^[[:space:]]+/, "", s); sub(/[[:space:]]+$/, "", s); return s }
{
  # Padded with a blank at each end, so the patterns need no anchor inside a group (awks differ there).
  s = " " $0 " "
  if (!match(s, /[^A-Za-z0-9_.-]git([[:space:]]+-[^[:space:]]+([[:space:]]+[^-[:space:]][^[:space:]]*)?)*[[:space:]]+commit[[:space:]]/)) {
    print "none"; exit
  }
  rest = substr(s, RSTART + RLENGTH - 1)
  if (!match(rest, "[[:space:]](-[A-Za-z]*m|--message)([[:space:]]+|=|[\"" q "])")) { print "pass"; exit }
  before = substr(rest, 1, RSTART - 1)
  v = substr(rest, RSTART + RLENGTH)
  last = substr(rest, RSTART + RLENGTH - 1, 1)
  if (last == "\"" || last == q) v = last v
  # The flag must belong to this commit, not to a later command of the chain.
  gsub("\"[^\"]*\"|" q "[^" q "]*" q, "", before)
  if (before ~ /[;&|)\n]/) { print "pass"; exit }

  subject = ""
  if (v ~ /^"\$\(cat[[:space:]]*<</) {
    nl = index(v, "\n")
    if (!nl) { print "pass"; exit }
    delim = substr(v, 1, nl - 1)
    sub(/^"\$\(cat[[:space:]]*<<-?[[:space:]]*/, "", delim)
    gsub("[\"" q "]", "", delim)
    sub(/[^A-Za-z0-9_].*$/, "", delim)
    body = substr(v, nl + 1)
    while (body != "") {
      nl = index(body, "\n")
      line = nl ? substr(body, 1, nl - 1) : body
      body = nl ? substr(body, nl + 1) : ""
      line = first_line(line)
      if (line == delim) break
      if (line != "") { subject = line; break }
    }
  } else if (substr(v, 1, 1) == "\"") {
    v = substr(v, 2)
    value = ""
    while (match(v, /[\\"]/)) {
      value = value substr(v, 1, RSTART - 1)
      if (substr(v, RSTART, 1) == "\"") break
      value = value substr(v, RSTART + 1, 1)
      v = substr(v, RSTART + 2)
    }
    if (substr(value, 1, 1) == "$") { print "pass"; exit }
    subject = first_line(value)
  } else if (substr(v, 1, 1) == q) {
    v = substr(v, 2)
    close_at = index(v, q)
    subject = first_line(close_at ? substr(v, 1, close_at - 1) : v)
  } else if (substr(v, 1, 1) != "$") {
    sub(/[[:space:];&|)].*$/, "", v)
    subject = v
  }
  if (subject == "") print "pass"; else print "subject:" subject
}
AWK
verdict="$(printf '%s' "$command" | LC_ALL=C awk "$parse" 2>/dev/null)" || exit 0
case "$verdict" in
  pass | subject:*) ;;
  *) exit 0 ;;   # not a commit, or nothing the parser could read
esac

# 1. The staged changes hold no secret (gitleaks 8.19 and later: "git --pre-commit"; before: "protect").
if command -v gitleaks >/dev/null 2>&1 && project_root && cd "$PROJECT_DIR" 2>/dev/null; then
  version="$(gitleaks version 2>/dev/null | head -n 1)"
  version="${version#v}"
  major="${version%%.*}"
  minor="${version#*.}"
  minor="${minor%%.*}"
  scan="git --pre-commit --staged"
  if [[ "$major" =~ ^[0-9]+$ && "$minor" =~ ^[0-9]+$ ]]; then
    if [ "$major" -lt 8 ] || { [ "$major" -eq 8 ] && [ "$minor" -lt 19 ]; }; then
      scan="protect --staged"
    fi
  fi
  config=""
  [ -f .gitleaks.toml ] && config="--config .gitleaks.toml"
  # shellcheck disable=SC2086 # $scan and $config are several words, none with a space of its own
  report="$(gitleaks $scan --redact --no-banner --verbose $config 2>&1)"
  status=$?
  if [ "$status" -ne 0 ]; then
    case "$report" in
      *"leaks found"*)
        decide deny "gitleaks found a secret in the staged changes, so this commit is refused (AGENTS.md, Never: commit secrets). Unstage it (git restore --staged <file>) and keep the value in user-secrets or the environment (docs/threat-model.md). A false positive gets the narrowest allowlist entry in .gitleaks.toml, as its header explains. The redacted report:
$(printf '%s\n' "$report" | tail -n 20)" ;;
      *)
        echo "guard-commit: gitleaks exited $status without reporting a finding; the staged changes were not scanned." >&2 ;;
    esac
  fi
fi

# 2. The subject is a Conventional Commits subject.
case "$verdict" in
  subject:*) subject="${verdict#subject:}" ;;
  *) exit 0 ;;
esac
style='^(feat|fix|docs|test|refactor|perf|build|ci|chore|style|revert)(\([a-z0-9][a-z0-9./-]*\))?!?: [^[:space:]]'
if ! [[ "$subject" =~ $style ]]; then
  decide ask "Commit style (AGENTS.md, Boundaries, Commits): a Conventional Commits subject, type(scope): subject in the imperative, the type one of feat fix docs test refactor perf build ci chore style revert and the scope optional, in lower case (for example: fix(invoices): refuse a negative quantity). Seen: \"$subject\". Commit anyway?"
fi
exit 0
