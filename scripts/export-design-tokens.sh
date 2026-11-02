#!/bin/sh
# Writes the design tokens as JSON for a design tool or a published design-system page to import
# (docs/design-system.md, "Mocks and the export"): one object per custom property declared under
# :root in the UI's tokens.css, in the file's order, with its value and its usage:
#   [
#     { "name": "--ink", "value": { "light": "#14231b", "dark": "#e6efe8" }, "usage": "body text" },
#     { "name": "--space-4", "value": "1rem", "usage": "16 px" }
#   ]
# written 2-space indented, one key to a line. A colour, light-dark(#rrggbb, #rrggbb), becomes its two
# values in lower case; any other value is the string as written, its whitespace collapsed. A value may
# not hold a ";", "{" or "}" inside quotes or parentheses (a data URL, say): the script stops with exit 2
# and names the token rather than write it cut short. The usage is the Role cell of the token's row in
# the doc's Tokens tables, "" for a token without one. The same inputs give byte-identical output, on
# Linux and macOS alike. DesignSystemTests (ArchitectureTests) runs it and holds every name and value to
# tokens.css and every usage to the doc.
#
# Usage:
#   export-design-tokens.sh [-o <file>]   src/Presentation/UI/*.UI.Common/wwwroot/tokens.css and
#                                         docs/design-system.md under the repository root, written
#                                         to stdout or to <file>
#   export-design-tokens.sh --tokens <file> --doc <file> [-o <file>]
#                                         other inputs (the test's planted pair)
# A project generated without the UI has no tokens.css: the script says so on stderr and exits 0.
# POSIX sh and awk only (no jq, no node), so it runs wherever the other scripts do.
set -eu

me="export-design-tokens"
fail() {
  printf '%s: %s\n' "$me" "$*" >&2
  exit 2
}

tokens="" doc="" out=""
while [ $# -gt 0 ]; do
  case "$1" in
    --tokens | --doc | -o)
      [ $# -ge 2 ] || fail "$1 needs a file"
      case "$1" in
        --tokens) tokens="$2" ;;
        --doc) doc="$2" ;;
        *) out="$2" ;;
      esac
      shift 2 ;;
    -h | --help)
      sed -n '2,/^set -eu$/p' "$0" | sed -e '$d' -e 's/^# \{0,1\}//'
      exit 0 ;;
    *)
      fail "unknown argument '$1' (--help lists the options)" ;;
  esac
done

# The repository root is the folder above this script; run from anywhere else (a copy, a link), the
# git checkout around the current directory.
root="$(CDPATH='' cd "$(dirname "$0")/.." && pwd)"
if [ ! -f "$root/scripts/$me.sh" ]; then
  root="$(git rev-parse --show-toplevel 2>/dev/null || pwd)"
fi

