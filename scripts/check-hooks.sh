#!/usr/bin/env bash
# Self-test of the Claude Code hooks in .claude/hooks/ (docs/development.md, "Agent hooks"). Checks
# that every hook script parses (bash -n) and is executable, then feeds each hook the JSON Claude Code
# would send it for the cases below and fails unless the hook exits 0 with the expected decision:
#   deny | ask   a PreToolUse permission decision on stdout
#   block        a Stop decision on stdout
#   remind       PostToolUse additional context on stdout
#   silent       nothing on stdout
# Prints "hook self-test passed: N cases", or each failing case with the hook's stdout and stderr.
#
# A new decision in a hook gets a case here, and so does the silence next to it (the edit it must
# not ask about). Nothing is installed and nothing outside a temporary folder is written: gitleaks is
# replaced by a stand-in for every commit case, so the result is the same with or without it, and the
# docs-drift cases build a scratch git repository (they are skipped, saying so, without git).
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
root="$PWD"
hooks="$root/.claude/hooks"
unset GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE

work="$(mktemp -d)" || { echo "check-hooks: cannot create a temporary folder"; exit 1; }
trap 'rm -rf "$work"' EXIT

cases=0
failed=0
fail() {
  failed=$((failed + 1))
  printf 'FAIL %s\n' "$1"
  shift
  [ "$#" -eq 0 ] || printf '  %s\n' "$@"
}

# --- every hook script parses, and every hook is executable (Claude Code runs it directly) ---
for f in "$hooks"/*.sh; do
  [ -e "$f" ] || continue
  name="${f#"$root"/}"
  cases=$((cases + 1))
  if ! out="$(bash -n "$f" 2>&1)"; then
    fail "$name does not parse (bash -n)" "$out"
  fi
  case "$f" in */common.sh) continue ;; esac
  cases=$((cases + 1))
  if [ ! -x "$f" ]; then
    fail "$name is not executable: Claude Code cannot run it" "chmod +x $name, and commit the mode (git update-index --chmod=+x $name)"
  fi
done

# --- the project's own names, discovered so the cases hold in a renamed copy as well ---
persistence=""
for d in src/Infrastructure/*.Persistence; do
  [ -d "$d" ] && persistence="$d" && break
done
slnx=""
for f in ./*.slnx; do
  [ -f "$f" ] && slnx="${f#./}" && break
done
if [ -z "$persistence" ] || [ -z "$slnx" ]; then
  echo "check-hooks: expected a src/Infrastructure/*.Persistence project and a .slnx at the root"
  exit 1
fi
persistence_csproj="$persistence/${persistence##*/}.csproj"
[ -f "$persistence_csproj" ] || { echo "check-hooks: $persistence_csproj does not exist"; exit 1; }

# --- the harness ---
# payload: the case's stdin, read from this function's own stdin (a here-document).
input="$work/input.json"
payload() { cat > "$input"; }

# expect LABEL HOOK WANT [TEXT]: runs .claude/hooks/HOOK on the payload with CLAUDE_PROJECT_DIR set to
# $project (the checkout unless a case changes it) and PATH to $path, and fails the case unless the
# hook exits 0 with the decision WANT and, when TEXT is given, prints TEXT on stdout or stderr.
project="$root"
path="$PATH"
expect() {
  local label="$1" hook="$2" want="$3" text="${4:-}" out err status got
  cases=$((cases + 1))
  out="$(cd "$root" && CLAUDE_PROJECT_DIR="$project" PATH="$path" "$hooks/$hook" < "$input" 2> "$work/stderr")"
  status=$?
  err="$(cat "$work/stderr")"
  case "$out" in
    '') got=silent ;;
    *'"permissionDecision":"deny"'*) got=deny ;;
    *'"permissionDecision":"ask"'*) got=ask ;;
    *'"decision":"block"'*) got=block ;;
    *'"additionalContext"'*) got=remind ;;
    *) got="unrecognised output" ;;
  esac
  if [ "$got" != silent ] && { [ "$(printf '%s\n' "$out" | wc -l | tr -d ' ')" != 1 ] || [[ "$out" != \{*\} ]]; }; then
    got="$got, but not one line of JSON"
  fi
  if [ "$status" -ne 0 ] || [ "$got" != "$want" ] || { [ -n "$text" ] && [[ "$out$err" != *"$text"* ]]; }; then
    fail "$hook: $label" "expected $want${text:+ mentioning \"$text\"}, got $got (exit $status)" "stdout: $out" "stderr: $err"
  fi
}

