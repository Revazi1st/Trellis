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
# REASON to the agent.
decide() {
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"%s","permissionDecisionReason":"%s"}}\n' \
    "$1" "$(json_escape "$2")"
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

# project_root: sets PROJECT_DIR to CLAUDE_PROJECT_DIR (the working directory when it is unset), with
# slashes for backslashes and no trailing slash, and PROJECT_DIR_REAL to the same folder with symbolic
# links resolved. Fails when the folder does not exist.
project_root() {
  PROJECT_DIR="${CLAUDE_PROJECT_DIR:-$PWD}"
  PROJECT_DIR="${PROJECT_DIR//\\//}"
  [ "$PROJECT_DIR" = / ] || PROJECT_DIR="${PROJECT_DIR%/}"
  PROJECT_DIR_REAL=""
  [ -n "$PROJECT_DIR" ] && [ -d "$PROJECT_DIR" ] || return 1
  PROJECT_DIR_REAL="$(cd "$PROJECT_DIR" 2>/dev/null && pwd -P)" || PROJECT_DIR_REAL="$PROJECT_DIR"
}

# relative_path PATH: prints PATH with slashes for backslashes, relative to the project folder when it
# lies inside it (under either spelling project_root found), and as given otherwise (still absolute).
relative_path() {
  local p="${1//\\//}" d
  for d in "${PROJECT_DIR:-}" "${PROJECT_DIR_REAL:-}"; do
    [ -n "$d" ] || continue
    case "$p" in
      "$d"/*) printf '%s\n' "${p#"$d"/}"; return 0 ;;
    esac
  done
  case "$p" in
    ./*) p="${p#./}" ;;
  esac
  printf '%s\n' "$p"
}