if [ -z "$tokens" ]; then
  count=0
  for candidate in "$root"/src/Presentation/UI/*.UI.Common/wwwroot/tokens.css; do
    [ -f "$candidate" ] || continue   # a glob that matches nothing stays as written
    tokens="$candidate"
    count=$((count + 1))
  done
  if [ "$count" -eq 0 ]; then
    printf '%s: this project has no UI, nothing to export\n' "$me" >&2
    exit 0
  fi
  [ "$count" -eq 1 ] || fail "$count files match src/Presentation/UI/*.UI.Common/wwwroot/tokens.css; name one with --tokens"
fi
{ [ -f "$tokens" ] && [ -r "$tokens" ]; } || fail "cannot read the token file $tokens"

if [ -z "$doc" ]; then
  doc="$root/docs/design-system.md"
  if [ ! -f "$doc" ]; then
    printf '%s: no docs/design-system.md, so every usage is empty\n' "$me" >&2
    doc=""
  fi
fi
[ -z "$doc" ] || { [ -f "$doc" ] && [ -r "$doc" ]; } || fail "cannot read the doc $doc"

# One awk program, all of it in BEGIN, reading both files by name from the environment (an operand
# with "=" in it would be an assignment, and -v would expand its backslashes). POSIX awk only: the
# escapes a gsub replacement takes differ between awks, so JSON strings are escaped a character at a
# time; the hex pattern is spelled out rather than written with an interval; and a split separator is
# a bracket expression, "[;]" and not ";", because the awk macOS ships also splits on every newline
# when the separator is one plain character, which would cut a value written over two lines.
export_tokens() {
  EXPORT_TOKENS_FILE="$tokens" EXPORT_DOC_FILE="$doc" awk '
    function trim(s) {
      sub(/^[ \t\r\n]+/, "", s)
      sub(/[ \t\r\n]+$/, "", s)
      return s
    }

    function collapse(s) {
      s = trim(s)
      gsub(/[ \t\r\n]+/, " ", s)
      return s
    }

    function str(s,    out, i, c, n) {
      out = ""
      n = length(s)
      for (i = 1; i <= n; i++) {
        c = substr(s, i, 1)
        if (c == "\\" || c == "\"") out = out "\\" c
        else if (c == "\t") out = out "\\t"
        else out = out c
      }
      return "\"" out "\""
    }

    # The Role cell of each token row in the section "## Tokens", up to the next level-two heading:
    # the row whose first cell is `--name`, the column headed Role, the first row of a name winning.
    function read_roles(file,    line, t, row, cells, n, c, column, inside, name) {
      if (file == "") return
      inside = 0
      column = 2
      while ((getline line < file) > 0) {
        sub(/\r$/, "", line)
        t = trim(line)
        if (!inside) {
          if (t == "## Tokens") inside = 1
          continue
        }
        if (line ~ /^##[ \t]/) break
        if (substr(t, 1, 1) != "|") continue
        row = t
        sub(/^[|]/, "", row)
        sub(/[|]$/, "", row)
        n = split(row, cells, "[|]")
        for (c = 1; c <= n; c++) cells[c] = trim(cells[c])
        if (cells[1] ~ /^`--[a-z][a-z0-9-]*`$/) {
          name = substr(cells[1], 4, length(cells[1]) - 4)
          if (!(name in role)) role[name] = (column <= n) ? cells[column] : ""
        } else {
          for (c = 1; c <= n; c++) if (cells[c] == "Role") { column = c; break }
        }
      }
      close(file)
    }

    # Stops the export: exit 2, the reason on stderr, nothing on stdout.
    function stop(what) {
      printf "export-design-tokens: %s: a value may not hold \";\", \"{\" or \"}\" inside quotes or parentheses\n", what | "cat 1>&2"
      close("cat 1>&2")
      exit 2
    }

    # 1 when s closes every quote and parenthesis it opens; a backslash escapes the character after it.
    function closed(s,    i, n, c, quote, depth) {
      quote = ""
      depth = 0
      n = length(s)
      for (i = 1; i <= n; i++) {
        c = substr(s, i, 1)
        if (c == "\\") i++
        else if (quote != "") { if (c == quote) quote = "" }
        else if (c == "\"" || c == sq) quote = c
        else if (c == "(") depth++
        else if (c == ")") { if (--depth < 0) return 0 }
      }
      return quote == "" && depth == 0
    }

    # Every "--name: value;" in a :root body, in order; the text after the last ";" is not one. A ";"
    # or a "}" inside quotes or parentheses has split the body there, so a part that leaves one open,
    # the last part included, stops the export rather than lose the rest of the value or the block.
    function declarations(body,    parts, n, p, seg, name, value, i, rest) {
      n = split(body, parts, "[;]")
      for (p = 1; p <= n; p++) {
        seg = trim(parts[p])
        if (!closed(seg)) stop(match(seg, /^--[A-Za-z_][A-Za-z0-9_-]*/) ? substr(seg, 1, RLENGTH) : ":root")
        if (p == n || !match(seg, /^--[A-Za-z_][A-Za-z0-9_-]*[ \t\r\n]*:/)) continue
        name = substr(seg, 3, RLENGTH - 3)
        sub(/[ \t\r\n]+$/, "", name)
        value = collapse(substr(seg, RLENGTH + 1))
        count++
        names[count] = name
        values[count] = value
        if (value ~ pair) {
          i = index(value, "#")
          light[count] = tolower(substr(value, i, 7))
          rest = substr(value, i + 7)
          dark[count] = tolower(substr(rest, index(rest, "#"), 7))
        }
      }
    }

    # The top-level blocks of the stylesheet, comments out: a block headed :root with no block inside
    # it holds the tokens; the [data-theme] blocks and anything nested are not tokens.
    function read_tokens(file,    line, css, code, i, j, k, n, c, depth, prelude, body) {
      css = ""
      while ((getline line < file) > 0) {
        sub(/\r$/, "", line)
        css = css line "\n"
      }
      close(file)
      code = ""
      while ((i = index(css, "/*")) > 0) {
        code = code substr(css, 1, i - 1) " "
        css = substr(css, i + 2)
        j = index(css, "*/")
        css = (j == 0) ? "" : substr(css, j + 2)
      }
      code = code css
      depth = 0
      prelude = ""
      body = ""
      n = length(code)
      for (k = 1; k <= n; k++) {
        c = substr(code, k, 1)
        if (c == "{") {
          if (depth > 0) body = body c
          depth++
        } else if (c == "}") {
          if (depth > 0) depth--
          if (depth > 0) {
            body = body c
          } else {
            if (trim(prelude) == ":root" && index(body, "{") == 0) declarations(body)
            prelude = ""
            body = ""
          }
        } else if (depth == 0) {
          prelude = prelude c
        } else {
          body = body c
        }
      }
      if (depth != 0) stop(file ": its braces do not balance")
    }

    BEGIN {
      sq = "\047"
      hex = "#[0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f][0-9A-Fa-f]"
      pair = "^light-dark[(] ?" hex " ?, ?" hex " ?[)]$"
      count = 0
      read_roles(ENVIRON["EXPORT_DOC_FILE"])
      read_tokens(ENVIRON["EXPORT_TOKENS_FILE"])

      printf "["
      for (t = 1; t <= count; t++) {
        if (t > 1) printf ","
        printf "\n  {\n    \"name\": %s,\n", str("--" names[t])
        if (t in light) {
          printf "    \"value\": {\n      \"light\": %s,\n      \"dark\": %s\n    },\n", str(light[t]), str(dark[t])
        } else {
          printf "    \"value\": %s,\n", str(values[t])
        }
        printf "    \"usage\": %s\n  }", str((names[t] in role) ? role[names[t]] : "")
      }
      if (count > 0) printf "\n"
      printf "]\n"
    }
  '
}

if [ -n "$out" ]; then
  export_tokens > "$out" || { status=$?; rm -f "$out"; exit "$status"; }
  printf 'wrote %s\n' "$out"
else
  export_tokens
fi