# JSON-escapes stdin for a payload (independent of the escaper the hooks use).
escape() { sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' | awk '{ printf "%s%s", (NR > 1 ? "\\n" : ""), $0 }'; }

edit() {  # edit FILE OLD NEW: an Edit payload (OLD and NEW already JSON-escaped)
  payload <<JSON
{"session_id":"s-edit","transcript_path":"$work/none.jsonl","cwd":"$root","permission_mode":"default","hook_event_name":"PreToolUse","tool_name":"Edit","tool_input":{"file_path":"$1","old_string":"$2","new_string":"$3","replace_all":false},"tool_use_id":"toolu_edit"}
JSON
}
write() {  # write FILE CONTENT: a Write payload (CONTENT already JSON-escaped)
  payload <<JSON
{"session_id":"s-write","transcript_path":"$work/none.jsonl","cwd":"$root","permission_mode":"default","hook_event_name":"PreToolUse","tool_name":"Write","tool_input":{"file_path":"$1","content":"$2"},"tool_use_id":"toolu_write"}
JSON
}

# --- guard-edits.sh: generated files, new projects and packages ask; everything else is silent ---
edit "$root/$persistence/Migrations/AppDbContextModelSnapshot.cs" 'a' 'b'
expect "an edit under the Persistence project's Migrations/" guard-edits.sh ask "migrations add"
edit "$root/src/Core/Acme.Application/Features/Migrations/Planted.cs" 'a' 'b'
expect "a Features/Migrations/ folder elsewhere" guard-edits.sh silent
edit "$root/$persistence/Features/Migrations/Planted.cs" 'a' 'b'
expect "a Migrations/ folder deeper in the Persistence project" guard-edits.sh silent
write "$root/docs/api/openapi.v1.json" '{}'
expect "a Write of the OpenAPI snapshot" guard-edits.sh ask "UPDATE_OPENAPI_SNAPSHOT=1"
write 'docs\\api\\openapi.v1.json' '{}'
expect "the snapshot under a relative path with backslashes" guard-edits.sh ask
edit "$root/src/Core/Acme.Domain/Invoices/Invoice.cs" 'a' 'b'
expect "an ordinary Domain file" guard-edits.sh silent
payload < /dev/null
expect "empty stdin" guard-edits.sh silent
printf 'not json' | payload
expect "stdin that is not JSON" guard-edits.sh silent
edit "$root/Directory.Packages.props" \
  '    <PackageVersion Include=\"Polly\" Version=\"8.5.0\" />' \
  '    <PackageVersion Include=\"Polly\" Version=\"8.5.0\" />\n    <PackageVersion Include=\"Bogus\" Version=\"35.6.1\" />'
expect "an edit that adds a PackageVersion" guard-edits.sh ask "LICENSE CEILINGS"
edit "$root/Directory.Packages.props" \
  '    <PackageVersion Include=\"Polly\" Version=\"8.5.0\" />' \
  '    <PackageVersion Include=\"Polly\" Version=\"8.6.0\" />'
expect "a version change alone" guard-edits.sh silent
edit "$root/$persistence_csproj" \
  '  <ItemGroup>' \
  '  <ItemGroup>\n    <PackageReference\n      Include=\"Bogus\" />'
expect "an edit that adds a PackageReference over two lines" guard-edits.sh ask
write "$root/src/Core/Acme.Billing/Acme.Billing.csproj" '<Project Sdk=\"Microsoft.NET.Sdk\" />'
expect "a Write that creates a .csproj" guard-edits.sh ask "ArchitectureRules.cs"
write "$root/$persistence_csproj" "$(escape < "$persistence_csproj")"
expect "a Write that keeps an existing .csproj's packages" guard-edits.sh silent
edit "$root/$slnx" \
  '  <Folder Name=\"/src/\">' \
  '  <Folder Name=\"/src/\">\n    <Project Path=\"src/Core/Acme.Billing/Acme.Billing.csproj\" />'
expect "an edit that adds a <Project> to the .slnx" guard-edits.sh ask
edit "$root/$slnx" \
  '  <Folder Name=\"/a/\">\n    <Project Path=\"src/X/X.csproj\" />\n  </Folder>\n  <Folder Name=\"/b/\">' \
  '  <Folder Name=\"/a/\">\n  </Folder>\n  <Folder Name=\"/b/\">\n    <Project Path=\"src/X/X.csproj\" />'
expect "a project moved between solution folders" guard-edits.sh silent
project="$work/missing"
edit "$root/$persistence/Migrations/AppDbContextModelSnapshot.cs" 'a' 'b'
expect "a project folder that does not exist" guard-edits.sh silent
project="$root"

# --- guard-commit.sh: subject style asks, a staged secret denies; gitleaks is always a stand-in ---
bin="$work/bin"
mkdir -p "$bin"
gitleaks_stub() {  # gitleaks_stub VERSION EXIT MESSAGE: what "gitleaks" does for the next cases
  cat > "$bin/gitleaks" <<SH
#!/bin/sh
if [ "\$1" = version ]; then echo "$1"; exit 0; fi
printf '%s\n' "\$*" > "$work/gitleaks.args"
echo "$3" >&2
exit $2
SH
  chmod +x "$bin/gitleaks"
}
path="$bin:$PATH"
gitleaks_stub 8.30.1 0 "no leaks found"

bash_call() {  # bash_call: a Bash payload whose command is this function's stdin, JSON-escaped
  local command
  command="$(escape)"
  payload <<JSON
{"session_id":"s-bash","transcript_path":"$work/none.jsonl","cwd":"$root","permission_mode":"default","hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"$command","description":"Commit"},"tool_use_id":"toolu_bash"}
JSON
}

printf '%s' 'git commit -m "feat(api): x"' | bash_call
expect 'git commit -m "feat(api): x"' guard-commit.sh silent
printf '%s' 'git add -A && git commit -m "WIP"' | bash_call
expect 'git commit -m "WIP"' guard-commit.sh ask 'Seen: \"WIP\"'
printf '%s' "git commit -am 'fix: y'" | bash_call
expect "git commit -am 'fix: y'" guard-commit.sh silent
printf '%s' 'git -C src commit --message="Fix the thing"' | bash_call
expect 'git -C <dir> commit --message="Fix the thing"' guard-commit.sh ask
bash_call <<'SH'
git commit -m "$(cat <<'EOF'
docs(adr): z

A body line.
EOF
)"
SH
expect "a here-document subject docs(adr): z" guard-commit.sh silent
bash_call <<'SH'
git add . && git commit -m "$(cat <<'EOF'
wip stuff
EOF
)" && git push
SH
expect "a here-document subject wip stuff" guard-commit.sh ask 'wip stuff'
printf '%s' 'git commit -F msg.txt' | bash_call
expect "git commit -F msg.txt" guard-commit.sh silent
printf '%s' 'git commit --amend --no-edit && grep -m 1 x y' | bash_call
expect "--amend without -m, and a later command's -m" guard-commit.sh silent
printf '%s' 'git commit -m "$MSG"' | bash_call
expect 'a subject only the shell can compute ("$MSG")' guard-commit.sh silent
printf '%s' 'git status' | bash_call
expect "git status" guard-commit.sh silent
printf '%s' 'git log --grep commit' | bash_call
expect "git log --grep commit" guard-commit.sh silent
payload < /dev/null
expect "empty stdin" guard-commit.sh silent

