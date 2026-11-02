# Helpers the hooks in this folder source; not a hook of its own, so it has no executable bit, no
# entry in .claude/settings.json and no line in docs/INDEX.md.
#
# A hook fails open: when its input is missing or unreadable, or a folder or tool it needs is not
# there, it exits 0 and prints nothing, which Claude Code reads as "no opinion". The helpers need
# POSIX awk, sed, tr and grep only (jq is never required), and run awk under LC_ALL=C so a byte is a
# character on every platform. scripts/check-hooks.sh is the self-test of every hook that uses them.

# JSON_AWK: an awk function the helpers below and the hooks share.
#   json_value(s)  s is the text right after a JSON string's opening quote. Returns the string's value
#                  decoded (\" \\ \/ \n \t \r; \uXXXX becomes "?"; \b and \f are dropped) and sets
#                  JSON_OK to 1, or to 0 when the string never closes.
JSON_AWK='
function json_value(s,    out, c, e) {
  out = ""
  JSON_OK = 0
  while (match(s, /[\\"]/)) {
    out = out substr(s, 1, RSTART - 1)
    c = substr(s, RSTART, 1)
    if (c == "\"") { JSON_OK = 1; return out }
    e = substr(s, RSTART + 1, 1)
    if (e == "u") { out = out "?"; s = substr(s, RSTART + 6); continue }
    if (e == "n") out = out "\n"
    else if (e == "t") out = out "\t"
    else if (e == "r") out = out "\r"
    else if (e != "b" && e != "f") out = out e
    s = substr(s, RSTART + 2)
  }
  return out
}
'

# read_input: reads the hook's JSON payload from stdin, once, into INPUT. Fails (INPUT empty) when
# stdin is a terminal, closed or empty.
read_input() {
  INPUT=""
  [ -t 0 ] && return 1
  INPUT="$(cat 2>/dev/null)" || INPUT=""
  [ -n "$INPUT" ]
}

# json_string KEY: prints the decoded value of the first "KEY":"…" in INPUT (whitespace allowed
# around the colon), or nothing when INPUT has no such string. A key inside another string's value
# is escaped there (\"KEY\") and never matches.
json_string() {
  printf '%s' "$INPUT" | LC_ALL=C awk -v key="$1" "$JSON_AWK"'
    BEGIN { RS = "\001" }
    {
      if (!match($0, "\"" key "\"[[:space:]]*:[[:space:]]*\"")) exit
      v = json_value(substr($0, RSTART + RLENGTH))
      if (JSON_OK) printf "%s", v
      exit
    }'
}

# json_bool KEY: prints true or false for the first "KEY": true|false in INPUT, nothing otherwise.
json_bool() {
  printf '%s' "$INPUT" | LC_ALL=C awk -v key="$1" '
    BEGIN { RS = "\001" }
    {
      if (!match($0, "\"" key "\"[[:space:]]*:[[:space:]]*(true|false)")) exit
      v = substr($0, RSTART, RLENGTH)
      sub(/.*[^a-z]/, "", v)
      print v
      exit
    }'
}

# json_escape TEXT: prints TEXT as the inside of a JSON string: backslash and quote escaped, a line
# break as \n, a tab as \t, a carriage return as \r, every other control character dropped.
json_escape() {
  printf '%s' "$1" | LC_ALL=C tr -d '\000-\010\013\014\016-\037' | LC_ALL=C awk '
    BEGIN { ORS = "" }
    {
      if (NR > 1) print "\\n"
      line = $0
      while (match(line, /[\\"\t\r]/)) {
        c = substr(line, RSTART, 1)
        print substr(line, 1, RSTART - 1)
        if (c == "\\") print "\\\\"
        else if (c == "\"") print "\\\""
        else if (c == "\t") print "\\t"
        else print "\\r"
        line = substr(line, RSTART + 1)
      }
      print line
    }'
}

# decide deny|ask REASON: prints the PreToolUse decision Claude Code reads from stdout and exits 0.
# "ask" shows REASON to the person, who allows or refuses the tool call; "deny" refuses it and shows
# REASON to the agent. A WARNING the hook set on the way rides along as the object's systemMessage
# (shown to the person), since stdout must hold that one object and nothing else.
decide() {
  local warning=""
  [ -z "${WARNING:-}" ] || warning="$(printf ',"systemMessage":"%s"' "$(json_escape "$WARNING")")"
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"%s","permissionDecisionReason":"%s"}%s}\n' \
    "$1" "$(json_escape "$2")" "$warning"
  exit 0
}

# warn_and_exit: prints a WARNING the hook set as {"systemMessage":"…"}, the one way a hook that exits
# 0 reaches the person (its stderr is never shown), and exits 0. Silent when there is none.
warn_and_exit() {
  [ -z "${WARNING:-}" ] || printf '{"systemMessage":"%s"}\n' "$(json_escape "$WARNING")"
  exit 0
}

# count_in REGEX TEXT: prints how many times the extended regular expression REGEX matches in TEXT,
# line breaks included (an element split over two lines still counts once).
count_in() {
  printf '%s' "$2" | PATTERN="$1" LC_ALL=C awk '
    BEGIN { RS = "\001"; n = 0 }
    {
      s = $0
      while (match(s, ENVIRON["PATTERN"])) {
        n++
        s = substr(s, RSTART + (RLENGTH > 0 ? RLENGTH : 1))
      }
    }
    END { print n }'
}

# normal_path PATH: prints PATH with slashes for backslashes, a run of slashes as one, "." segments
# dropped and each "name/.." pair resolved, from the text alone (the folders need not exist). A ".."
# with nothing left to climb out of is kept, so the path stays outside the folder it climbed above.
# Claude Code resolves "<project>/src/../docs/x" and "<project>//docs/x" to "<project>/docs/x", and so
# must a rule that names that file. Prints nothing for an empty PATH.
normal_path() {
  [ -n "$1" ] || return 0
  local rest="${1//\\//}/" lead="" out="" seg
  case "$rest" in /*) lead=/ ;; esac
  while [ -n "$rest" ]; do
    seg="${rest%%/*}"
    rest="${rest#*/}"
    case "$seg" in
      '' | .) ;;
      ..)
        case "$out" in
          '' | .. | */..) out="${out:+$out/}.." ;;
          */*) out="${out%/*}" ;;
          *) out="" ;;
        esac ;;
      *) out="${out:+$out/}$seg" ;;
    esac
  done
  printf '%s\n' "$lead$out"
}

# project_root: sets PROJECT_DIR to CLAUDE_PROJECT_DIR (the working directory when it is unset),
# through normal_path, and PROJECT_DIR_REAL to the same folder with symbolic links resolved. Fails when
# the folder does not exist.
project_root() {
  PROJECT_DIR="$(normal_path "${CLAUDE_PROJECT_DIR:-$PWD}")"
  PROJECT_DIR_REAL=""
  [ -n "$PROJECT_DIR" ] && [ -d "$PROJECT_DIR" ] || return 1
  PROJECT_DIR_REAL="$(cd "$PROJECT_DIR" 2>/dev/null && pwd -P)" || PROJECT_DIR_REAL="$PROJECT_DIR"
}

# work_root: sets WORK_DIR to the top folder of the git checkout that holds the input's cwd, spelled
# as cwd spells it, and WORK_DIR_REAL to it with symbolic links resolved. That is the checkout Claude
# works in: cwd follows Claude into a worktree (and through a cd), while CLAUDE_PROJECT_DIR stays at the
# folder the session started in. Both are empty when cwd is missing or outside a checkout, or git is
# not installed.
work_root() {
  local cwd cdup
  WORK_DIR=""
  WORK_DIR_REAL=""
  cwd="$(normal_path "$(json_string cwd)")"
  [ -n "$cwd" ] && [ -d "$cwd" ] || return 0
  cdup="$(git -C "$cwd" rev-parse --show-cdup 2>/dev/null)" || return 0
  while [ -n "$cdup" ]; do
    case "$cdup" in
      ../*) cwd="${cwd%/*}"; cdup="${cdup#../}" ;;
      *) return 0 ;;
    esac
  done
  [ -n "$cwd" ] && [ -d "$cwd" ] || return 0
  WORK_DIR="$cwd"
  WORK_DIR_REAL="$(cd "$cwd" 2>/dev/null && pwd -P)" || WORK_DIR_REAL="$cwd"
}

# relative_path PATH: sets REL to PATH (through normal_path) relative to the deepest of WORK_DIR and
# PROJECT_DIR, under either spelling, that holds it, and REL_ROOT to that folder. When none holds it,
# REL is PATH as normalised (still absolute, or relative as given) and REL_ROOT is empty.
relative_path() {
  local p d
  p="$(normal_path "$1")"
  REL="$p"
  REL_ROOT=""
  for d in "${WORK_DIR:-}" "${WORK_DIR_REAL:-}" "${PROJECT_DIR:-}" "${PROJECT_DIR_REAL:-}"; do
    [ -n "$d" ] || continue
    case "$p" in
      "$d"/*)
        if [ "${#d}" -gt "${#REL_ROOT}" ]; then
          REL_ROOT="$d"
          REL="${p#"$d"/}"
        fi ;;
    esac
  done
}
