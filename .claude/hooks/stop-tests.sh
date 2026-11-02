#!/usr/bin/env bash
# Stop: when the agent is about to finish, reads the session's transcript and blocks the stop once if
# the session changed a .cs or .razor file under src/ or tools/ (an Edit, Write or MultiEdit) and no
# Bash command ran the tests (dotnet test, or ./scripts/dotnet.sh test). The reason asks for the run,
# narrowed to the project touched, or a one-line reason why none is needed, and adds the red/green
# rule when nothing under tests/ changed either (AGENTS.md, Always). Claude Code sets
# stop_hook_active on the stop that follows a block, and the hook lets that one through, so it never
# blocks twice in a row. A missing or unreadable transcript: silent. scripts/check-hooks.sh has a case
# for every decision.
#
# The transcript is JSON Lines; a tool call is {"type":"tool_use",…,"name":"Edit","input":{…,
# "file_path":"…",…}} and a Bash call carries "command":"…" (several calls may share a line). Only those
# keys of those tools count: a test command quoted in a message or a tool's output is not a run.
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd)" || exit 0
# shellcheck source=common.sh
. "$here/common.sh" 2>/dev/null || exit 0

read_input || exit 0
[ "$(json_bool stop_hook_active)" = true ] && exit 0
transcript="$(json_string transcript_path)"
case "$transcript" in
  "~/"*) transcript="$HOME/${transcript#"~/"}" ;;
esac
[ -n "$transcript" ] && [ -f "$transcript" ] && [ -r "$transcript" ] || exit 0
project_root || exit 0

# Prints "<code changed> <tests changed> <tests ran>", each 0 or 1.
read -r -d '' scan <<'AWK'
BEGIN { root = ENVIRON["HOOK_ROOT"]; real = ENVIRON["HOOK_ROOT_REAL"]; code = 0; tests = 0; ran = 0 }
# The path relative to the project, or "" when it lies outside.
function inside(p) {
  gsub(/\\/, "/", p)
  if (root != "" && index(p, root "/") == 1) return substr(p, length(root) + 2)
  if (real != "" && index(p, real "/") == 1) return substr(p, length(real) + 2)
  return ""
}
{
  line = $0
  while (match(line, /"name"[[:space:]]*:[[:space:]]*"(Edit|Write|MultiEdit|Bash)"/)) {
    tool = substr(line, RSTART, RLENGTH)
    line = substr(line, RSTART + RLENGTH)
    if (tool ~ /"Bash"$/) {
      if (match(line, /"command"[[:space:]]*:[[:space:]]*"/)) {
        command = json_value(substr(line, RSTART + RLENGTH))
        if (command ~ /dotnet(\.sh)?[[:space:]]+test/) ran = 1
      }
    } else if (match(line, /"file_path"[[:space:]]*:[[:space:]]*"/)) {
      path = inside(json_value(substr(line, RSTART + RLENGTH)))
      if (path == "") continue
      if (("/" path) ~ /\/(src|tools)\/.*\.(cs|razor)$/) code = 1
      if (("/" path) ~ /\/tests\//) tests = 1
    }
  }
}
END { print code, tests, ran }
AWK
result="$(grep -E '"name"[[:space:]]*:[[:space:]]*"(Edit|Write|MultiEdit|Bash)"' "$transcript" 2>/dev/null \
  | HOOK_ROOT="$PROJECT_DIR" HOOK_ROOT_REAL="$PROJECT_DIR_REAL" LC_ALL=C awk "$JSON_AWK$scan" 2>/dev/null)" || exit 0
read -r code tests ran <<< "$result"
[ "${code:-0}" = 1 ] && [ "${ran:-1}" = 0 ] || exit 0

reason="Code under src/ or tools/ changed in this session and no test ran. Run ./scripts/dotnet.sh test <the project you touched> (narrow with --filter-class; never filter the .slnx) and read the result, or say in one line why no run is needed."
if [ "${tests:-0}" = 0 ]; then
  reason="$reason Nothing under tests/ changed either: a bug fix starts with a test that fails without the fix (red) and passes with it (green)."
fi
printf '{"decision":"block","reason":"%s"}\n' "$(json_escape "$reason")"
exit 0