gitleaks_stub 8.30.1 1 "leaks found: 1"
printf '%s' 'git commit -m "feat: x"' | bash_call
expect "gitleaks finds a staged secret" guard-commit.sh deny "leaks found: 1"
cases=$((cases + 1))
case "$(cat "$work/gitleaks.args" 2>/dev/null)" in
  "git --pre-commit --staged --redact --no-banner --verbose --config .gitleaks.toml") ;;
  *) fail "guard-commit.sh: gitleaks 8.30 is run as 'gitleaks git --pre-commit --staged … --config .gitleaks.toml'" "ran: gitleaks $(cat "$work/gitleaks.args" 2>/dev/null)" ;;
esac
gitleaks_stub 8.18.2 1 "leaks found: 2"
printf '%s' 'git commit -m "feat: x"' | bash_call
expect "an older gitleaks finds a staged secret" guard-commit.sh deny "leaks found: 2"
cases=$((cases + 1))
case "$(cat "$work/gitleaks.args" 2>/dev/null)" in
  "protect --staged "*) ;;
  *) fail "guard-commit.sh: gitleaks before 8.19 is run as 'gitleaks protect --staged …'" "ran: gitleaks $(cat "$work/gitleaks.args" 2>/dev/null)" ;;
esac
gitleaks_stub 8.30.1 126 "cannot execute"
printf '%s' 'git commit -m "feat: x"' | bash_call
expect "gitleaks fails without a finding (exit 126)" guard-commit.sh silent "were not scanned"
path="$PATH"

# --- stop-tests.sh: a src/ change with no test run blocks once ---
# Transcript lines in the shape Claude Code writes: compact JSON, one message per line, a message's
# tool calls in its content array, a tool's output in a later user line.
user_line() {
  printf '{"parentUuid":null,"isSidechain":false,"type":"user","message":{"role":"user","content":"%s"},"uuid":"u-%s","sessionId":"s-stop"}\n' "$1" "$RANDOM"
}
call_line() {  # call_line '<tool_use JSON>…': an assistant message holding those tool calls
  local calls="" call
  for call in "$@"; do calls="$calls${calls:+,}$call"; done
  printf '{"parentUuid":"u","isSidechain":false,"message":{"model":"m","id":"msg_1","type":"message","role":"assistant","content":[%s]},"type":"assistant","uuid":"a-%s","sessionId":"s-stop"}\n' "$calls" "$RANDOM"
}
edit_call() { printf '{"type":"tool_use","id":"toolu_e%s","name":"Edit","input":{"replace_all":false,"file_path":"%s","old_string":"a","new_string":"b"}}' "$RANDOM" "$1"; }
write_call() { printf '{"type":"tool_use","id":"toolu_w%s","name":"Write","input":{"file_path":"%s","content":"namespace Acme;\\n"}}' "$RANDOM" "$1"; }
bash_tool() { printf '{"type":"tool_use","id":"toolu_b%s","name":"Bash","input":{"command":"%s","description":"Run it"}}' "$RANDOM" "$1"; }
text_block() { printf '{"type":"text","text":"%s"}' "$1"; }
result_line() {
  printf '{"parentUuid":"a","isSidechain":false,"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_x","type":"tool_result","content":"%s"}]},"uuid":"r-%s"}\n' "$1" "$RANDOM"
}
stop() {  # stop TRANSCRIPT ACTIVE: a Stop payload
  payload <<JSON
{"session_id":"s-stop","transcript_path":"$1","cwd":"$root","permission_mode":"default","hook_event_name":"Stop","stop_hook_active":$2}
JSON
}

t="$work/src-edit.jsonl"
{
  user_line 'Fix the rounding, then run ./scripts/dotnet.sh test when done.'
  call_line "$(text_block 'Next: ./scripts/dotnet.sh test, and \"dotnet test\" in the docs.')" \
    "$(edit_call "$root/docs/features/invoices.md")" "$(edit_call "$root/src/Core/Acme.Domain/Invoices/Invoice.cs")"
  result_line 'The file has been updated. Run \"dotnet test\" to check.'
} > "$t"
stop "$t" false
expect "a src/ edit and no test run (a test command only quoted in messages)" stop-tests.sh block "fails without the fix"
stop "$t" true
expect "the stop right after a block (stop_hook_active)" stop-tests.sh silent

t2="$work/src-edit-tested.jsonl"
{
  cat "$t"
  call_line "$(bash_tool "./scripts/dotnet.sh test tests/Acme.Domain.UnitTests --filter-class '*InvoiceTests'")"
  result_line 'Passed!'
} > "$t2"
stop "$t2" false
expect "a src/ edit followed by ./scripts/dotnet.sh test" stop-tests.sh silent

t3="$work/razor-and-test-edit.jsonl"
{
  call_line "$(write_call "$root/tools/Acme.AppHost/Program.cs")" "$(edit_call "$root/tests/Acme.Domain.UnitTests/InvoiceTests.cs")"
  call_line "$(bash_tool "cd $root \\u0026\\u0026 dotnet\\ttest tests/Acme.Domain.UnitTests")"
} > "$t3"
stop "$t3" false
expect "a tools/ Write, a tests/ edit and dotnet<tab>test" stop-tests.sh silent
head -n 1 "$t3" > "$work/tools-and-tests.jsonl"
stop "$work/tools-and-tests.jsonl" false
expect "a tools/ change and a tests/ change, no run (no red/green line)" stop-tests.sh block "no test ran"
cases=$((cases + 1))
if CLAUDE_PROJECT_DIR="$root" "$hooks/stop-tests.sh" < "$input" 2>/dev/null | grep -q 'fails without the fix'; then
  fail "stop-tests.sh: the red/green sentence appears although a tests/ file changed"
fi

t4="$work/docs-only.jsonl"
{
  call_line "$(edit_call "$root/docs/features/invoices.md")" "$(write_call "$root/README.md")"
  call_line "$(edit_call "/elsewhere/src/Other/Thing.cs")"
} > "$t4"
stop "$t4" false
expect "docs-only edits (and a src/ file outside the project)" stop-tests.sh silent
stop "$work/no-such-transcript.jsonl" false
expect "a transcript that does not exist" stop-tests.sh silent
payload < /dev/null
expect "empty stdin" stop-tests.sh silent

# --- docs-drift.sh: one reminder per session for a given report ---
if command -v git >/dev/null 2>&1; then
  repo="$work/drift"
  mkdir -p "$repo/scripts" "$repo/src/Core/Acme.Application/Features/Widgets" "$repo/docs/features"
  git init -q --template= "$repo"
  cp "$root/scripts/check-docs-drift.sh" "$repo/scripts/"
  printf 'namespace Acme;\n' > "$repo/src/Core/Acme.Application/Features/Widgets/Widget.cs"
  posted() {  # posted SESSION: a PostToolUse payload for a Write in that session
    payload <<JSON
{"session_id":"$1","transcript_path":"$work/none.jsonl","cwd":"$repo","permission_mode":"default","hook_event_name":"PostToolUse","tool_name":"Write","tool_input":{"file_path":"$repo/src/Core/Acme.Application/Features/Widgets/Widget.cs","content":"x"},"tool_response":{"success":true},"tool_use_id":"toolu_drift"}
JSON
  }
  project="$repo"
  posted session-1
  expect "a drift report, first time in a session" docs-drift.sh remind "docs/features/widgets.md"
  expect "the same report again in that session" docs-drift.sh silent
  posted session-2
  expect "the same report in another session" docs-drift.sh remind
  printf '# Widgets\n' > "$repo/docs/features/widgets.md"
  expect "no drift" docs-drift.sh silent
  cases=$((cases + 1))
  if [ -e "$repo/.git/claude-hooks/docs-drift.last" ]; then
    fail "docs-drift.sh: a clean run leaves its record behind (.git/claude-hooks/docs-drift.last)"
  fi
  rm "$repo/docs/features/widgets.md"
  expect "the report again after a clean run, same session" docs-drift.sh remind
  project="$work/missing"
  expect "a project folder that does not exist" docs-drift.sh silent
  project="$root"
else
  echo "check-hooks: git is not on PATH; the docs-drift cases were skipped."
fi

if [ "$failed" -ne 0 ]; then
  echo "hook self-test failed: $failed of $cases cases."
  exit 1
fi
echo "hook self-test passed: $cases cases"
