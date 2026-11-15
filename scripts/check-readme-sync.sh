#!/usr/bin/env bash
# Fails when a copy of a fact in the README, the package README or the documentation site no longer
# agrees with the fact's one home (ADR-0031, amendment of 2026-11-15). Maintainer-only: template.json
# and the pack project exclude it, and ci.yml runs it in its template-source block. Every message names
# the home and the copy, and says which side to change. The comparisons:
#   C1a  eng/readme/pitch.md is one line with exactly one *...* span (the short form) and none of the
#        characters JSON, XML or Markdown would change: ` & < > " \ |
#   C1b  README.md contains the pitch (asterisks and backticks dropped, whitespace folded).
#   C1c  the package README's first paragraph after its H1 is the pitch.
#   C1d  <Description> in the pack csproj is the pitch.
#   C1e  the top-level description in template.json is the pitch.
#   C1f  the site derives it: site/trellis.config.mjs names eng/readme/pitch.md, site/astro.config.mjs
#        has the line "description: pitch,", and the frontmatter of the landing page has no description.
#   C1g  the landing page's hero title is the short form (tags, quotes and a final full stop dropped).
#   C1h  the landing page's head title ends with the short form.
#   C1i  render-diagrams.py reads pitch.md, and the alt text of the README's title picture holds the
#        short form.
#   C2   the options table of README.md and of the package README: one row per boolean parameter of
#        template.json, in its order with skipRestore last: the flag linked to its section of the
#        site's Options page, on or off as its default, and its description word for word.
#   C2b  each boolean parameter's displayName is its name.text in ide.host.json.
#   C2c  every `--flag` code span of README.md's prose is the flag of a boolean parameter.
#   C3   every "dotnet new" and "git clone" line of a fenced block in the two READMEs is a quickstart
#        string of site/src/data/repo.mjs (between "// readme-sync:quickstart" and
#        "// readme-sync:end") or the banner's create line (its placeholder read from
#        render-diagrams.py); README.md shows install, both update lines, uninstall, create and the
#        first two clone lines, and the package README shows install.
#   C4   the use-case tables of README.md hold exactly the operations of docs/api/openapi.v1.json, each
#        row opened by the **Name** of a use-case folder under src/Core/Trellis.Application/Features/,
#        and every `METHOD /api/vN/...` code span of its prose is one of those operations.
#   C4b  their Permission column is EndpointMetadataTests.ExpectedPermissions, resolved through
#        Permissions.cs, in both directions.
#   C5   every file in login_copies below holds the seeded e-mail of SeedOptions.cs and, unless it is
#        marked e-mail only, its password; no other file under README.md, AGENTS.md, docs/, .claude/,
#        .template.config/ and .github/workflows/ holds the e-mail.
#   C6a  the coverage floors under "### Gates beyond the tests" in docs/development.md are ci.yml's.
#   C6b  the Aspire CLI minimum in docs/development.md's prerequisites table is doctor.sh's aspire_floor
#        and every "Aspire CLI <n> or later" under docs/ (not docs/adr/) and .claude/.
#   C7   a link from the two READMEs to the site (<PackageProjectUrl>, which is site + base of
#        site/trellis.config.mjs) names a page site/src/content/docs has, with an anchor only on the
#        Options page and only an option's flag; a <RepositoryUrl>/blob/main/ or /tree/main/ link names
#        a path that exists, and a <RepositoryUrl>/actions/workflows/<file> link names a workflow.
#   C8   every picture README.md names exists, and every eng/readme/shots/*.png is used by README.md or
#        a file under site/src; the alt text of each screenshot README.md shows, and the caption under
#        them, are on the landing page too.
#   C9   every guide directly under docs/ is listed in docs/INDEX.md.
#   C10  every #anchor of a repository link in the two READMEs is a heading (GitHub's slug) or an <a>
#        name or id of its file, and every ## and ### heading of README.md below the table of contents
#        has its entry there, in order.
#   C11  the tools of docs/development.md's prerequisites table are the bullets of the README's "Before
#        you start" and the first cells of the Install page's prerequisites table.
#   C12  every port README.md names (localhost:<n>, host port <n>) is in an applicationUrl of a
#        launchSettings.json or is a default port of docker-compose.yml.
#   C13  "Built for AI agents" in README.md links every shipped skill and reviewer, and its bullet that
#        links .mcp.json names every MCP server of .mcp.json before the words "MCP server".
#   C14  the number word of the site's walkthrough (index.mdx, start/install.mdx) is the row count of the
#        Try it table in docs/features/invoices.md.
#   C15  "With an option" in docs/overview.md has a bullet for every option but --skip-restore, each
#        opening with its flag and alone in an #if block that names its symbol, and no other bullet.
# Only these copies are compared; a fact stated anywhere else has no check behind it.
# A source that yields nothing (no boolean parameter, no operation, no marker) is a failure of its own.
#
# Usage:
#   check-readme-sync.sh              run every comparison; exit 0 when all agree, 1 otherwise
#   check-readme-sync.sh --self-test  plant a disagreement for each comparison in a throwaway tree
# Files only: no git, no jq, no node; awk, sed, grep, tr and find, the same under macOS awk, mawk and
# gawk, and bash 3.2.
set -euo pipefail
# Bytes, not the locale's collation: macOS awk would otherwise read a multi-byte heading differently
# from CI's.
export LC_ALL=C
unset GREP_OPTIONS   # BSD grep honours it: --color=always would put escapes into the values cut out
cd "$(dirname "${BASH_SOURCE[0]}")/.."

usage() { echo "usage: scripts/check-readme-sync.sh [--self-test]" >&2; exit 2; }

fail=0
tab=$'\t'
# Records whose fields may be empty use the unit separator: tab is whitespace to read, which folds two
# tabs into one and moves every later field one to the left.
us=$'\037'

# report FILE LINE TEXT
report() {
  local file="$1" line="${2:-1}" text="$3"
  [ -n "$line" ] || line=1
  if [ "${GITHUB_ACTIONS:-}" = "true" ]; then
    printf '::error file=%s,line=%s::%s\n' "$file" "$line" "$text"
  else
    printf '%s:%s: %s\n' "$file" "$line" "$text"
  fi
  fail=1
}

# need FILE ID: true when FILE exists, a failure of comparison ID otherwise.
need() {
  [ -f "$1" ] && return 0
  report "$1" 1 "$2: $1 is missing; teach scripts/check-readme-sync.sh where it went"
  return 1
}

# plain: asterisks and backticks dropped, every run of whitespace one space, trimmed.
plain() { tr -d '*`' | tr -s '[:space:]' ' ' | sed -e 's/^ //' -e 's/ $//'; }

# line_of FILE TEXT: the number of the first line holding TEXT, or nothing.
line_of() { S="$2" awk 'index($0, ENVIRON["S"]) { print NR; exit }' "$1"; }

# in_list ITEM LIST: true when ITEM is a whole line of LIST.
in_list() { grep -qxF -- "$1" <<< "$2"; }

# frontmatter_line FILE REGEX: the first line of FILE's frontmatter matching REGEX, as "NR<TAB>line".
frontmatter_line() {
  RE="$2" awk '/^---[[:space:]]*$/ { c++; if (c == 2) exit; next } c == 1 && $0 ~ ENVIRON["RE"] { print NR "\t" $0; exit }' "$1"
}

# --- the files ------------------------------------------------------------------------------------
pitch_file=eng/readme/pitch.md
readme=README.md
pack_readme=eng/Trellis.TemplatePack/README.md
pack_csproj=eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj
template=.template.config/template.json
cli_host=.template.config/dotnetcli.host.json
ide_host=.template.config/ide.host.json
site_config=site/trellis.config.mjs
astro_config=site/astro.config.mjs
site_pages=site/src/content/docs
landing=$site_pages/index.mdx
install_page=$site_pages/start/install.mdx
repo_data=site/src/data/repo.mjs
renderer=eng/readme/render-diagrams.py
shots=eng/readme/shots
openapi=docs/api/openapi.v1.json
features_dir=src/Core/Trellis.Application/Features
endpoint_tests=tests/Trellis.ApiTests/InProcess/EndpointMetadataTests.cs
permissions=src/Core/Trellis.Application/Authorization/Permissions.cs
seed_options=tools/Trellis.MigrationService/SeedOptions.cs
ci=.github/workflows/ci.yml
development=docs/development.md
doctor=scripts/doctor.sh
index=docs/INDEX.md
invoices_doc=docs/features/invoices.md
overview=docs/overview.md
compose=docker-compose.yml
launch_settings='tools/Trellis.AppHost/Properties/launchSettings.json
src/Presentation/Api/Trellis.Api/Properties/launchSettings.json
src/Presentation/UI/Trellis.UI.Web/Properties/launchSettings.json'

# A permission as the README's Permission column writes it: one code span of the shape resource:action.
permission_span='^`[a-z0-9._-]+:[a-z0-9._-]+`$'

# Every file that states the seeded login (C5). "email-only" marks a file that names the e-mail and not
# the password. A new copy goes here, so a change of the login reaches every one of them.
login_copies='README.md
AGENTS.md
.template.config/consumer/README.md
.template.config/template.json
docs/development.md
docs/features/auth.md
docs/features/invoices.md
.claude/skills/run-app/SKILL.md
.claude/agents/ui-reviewer.md
docs/deployment.md email-only
.github/workflows/ci.yml email-only'

# --- facts read once ------------------------------------------------------------------------------

# The pack csproj's two URLs: the site (C2, C7) and the repository (C7, C10).
csproj_value() { sed -n "s/^[[:space:]]*<$1>\\(.*\\)<\\/$1>[[:space:]]*\$/\\1/p" "$pack_csproj"; }
site_url=""
repo_url=""
if [ -f "$pack_csproj" ]; then
  site_url=$(csproj_value PackageProjectUrl)
  repo_url=$(csproj_value RepositoryUrl)
fi

# The boolean parameters of template.json in its order, skipRestore last:
# "Name<US>default<US>displayName<US>description<US>line", the strings unescaped.
read_params() {
  awk '
    function val(s) {
      sub(/^[[:space:]]*"[^"]*": "/, "", s)
      sub(/",?[[:space:]]*$/, "", s)
      return unesc(s)
    }
    function unesc(s,   out, i, c) {
      out = ""
      while ((i = index(s, "\\")) > 0) {
        c = substr(s, i + 1, 1)
        out = out substr(s, 1, i - 1) ((c == "\"" || c == "\\" || c == "/") ? c : "\\" c)
        s = substr(s, i + 2)
      }
      return out s
    }
    /^  "symbols": [{]/ { ins = 1; next }
    ins && /^  [}]/ { ins = 0 }
    ins && /^    "[^"]*": [{]/ {
      name = $0; sub(/^    "/, "", name); sub(/".*/, "", name)
      isbool = 0; def = ""; disp = ""; desc = ""; at = NR; next
    }
    ins && name != "" && /^      "datatype": "bool"/ { isbool = 1 }
    ins && name != "" && /^      "defaultValue": "/ { def = val($0) }
    ins && name != "" && /^      "displayName": "/ { disp = val($0) }
    ins && name != "" && /^      "description": "/ { desc = val($0) }
    ins && name != "" && /^    [}],?[[:space:]]*$/ {
      if (isbool) {
        row = name "\037" def "\037" disp "\037" desc "\037" at
        if (name == "skipRestore") last = row; else print row
      }
      name = ""
    }
    END { if (last != "") print last }
  ' "$template"
}
params=""
[ -f "$template" ] && params=$(read_params)

# The CLI flag of a symbol, from dotnetcli.host.json.
long_names=""
if [ -f "$cli_host" ]; then
  long_names=$(awk '
    /^    "[^"]*": [{]/ { name = $0; sub(/^    "/, "", name); sub(/".*/, "", name); next }
    name != "" && /"longName": "/ { v = $0; sub(/.*"longName": "/, "", v); sub(/".*/, "", v); print name "\t" v; name = "" }
  ' "$cli_host")
fi
long_name() { awk -F'\t' -v n="$1" '$1 == n { print $2; exit }' <<< "$long_names"; }

# The template's package id and short name (C3).
package_id=""
short_name=""
if [ -f "$template" ]; then
  package_id=$(awk '/^    "templatePackageId": [{]/ { t = 1 } t && /"value": "/ { v = $0; sub(/.*"value": "/, "", v); sub(/".*/, "", v); print v; exit }' "$template")
  short_name=$(sed -n 's/^  "shortName": "\([^"]*\)",\{0,1\}$/\1/p' "$template")
fi

# --- C1: the pitch --------------------------------------------------------------------------------
pitch=""
short=""
check_pitch() {
  local count line stars bad
  need "$pitch_file" C1a || return 0
  count=$(awk 'NF { n++ } END { print n + 0 }' "$pitch_file")
  if [ "$count" != 1 ]; then
    report "$pitch_file" 2 "C1a: the pitch is one line, and this file has $count non-empty lines; keep one sentence"
    return 0
  fi
  line=$(awk 'NF { print; exit }' "$pitch_file")
  stars=$(awk 'NF { print gsub(/[*]/, "*"); exit }' "$pitch_file")
  if [ "$stars" != 2 ]; then
    report "$pitch_file" 1 "C1a: the pitch holds exactly one *...* span, its short form; this line has $stars asterisks"
    return 0
  fi
  bad=$(awk '/[`&<>"\\|]/ { print NR; exit }' "$pitch_file")
  if [ -n "$bad" ]; then
    report "$pitch_file" "$bad" "C1a: the pitch holds one of \` & < > \" \\ |, which JSON, XML or Markdown would change; reword it"
    return 0
  fi
  pitch=$(printf '%s\n' "$line" | tr -d '*' | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
  short=$(printf '%s\n' "$line" | sed 's/^[^*]*[*]\([^*]*\)[*].*$/\1/')
}

check_pitch_copies() {
  local para at desc fm hero head
  [ -n "$pitch" ] || return 0
  local home="the home is $pitch_file (\"$pitch\")"

  if need "$readme" C1b && ! grep -qF -- "$pitch" <<< "$(plain < "$readme")"; then
    report "$readme" 1 "C1b: README.md does not contain the pitch; $home; update the copy"
  fi

  if need "$pack_readme" C1c; then
    para=$(awk '!h && /^# / { h = 1; next } h && /^[[:space:]]*$/ { if (s) exit; next } h { s = 1; print }' "$pack_readme" | plain)
    at=$(awk '!h && /^# / { h = 1; next } h && NF { print NR; exit }' "$pack_readme")
    if [ -z "$para" ]; then
      report "$pack_readme" 1 "C1c: read no paragraph after the H1 of $pack_readme (format changed?); teach scripts/check-readme-sync.sh"
    elif [ "$para" != "$pitch" ]; then
      report "$pack_readme" "$at" "C1c: the package README's first paragraph is \"$para\"; $home; update the copy"
    fi
  fi

  if need "$pack_csproj" C1d; then
    desc=$(csproj_value Description)
    desc=${desc%%$'\n'*}
    if [ "$desc" != "$pitch" ]; then
      report "$pack_csproj" "$(line_of "$pack_csproj" '<Description>')" "C1d: <Description> is \"$desc\"; $home; update the copy"
    fi
  fi

  if need "$template" C1e; then
    desc=$(sed -n 's/^  "description": "\(.*\)",\{0,1\}$/\1/p' "$template" | sed -e 's/\\"/"/g' -e 's/\\\\/\\/g')
    desc=${desc%%$'\n'*}
    if [ "$desc" != "$pitch" ]; then
      report "$template" "$(line_of "$template" '  "description": ')" "C1e: the template's description is \"$desc\"; $home; update the copy"
    fi
  fi

  if need "$site_config" C1f && ! grep -qF 'eng/readme/pitch.md' "$site_config"; then
    report "$site_config" 1 "C1f: $site_config does not read eng/readme/pitch.md; the site's pitch is derived from that file"
  fi
  if need "$astro_config" C1f && ! grep -qE '^[[:space:]]*description: pitch,?[[:space:]]*$' "$astro_config"; then
    report "$astro_config" 1 "C1f: $astro_config has no line 'description: pitch,'; the site's description is the pitch, read through site/trellis.config.mjs"
  fi
  if need "$landing" C1f; then
    fm=$(frontmatter_line "$landing" '^description:')
    if [ -n "$fm" ]; then
      report "$landing" "${fm%%"$tab"*}" "C1f: the landing page's frontmatter has a description of its own; drop it, so Starlight uses the site's, which is the pitch"
    fi

    hero=$(frontmatter_line "$landing" '^  title: ')
    if [ -z "$hero" ]; then
      report "$landing" 1 "C1g: read no hero title ('  title: ') from the frontmatter of $landing (format changed?); teach scripts/check-readme-sync.sh"
    else
      at=${hero%%"$tab"*}
      hero=$(printf '%s\n' "${hero#*"$tab"}" | sed -e 's/^  title: //' -e "s/^[[:space:]]*['\"]//" -e "s/['\"][[:space:]]*\$//" -e 's/<[^>]*>//g' -e 's/[.][[:space:]]*$//' -e 's/[[:space:]]*$//')
      if [ "$hero" != "$short" ]; then
        report "$landing" "$at" "C1g: the hero title reads \"$hero\"; the home is the *...* span of $pitch_file (\"$short\"); update the copy"
      fi
    fi

    head=$(frontmatter_line "$landing" '^    content: ')
    if [ -z "$head" ]; then
      report "$landing" 1 "C1h: read no head title ('    content: ') from the frontmatter of $landing (format changed?); teach scripts/check-readme-sync.sh"
    else
      at=${head%%"$tab"*}
      head=$(printf '%s\n' "${head#*"$tab"}" | sed -e 's/^    content: //' -e "s/^[[:space:]]*['\"]//" -e "s/['\"][[:space:]]*\$//" -e 's/[[:space:]]*$//')
      case "$head" in
        *"$short") ;;
        *) report "$landing" "$at" "C1h: the head title \"$head\" does not end with the short form; the home is the *...* span of $pitch_file (\"$short\"); update the copy" ;;
      esac
    fi
  fi

  if need "$renderer" C1i && ! grep -qF 'pitch.md' "$renderer"; then
    report "$renderer" 1 "C1i: $renderer does not read pitch.md, so the banner's tagline has a home of its own; read it from $pitch_file"
  fi
  if [ -f "$readme" ]; then
    at=$(line_of "$readme" 'src="eng/readme/title-light.svg"')
    if [ -z "$at" ]; then
      report "$readme" 1 "C1i: read no line holding src=\"eng/readme/title-light.svg\" from README.md (format changed?); teach scripts/check-readme-sync.sh"
    elif ! grep -qF -- "$short" <<< "$(awk -v n="$at" 'NR == n' "$readme")"; then
      report "$readme" "$at" "C1i: the alt text of the title picture does not hold the short form \"$short\" (the *...* span of $pitch_file); update the copy"
    fi
  fi
}

# --- C2, C2b: the options -------------------------------------------------------------------------

# table_rows FILE: "NR<TAB>row" for each row of the options table, delimiter row skipped.
table_rows() {
  awk '
    $0 == "| Option | Default | What it does |" { t = 1; next }
    t == 1 && /^[|][-:| ]*[|][[:space:]]*$/ { t = 2; next }
    t == 2 && /^[|]/ { print NR "\t" $0; next }
    t == 2 { exit }
  ' "$1"
}

check_options_table() {  # FILE
  local file="$1" rows expected n_rows n_expected i at row name def disp desc flag cells c1 c2 c3 want
  need "$file" C2 || return 0
  rows=$(table_rows "$file")
  if [ -z "$rows" ]; then
    report "$file" 1 "C2: read no options table ('| Option | Default | What it does |') from $file (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  n_rows=$(awk 'END { print NR }' <<< "$rows")
  n_expected=$(awk 'END { print NR }' <<< "$params")
  if [ "$n_rows" != "$n_expected" ]; then
    report "$file" "${rows%%"$tab"*}" "C2: the options table of $file has $n_rows rows and template.json has $n_expected boolean parameters; the home is template.json; give each parameter its row, in template.json's order with --skip-restore last"
  fi
  i=0
  while IFS="$us" read -r name def disp desc _; do
    [ -n "$name" ] || continue
    i=$((i + 1))
    flag=$(long_name "$name")
    [ -n "$flag" ] || continue   # no longName: check_options has said so, and there is no row to expect
    flag="--$flag"
    row=$(awk -F'\t' -v i="$i" 'NR == i { print; exit }' <<< "$rows")
    [ -n "$row" ] || continue
    at=${row%%"$tab"*}
    row=${row#*"$tab"}
    cells=$(printf '%s\n' "$row" | sed -e 's/^|[[:space:]]*//' -e 's/[[:space:]]*|[[:space:]]*$//' | awk '{ n = split($0, p, " [|] "); for (k = 1; k <= n; k++) print p[k] }')
    c1=$(awk 'NR == 1' <<< "$cells")
    c2=$(awk 'NR == 2' <<< "$cells")
    c3=$(awk 'NR >= 3' <<< "$cells")
    want="[\`$flag\`](${site_url}start/options/#$flag)"
    if [ "$c1" != "$want" ]; then
      report "$file" "$at" "C2: row $i of the options table of $file is $c1; the home is template.json, whose row $i is $flag: write $want"
      continue
    fi
    case "$def" in true | True) want=on ;; *) want=off ;; esac
    if [ "$c2" != "$want" ]; then
      report "$file" "$at" "C2: the default of $flag in $file is '$c2'; the home is its defaultValue in template.json ($def): write '$want'"
    fi
    if [ "$c3" != "$desc" ]; then
      report "$file" "$at" "C2: the description of $flag in $file is not template.json's; the home is template.json (\"$desc\"); update the copy"
    fi
  done <<< "$params"
}

# all_flags: every boolean parameter's flag, "--<longName>", one per line.
all_flags() {
  local name
  while IFS="$us" read -r name _; do
    if [ -n "$name" ] && [ -n "$(long_name "$name")" ]; then printf -- '--%s\n' "$(long_name "$name")"; fi
  done <<< "$params"
}

# C2c: a code span of README.md's prose that is a flag alone (`--use-redis`) names a boolean parameter.
check_option_flags() {
  local at flag known
  [ -f "$readme" ] || return 0
  known=$(all_flags)
  while IFS=$'\t' read -r at flag; do
    [ -n "$at" ] || continue
    in_list "$flag" "$known" ||
      report "$readme" "$at" "C2c: \`$flag\` is no flag of a boolean parameter in $cli_host; the home is the longName there; update the copy"
  done <<< "$(awk '/^[[:space:]]*(```|~~~)/ { fence = !fence; next } !fence { s = $0; while (match(s, /`--[a-z][a-z0-9-]*`/)) { print NR "\t" substr(s, RSTART + 1, RLENGTH - 2); s = substr(s, RSTART + RLENGTH) } }' "$readme")"
}

check_options() {
  local name def disp desc at text
  need "$template" C2 || return 0
  if [ -z "$params" ]; then
    report "$template" 1 "C2: read no boolean parameters from $template (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  while IFS="$us" read -r name def disp desc at; do
    [ -n "$name" ] || continue
    case "$desc" in
      *"|"*) report "$template" "$at" "C2: the description of $name holds '|', which a Markdown table cannot show; reword it in template.json" ;;
    esac
    if [ -z "$(long_name "$name")" ]; then
      report "$cli_host" 1 "C2: $cli_host has no longName for the boolean parameter $name; add its flag there"
    fi
  done <<< "$params"
  check_options_table "$readme"
  check_options_table "$pack_readme"
  check_option_flags

  need "$ide_host" C2b || return 0
  while IFS="$us" read -r name def disp desc at; do
    [ -n "$name" ] || continue
    if [ -z "$disp" ]; then
      report "$template" "$at" "C2b: the boolean parameter $name has no displayName in $template, the label the dialogs show; add one, and the same text as its name.text in $ide_host"
      continue
    fi
    text=$(N="$name" awk '$0 ~ "\"id\": \"" ENVIRON["N"] "\"" { f = 1; next } f && /"text": "/ { v = $0; sub(/.*"text": "/, "", v); sub(/".*/, "", v); print v; exit }' "$ide_host")
    if [ -z "$text" ]; then
      report "$ide_host" 1 "C2b: $ide_host has no name.text for the boolean parameter $name; add its entry with the displayName \"$disp\""
    elif [ "$text" != "$disp" ]; then
      report "$ide_host" "$(line_of "$ide_host" "\"text\": \"$text\"")" "C2b: the dialog label of $name is \"$text\" here and \"$disp\" in template.json; the home is the displayName in template.json; update the copy"
    fi
  done <<< "$params"
}

# --- C3: the commands -----------------------------------------------------------------------------

# quickstart_strings: "key<US>n<US>string<US>line" for every literal between the markers, with
# ${packageId} and ${templateShortName} substituted; "NOMARK", "NOEND" or "BAD<US>line" on a shape
# this script cannot read: a key whose value goes on to the next line, a double-quoted string, an
# unclosed quote, another substitution.
quickstart_strings() {
  PKG="$package_id" SHORT="$short_name" awk '
    function replace(s, from, to,   out, i) {
      out = ""
      while ((i = index(s, from)) > 0) { out = out substr(s, 1, i - 1) to; s = substr(s, i + length(from)) }
      return out s
    }
    /^\/\/ readme-sync:quickstart/ { m = 1; found = 1; next }
    m && /^\/\/ readme-sync:end/ { m = 0; ended = 1; next }
    m && /^    / { print "BAD\037" NR; next }
    m && /^  [A-Za-z]+: / {
      key = $0; sub(/^  /, "", key); sub(/:.*/, "", key)
      rest = $0; sub(/^  [A-Za-z]+: /, "", rest)
      if (rest ~ /^[[({][[:space:]]*$/) { print "BAD\037" NR; next }
      n = 0
      while (length(rest) > 0) {
        c = substr(rest, 1, 1)
        if (c == "\"") { print "BAD\037" NR; break }
        if (c == "`" || c == "\047") {
          j = index(substr(rest, 2), c)
          if (j == 0) { print "BAD\037" NR; break }
          lit = substr(rest, 2, j - 1)
          lit = replace(lit, "${packageId}", ENVIRON["PKG"])
          lit = replace(lit, "${templateShortName}", ENVIRON["SHORT"])
          if (index(lit, "${") > 0) { print "BAD\037" NR; break }
          n++
          print key "\037" n "\037" lit "\037" NR
          rest = substr(rest, j + 2)
        } else {
          rest = substr(rest, 2)
        }
      }
    }
    END { if (!found) print "NOMARK"; else if (!ended) print "NOEND" }
  ' "$repo_data"
}

# command_lines FILE: "NR<TAB>command" for each dotnet new / git clone line of a fenced block.
command_lines() {
  awk '
    /^[[:space:]]*(```|~~~)/ { fence = !fence; next }
    fence {
      s = $0
      sub(/^[[:space:]]+/, "", s)
      if (index(s, "dotnet new ") != 1 && index(s, "git clone ") != 1) next
      sub(/[[:space:]]+#.*$/, "", s)
      sub(/[[:space:]]+$/, "", s)
      print NR "\t" s
    }
  ' "$1"
}

check_commands() {
  local strings allowed first line cmd key n lit at have file placeholder
  need "$repo_data" C3 || return 0
  if [ -z "$package_id" ] || [ -z "$short_name" ]; then
    report "$template" 1 "C3: read no templatePackageId value or shortName from $template (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  strings=$(quickstart_strings)
  first=${strings%%$'\n'*}
  case "$first" in
    NOMARK | "")
      report "$repo_data" 1 "C3: read no '// readme-sync:quickstart' marker from $repo_data (format changed?); wrap the quickstart object in the two marker comments"
      return 0
      ;;
  esac
  if in_list NOEND "$strings"; then
    report "$repo_data" 1 "C3: $repo_data opens '// readme-sync:quickstart' and never closes it with '// readme-sync:end'"
    return 0
  fi
  if grep -q '^BAD' <<< "$strings"; then
    at=$(awk -F"$us" '$1 == "BAD" { print $2; exit }' <<< "$strings")
    report "$repo_data" "$at" "C3: this quickstart line holds what this script cannot read (a value continued on the next line, a double-quoted string, an unclosed quote, or a substitution other than \${packageId} and \${templateShortName}); keep each key on one line with backtick or single-quote strings, or teach scripts/check-readme-sync.sh"
    return 0
  fi
  for key in install update uninstall create fromClone; do
    awk -F"$us" -v k="$key" '$1 == k { f = 1 } END { exit f ? 0 : 1 }' <<< "$strings" ||
      report "$repo_data" 1 "C3: read no $key string from the quickstart of $repo_data (format changed?); the README's commands are held to it, so restore the key or teach scripts/check-readme-sync.sh"
  done
  placeholder=""
  [ -f "$renderer" ] && placeholder=$(sed -n 's/^PROJECT_PLACEHOLDER = "\([^"]*\)".*/\1/p' "$renderer")
  if [ -z "$placeholder" ]; then
    report "$renderer" 1 "C3: read no PROJECT_PLACEHOLDER from $renderer (format changed?); it names the project in the banner's create line"
    return 0
  fi
  allowed=$(awk -F"$us" '{ print $3 }' <<< "$strings")
  allowed="$allowed
dotnet new $short_name -n $placeholder"

  for file in "$readme" "$pack_readme"; do
    [ -f "$file" ] || continue
    have=$(command_lines "$file")
    while IFS=$'\t' read -r at cmd; do
      [ -n "$at" ] || continue
      if ! in_list "$cmd" "$allowed"; then
        report "$file" "$at" "C3: '$cmd' is no quickstart command; the home is the quickstart object of $repo_data; update the copy (or the home first, and the site with it)"
      fi
    done <<< "$have"
    have=$(awk -F'\t' '{ print $2 }' <<< "$have")
    while IFS="$us" read -r key n lit _; do
      [ -n "$key" ] || continue
      case "$file:$key:$n" in
        "$readme":install:1 | "$readme":update:1 | "$readme":update:2 | "$readme":uninstall:1 | "$readme":create:1 | \
        "$readme":fromClone:1 | "$readme":fromClone:2 | "$pack_readme":install:1) ;;
        *) continue ;;
      esac
      if ! in_list "$lit" "$have"; then
        report "$file" 1 "C3: $file shows no '$lit', the $key command of the quickstart in $repo_data; add it to a fenced block"
      fi
    done <<< "$strings"
  done
}

# --- C4, C4b: the use cases -----------------------------------------------------------------------

# use_case_rows: "NR<US>METHOD path<US>Name<US>last cell" for each README table row with a request span
# (the Name is empty when the row opens with none).
use_case_rows() {
  awk '
    /^[|]/ {
      if (!match($0, /`(GET|POST|PUT|PATCH|DELETE) \/api\/v[0-9]+\/[^`]*`/)) next
      span = substr($0, RSTART + 1, RLENGTH - 2)
      sub(/[?].*/, "", span)
      name = ""
      if (match($0, /^[|] [*][*][A-Za-z0-9]+[*][*]/)) name = substr($0, RSTART + 4, RLENGTH - 6)
      row = $0
      sub(/[[:space:]]*[|][[:space:]]*$/, "", row)
      n = split(row, p, " [|] ")
      last = p[n]
      sub(/^[[:space:]]+/, "", last)
      print NR "\037" span "\037" name "\037" last
    }
  ' "$readme"
}

# prose_requests: "NR<TAB>METHOD path" for each request code span outside a table row and a fence, its
# query dropped.
prose_requests() {
  awk '
    /^[[:space:]]*(```|~~~)/ { fence = !fence; next }
    fence || /^[|]/ { next }
    {
      s = $0
      while (match(s, /`(GET|POST|PUT|PATCH|DELETE) \/api\/v[0-9]+\/[^`]*`/)) {
        span = substr(s, RSTART + 1, RLENGTH - 2)
        sub(/[?].*/, "", span)
        print NR "\t" span
        s = substr(s, RSTART + RLENGTH)
      }
    }
  ' "$readme"
}

check_use_cases() {
  local ops rows at op name last key perm expected ref const value consts row
  need "$readme" C4 || return 0
  need "$openapi" C4 || return 0
  ops=$(awk '
    /^    "\/[^"]*": [{]/ { p = $0; sub(/^    "/, "", p); sub(/".*/, "", p); next }
    p != "" && /^      "(get|post|put|patch|delete)": [{]/ { m = $0; sub(/^      "/, "", m); sub(/".*/, "", m); print toupper(m) " " p }
  ' "$openapi")
  if [ -z "$ops" ]; then
    report "$openapi" 1 "C4: read no operations from $openapi (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  rows=$(use_case_rows)
  if [ -z "$rows" ]; then
    report "$readme" 1 "C4: read no use-case rows (a table row holding \`METHOD /api/vN/...\`) from README.md (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  while IFS="$us" read -r at op name last; do
    [ -n "$at" ] || continue
    if ! in_list "$op" "$ops"; then
      report "$readme" "$at" "C4: \`$op\` is no operation of $openapi; the home is the committed OpenAPI document; update the row"
    fi
    if [ -z "$name" ]; then
      report "$readme" "$at" "C4: this use-case row opens with no **Name**; name its use-case folder under $features_dir/<Feature>/"
    elif [ -z "$(find "$features_dir" -mindepth 2 -maxdepth 2 -type d -name "$name" 2>/dev/null)" ]; then
      report "$readme" "$at" "C4: **$name** is no use-case folder under $features_dir/<Feature>/; the home is the folder; update the row"
    fi
  done <<< "$rows"
  while IFS= read -r op; do
    [ -n "$op" ] || continue
    if ! in_list "$op" "$(awk -F"$us" '{ print $2 }' <<< "$rows")"; then
      report "$readme" 1 "C4: the operation \`$op\` of $openapi has no row in the use-case tables of README.md; add one"
    fi
  done <<< "$ops"
  while IFS=$'\t' read -r at op; do
    [ -n "$at" ] || continue
    in_list "$op" "$ops" ||
      report "$readme" "$at" "C4: \`$op\` is no operation of $openapi; the home is the committed OpenAPI document; update the copy"
  done <<< "$(prose_requests)"

  need "$endpoint_tests" C4b || return 0
  need "$permissions" C4b || return 0
  expected=$(awk '
    /ExpectedPermissions = new/ { e = 1; next }
    e && /^[[:space:]]*[}];/ { exit }
    e && /\["[A-Z]+ [^"]*"\] = Permissions[.][A-Za-z0-9]+[.][A-Za-z0-9]+,/ {
      k = $0; sub(/^[^"]*"/, "", k); sub(/".*/, "", k)
      r = $0; sub(/.*= Permissions[.]/, "", r); sub(/,.*/, "", r)
      print NR "\t" k "\t" r
    }
  ' "$endpoint_tests")
  if [ -z "$expected" ]; then
    report "$endpoint_tests" 1 "C4b: read no ExpectedPermissions rows from $endpoint_tests (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  consts=$(awk '
    /public static class [A-Za-z0-9_]+/ { c = $0; sub(/.*public static class /, "", c); sub(/[^A-Za-z0-9_].*/, "", c) }
    /public const string [A-Za-z0-9_]+ = "/ { n = $0; sub(/.*public const string /, "", n); v = n; sub(/ .*/, "", n); sub(/^[^"]*"/, "", v); sub(/".*/, "", v); print c "." n "\t" v }
  ' "$permissions")
  if [ -z "$consts" ]; then
    report "$permissions" 1 "C4b: read no permission constants from $permissions (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  while IFS=$'\t' read -r at key ref; do
    [ -n "$at" ] || continue
    value=$(awk -F'\t' -v r="$ref" '$1 == r { print $2; exit }' <<< "$consts")
    if [ -z "$value" ]; then
      report "$endpoint_tests" "$at" "C4b: Permissions.$ref resolves to no 'public const string' in $permissions; teach scripts/check-readme-sync.sh"
      continue
    fi
    row=$(awk -F"$us" -v k="$key" -v re="$permission_span" '{ op = $2; sub(/ \/api\/v[0-9]+\//, " ", op) } op == k && $4 ~ re { print $1 "\t" $4; exit }' <<< "$rows")
    if [ -z "$row" ]; then
      report "$readme" 1 "C4b: '$key' of ExpectedPermissions ($endpoint_tests) has no use-case row with a \`resource:action\` permission in README.md; add the row"
      continue
    fi
    perm=${row#*"$tab"}
    perm=${perm#\`}
    perm=${perm%\`}
    if [ "$perm" != "$value" ]; then
      report "$readme" "${row%%"$tab"*}" "C4b: '$key' needs \`$value\` (ExpectedPermissions, $endpoint_tests) and README.md says \`$perm\`; the home is the test's table; update the copy"
    fi
  done <<< "$expected"
  # The other direction: a row that states a permission for an endpoint the test's table does not list.
  while IFS="$us" read -r at op name last; do
    [ -n "$at" ] || continue
    grep -qE -- "$permission_span" <<< "$last" || continue
    key=$(sed 's| /api/v[0-9]*/| |' <<< "$op")
    awk -F'\t' -v k="$key" '$2 == k { f = 1 } END { exit f ? 0 : 1 }' <<< "$expected" ||
      report "$readme" "$at" "C4b: README.md says \`$op\` needs $last, and ExpectedPermissions ($endpoint_tests) has no '$key'; the home is the test's table; update the copy"
  done <<< "$rows"
}

# --- C5: the seeded login -------------------------------------------------------------------------
check_login() {
  local email password entry file mode listed scanned
  need "$seed_options" C5 || return 0
  email=$(sed -n 's/.*AdminEmail { get; init; } = "\([^"]*\)".*/\1/p' "$seed_options")
  password=$(sed -n 's/.*DefaultAdminPassword = "\([^"]*\)".*/\1/p' "$seed_options")
  if [ -z "$email" ] || [ -z "$password" ]; then
    report "$seed_options" 1 "C5: read no AdminEmail or DefaultAdminPassword from $seed_options (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  listed=""
  while IFS= read -r entry; do
    [ -n "$entry" ] || continue
    file=${entry%% *}
    mode=""
    [ "$file" = "$entry" ] || mode=${entry#* }
    listed="$listed
$file"
    if [ ! -f "$file" ]; then
      report "$file" 1 "C5: $file is in login_copies of scripts/check-readme-sync.sh and does not exist; drop it from the list"
      continue
    fi
    if ! grep -qF -- "$email" "$file"; then
      report "$file" 1 "C5: $file does not hold the seeded e-mail; the home is $seed_options ($email); update the copy"
    fi
    if [ "$mode" != "email-only" ] && ! grep -qF -- "$password" "$file"; then
      report "$file" "$(line_of "$file" "$email")" "C5: $file does not hold the seeded password; the home is $seed_options ($password); update the copy"
    fi
  done <<< "$login_copies"
  scanned=$(grep -rlIF --exclude-dir=worktrees --exclude-dir=node_modules -- "$email" \
    README.md AGENTS.md docs .claude .template.config .github/workflows 2>/dev/null || true)
  while IFS= read -r file; do
    [ -n "$file" ] || continue
    if ! in_list "$file" "$listed"; then
      report "$file" "$(line_of "$file" "$email")" "C5: $file states the seeded login and is not in login_copies of scripts/check-readme-sync.sh; add it there, so a change of the login reaches it"
    fi
  done <<< "$scanned"
}

# --- C6a, C6b: the gate numbers -------------------------------------------------------------------
check_gate_numbers() {
  local floors section count pair ring n at home copies file line found
  need "$ci" C6a || return 0
  need "$development" C6a || return 0
  floors=$(grep -oE 'min-line-rate src/[A-Za-z]+=[0-9]+' "$ci" || true)
  if [ -z "$floors" ]; then
    report "$ci" 1 "C6a: read no '--min-line-rate src/<Ring>=<n>' from $ci (format changed?); teach scripts/check-readme-sync.sh"
  else
    section=$(awk '/^### Gates beyond the tests[[:space:]]*$/ { s = 1; next } s && /^##+ / { exit } s' "$development")
    at=$(line_of "$development" '### Gates beyond the tests')
    if [ -z "$section" ]; then
      report "$development" 1 "C6a: read no '### Gates beyond the tests' section from $development (format changed?); it states the coverage floors"
    else
      while IFS= read -r pair; do
        [ -n "$pair" ] || continue
        ring=${pair#min-line-rate src/}
        n=${ring#*=}
        ring=${ring%%=*}
        if ! grep -qF -- "$ring $n %" <<< "$section"; then
          report "$development" "$at" "C6a: 'Gates beyond the tests' does not say '$ring $n %'; the home is the floor in $ci; update the copy"
        fi
      done <<< "$floors"
      count=$(awk '{ s = $0; while (match(s, /[A-Z][a-z]+ [0-9]+ %/)) { n++; s = substr(s, RSTART + RLENGTH) } } END { print n + 0 }' <<< "$section")
      if [ "$count" != "$(awk 'END { print NR }' <<< "$floors")" ]; then
        report "$development" "$at" "C6a: 'Gates beyond the tests' states $count ring floors and $ci sets $(awk 'END { print NR }' <<< "$floors"); the home is $ci; update the copy"
      fi
    fi
  fi

  home=$(awk '/^[|] [*][*]Aspire CLI[*][*]/ { r = $0; sub(/[[:space:]]*[|][[:space:]]*$/, "", r); n = split(r, p, "|"); v = p[n]; sub(/^[[:space:]]+/, "", v); sub(/[[:space:]]+$/, "", v); print v; exit }' "$development")
  at=$(line_of "$development" '| **Aspire CLI**')
  if [ -z "$home" ]; then
    report "$development" 1 "C6b: read no row starting '| **Aspire CLI**' from $development (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  if ! grep -qE '^[0-9]+([.][0-9]+)*$' <<< "$home"; then
    report "$development" "$at" "C6b: the Aspire CLI row's last cell is '$home', not a bare version; the Minimum column holds the number alone"
    return 0
  fi
  if need "$doctor" C6b; then
    found=$(sed -n 's/^aspire_floor=\([0-9.]*\).*/\1/p' "$doctor")
    if [ -z "$found" ]; then
      report "$doctor" 1 "C6b: read no 'aspire_floor=<n>' from $doctor (format changed?); teach scripts/check-readme-sync.sh"
    elif [ "$found" != "$home" ]; then
      report "$doctor" "$(line_of "$doctor" 'aspire_floor=')" "C6b: aspire_floor is $found; the home is the Minimum of the Aspire CLI row in $development ($home); update the copy"
    fi
  fi
  copies=$(grep -rnoE --exclude-dir=adr --exclude-dir=worktrees --exclude-dir=node_modules \
    'Aspire CLI [0-9]+([.][0-9]+)* or later' docs .claude 2>/dev/null || true)
  while IFS= read -r line; do
    [ -n "$line" ] || continue
    file=${line%%:*}
    n=${line#*:}
    at=${n%%:*}
    n=${n#*:}
    n=${n#Aspire CLI }
    n=${n% or later}
    if [ "$n" != "$home" ]; then
      report "$file" "$at" "C6b: 'Aspire CLI $n or later'; the home is the Minimum of the Aspire CLI row in $development ($home); update the copy"
    fi
  done <<< "$copies"
}

# --- C7: the links --------------------------------------------------------------------------------

# urls FILE PREFIX: "NR<TAB>url" for every URL in FILE that starts with PREFIX.
urls() {
  P="$2" awk '
    BEGIN { T = " )>\"\047`]<\t"; P = ENVIRON["P"] }
    P != "" {
      s = $0
      while ((i = index(s, P)) > 0) {
        u = substr(s, i)
        j = 1
        while (j <= length(u) && index(T, substr(u, j, 1)) == 0) j++
        u = substr(u, 1, j - 1)
        sub(/[.,;:]+$/, "", u)
        print NR "\t" u
        s = substr(s, i + length(P))
      }
    }
  ' "$1"
}

check_links() {
  local site base expected file at url path anchor flags found
  need "$site_config" C7 || return 0
  site=$(sed -n "s/^export const site = ['\"]\\([^'\"]*\\)['\"];.*/\\1/p" "$site_config")
  base=$(sed -n "s/^export const base = ['\"]\\([^'\"]*\\)['\"];.*/\\1/p" "$site_config")
  expected="$site$base/"
  if [ -z "$site_url" ]; then
    report "$pack_csproj" 1 "C7: read no <PackageProjectUrl> from $pack_csproj (format changed?); it is the site's address"
    return 0
  fi
  if [ "$site_url" != "$expected" ]; then
    report "$pack_csproj" "$(line_of "$pack_csproj" '<PackageProjectUrl>')" "C7: <PackageProjectUrl> is $site_url and the site is at $expected (site + base in $site_config); update the copy"
  fi
  flags=$(all_flags)
  for file in "$readme" "$pack_readme"; do
    [ -f "$file" ] || continue
    while IFS=$'\t' read -r at url; do
      [ -n "$at" ] || continue
      path=${url#"$site_url"}
      anchor=""
      case "$path" in *"#"*) anchor=${path#*#}; path=${path%%#*} ;; esac
      path=${path%/}
      if [ -n "$path" ]; then
        found=""
        for f in "$site_pages/$path.mdx" "$site_pages/$path.md" "$site_pages/$path/index.mdx" "$site_pages/$path/index.md"; do
          if [ -f "$f" ]; then found="$f"; fi
        done
        if [ -z "$found" ]; then
          report "$file" "$at" "C7: $url names a page the site has no file for ($site_pages/$path.mdx); link a doc by its repository path, or a page of the site's own"
          continue
        fi
      fi
      if [ -n "$anchor" ] && [ -n "$flags" ]; then   # no flags: C2 has said why
        if [ "$path" != "start/options" ] || ! in_list "$anchor" "$flags"; then
          report "$file" "$at" "C7: the anchor #$anchor of $url cannot be checked; only the Options page's anchors, an option's flag, can"
        fi
      fi
    done <<< "$(urls "$file" "$site_url")"
    [ -n "$repo_url" ] || continue
    while IFS=$'\t' read -r at url; do
      [ -n "$at" ] || continue
      case "$url" in
        "$repo_url/blob/main/"*) path=${url#"$repo_url/blob/main/"} ;;
        "$repo_url/tree/main/"*) path=${url#"$repo_url/tree/main/"} ;;
        "$repo_url/actions/workflows/"*)
          path=${url#"$repo_url/actions/workflows/"}
          path=".github/workflows/${path%%/*}"
          ;;
        "$repo_url/blob/"* | "$repo_url/tree/"*)
          report "$file" "$at" "C7: $url links a ref other than main, which this script cannot check against the tree; link main, or teach scripts/check-readme-sync.sh"
          continue
          ;;
        *) continue ;;
      esac
      path=${path%%#*}
      path=${path%%\?*}
      if [ ! -e "$path" ]; then
        report "$file" "$at" "C7: $url names $path, which does not exist on main's tree here; update the link"
      fi
    done <<< "$(urls "$file" "$repo_url/")"
  done
}

# --- C8: the pictures -----------------------------------------------------------------------------
check_pictures() {
  local at value shot name
  need "$readme" C8 || return 0
  while IFS=$'\t' read -r at value; do
    [ -n "$at" ] || continue
    case "$value" in *://* | data:*) continue ;; esac
    if [ ! -e "$value" ]; then
      report "$readme" "$at" "C8: the picture $value does not exist; render or copy it, or update the README"
    fi
  done <<< "$(awk '{ s = $0; while (match(s, /(src|srcset)="[^"]*"/)) { v = substr(s, RSTART, RLENGTH); sub(/^[a-z]*="/, "", v); sub(/"$/, "", v); print NR "\t" v; s = substr(s, RSTART + RLENGTH) } }' "$readme")"
  [ -d "$shots" ] || return 0
  for shot in "$shots"/*.png; do
    [ -e "$shot" ] || continue
    name=$(basename "$shot")
    if grep -qF -- "$name" "$readme"; then continue; fi
    if [ -d site/src ] && grep -rqF --exclude-dir=node_modules -- "$name" site/src; then continue; fi
    report "$shot" 1 "C8: $shot is used neither by README.md nor by a file under site/src; show it or delete it"
  done
  check_picture_words
}

# The screenshots' alt texts and their caption, which README.md and the landing page say word for word.
check_picture_words() {
  local page at alt caption last
  [ -f "$landing" ] || return 0
  page=$(plain < "$landing")
  while IFS=$'\t' read -r at alt; do
    [ -n "$at" ] || continue
    grep -qF -- "$alt" <<< "$page" ||
      report "$readme" "$at" "C8: the alt text \"$alt\" of a screenshot is not on the landing page ($landing); the two say the same words; make them agree"
  done <<< "$(awk 'index($0, "src=\"eng/readme/shots/") && match($0, /alt="[^"]*"/) { print NR "\t" substr($0, RSTART + 5, RLENGTH - 6) }' "$readme")"
  last=$(awk 'index($0, "eng/readme/shots/") { n = NR } END { print n + 0 }' "$readme")
  [ "$last" -gt 0 ] || return 0
  # The caption: the first paragraph after the last line that names a screenshot, past the closing tags.
  caption=$(awk -v l="$last" 'NR <= l { next } !c && (/^[[:space:]]*$/ || /^[[:space:]]*</) { next } !c && /^#/ { exit } /^[[:space:]]*$/ { exit } { c = 1; print }' "$readme" | plain)
  at=$(awk -v l="$last" 'NR > l && NF && !/^[[:space:]]*</ { print NR; exit }' "$readme")
  if [ -z "$caption" ]; then
    report "$readme" "$last" "C8: read no caption paragraph under the screenshots of README.md (format changed?); teach scripts/check-readme-sync.sh"
  elif ! grep -qF -- "$caption" <<< "$page"; then
    report "$readme" "${at:-$last}" "C8: the caption under the screenshots (\"$caption\") is not on the landing page ($landing); the two say the same words; make them agree"
  fi
}

# --- C9: the index --------------------------------------------------------------------------------
check_index() {
  local f name
  need "$index" C9 || return 0
  for f in docs/*.md; do
    [ -e "$f" ] || continue
    name=$(basename "$f")
    if [ "$name" = INDEX.md ]; then continue; fi
    if ! grep -qF -- "($name)" "$index"; then
      report "$index" 1 "C9: $f is not listed in $index; add a '- [$name]($name) — …' line under Guides"
    fi
  done
}

# --- C10: the anchors -----------------------------------------------------------------------------

# headings FILE: "NR<TAB>level<TAB>slug" for each heading outside a fence, as GitHub slugs it (a
# link reduced to its text, lowercased, every byte above 0x7F and every character but letters,
# digits, space, - and _ dropped, spaces to hyphens, the nth repeat suffixed -n), then "NR<TAB>a<TAB>x"
# for each name="x" or id="x" of an <a> tag.
headings() {
  awk '
    function delink(t,   a, b, c, out) {
      out = ""
      while ((a = index(t, "[")) > 0) {
        b = index(substr(t, a + 1), "](")
        if (b == 0) break
        c = index(substr(t, a + b + 2), ")")
        if (c == 0) break
        out = out substr(t, 1, a - 1) substr(t, a + 1, b - 1)
        t = substr(t, a + b + 2 + c)
      }
      return out t
    }
    /^[[:space:]]*(```|~~~)/ { fence = !fence; next }
    fence { next }
    /^#+ / {
      level = $0; sub(/ .*/, "", level)
      t = $0; sub(/^#+ +/, "", t); sub(/[[:space:]]+#+[[:space:]]*$/, "", t); sub(/[[:space:]]+$/, "", t)
      s = tolower(delink(t))
      gsub(/[^a-z0-9 _-]/, "", s)
      gsub(/ /, "-", s)
      if (s in seen) { seen[s]++; s = s "-" seen[s] } else seen[s] = 0
      print NR "\t" length(level) "\t" s
    }
    {
      line = $0
      while (match(line, /<a [^>]*(name|id)="[^"]*"/)) {
        v = substr(line, RSTART, RLENGTH); sub(/.*(name|id)="/, "", v); sub(/"$/, "", v)
        print NR "\ta\t" v
        line = substr(line, RSTART + RLENGTH)
      }
    }
  ' "$1"
}

# link_targets FILE: "NR<TAB>target" for each ](target) outside a fence.
link_targets() {
  awk '
    /^[[:space:]]*(```|~~~)/ { fence = !fence; next }
    fence { next }
    {
      s = $0
      while ((i = index(s, "](")) > 0) {
        s = substr(s, i + 2)
        j = index(s, ")")
        if (j == 0) break
        print NR "\t" substr(s, 1, j - 1)
        s = substr(s, j + 1)
      }
    }
  ' "$1"
}

check_anchor() {  # FROM LINE TARGET-FILE ANCHOR
  local from="$1" at="$2" target="$3" anchor="$4"
  [ -f "$target" ] || return 0   # a missing file is the link check's (check-adr-links.sh, C7)
  case "$target" in
    *.md) ;;
    *) report "$from" "$at" "C10: the anchor #$anchor points into $target, which is not Markdown; drop the anchor or teach scripts/check-readme-sync.sh"; return 0 ;;
  esac
  if ! in_list "$anchor" "$(awk -F'\t' '{ print $3 }' <<< "$(headings "$target")")"; then
    report "$from" "$at" "C10: $target has no heading for #$anchor; the home is that heading; update the link"
  fi
}

check_anchors() {
  local at target path anchor url toc_line toc heads first missing slugs order file
  need "$readme" C10 || return 0
  while IFS=$'\t' read -r at target; do
    [ -n "$at" ] || continue
    case "$target" in *"#"*) ;; *) continue ;; esac
    target=${target%% *}
    case "$target" in *://* | mailto:*) continue ;; esac
    path=${target%%#*}
    anchor=${target#*#}
    [ -n "$path" ] || path="$readme"
    check_anchor "$readme" "$at" "$path" "$anchor"
  done <<< "$(link_targets "$readme")"
  if [ -n "$repo_url" ]; then
    for file in "$readme" "$pack_readme"; do
      [ -f "$file" ] || continue
      while IFS=$'\t' read -r at url; do
        [ -n "$at" ] || continue
        case "$url" in
          "$repo_url#"*) check_anchor "$file" "$at" "$readme" "${url#"$repo_url#"}" ;;
          "$repo_url/blob/main/"*"#"*)
            path=${url#"$repo_url/blob/main/"}
            check_anchor "$file" "$at" "${path%%#*}" "${path#*#}"
            ;;
        esac
      done <<< "$(urls "$file" "$repo_url")"
    done
  fi

  toc_line=$(awk '/^## Table of contents/ { print NR; exit }' "$readme")
  if [ -z "$toc_line" ]; then
    report "$readme" 1 "C10: read no '## Table of contents' heading from README.md (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  toc=$(awk -v t="$toc_line" 'NR <= t { next } /^## / { exit } { s = $0; while ((i = index(s, "](#")) > 0) { s = substr(s, i + 3); j = index(s, ")"); if (j == 0) break; print substr(s, 1, j - 1); s = substr(s, j + 1) } }' "$readme")
  heads=$(awk -F'\t' -v t="$toc_line" '$1 > t && ($2 == 2 || $2 == 3) { print $1 "\t" $3 }' <<< "$(headings "$readme")")
  missing=0
  while IFS=$'\t' read -r at anchor; do
    [ -n "$at" ] || continue
    if ! in_list "$anchor" "$toc"; then
      report "$readme" "$at" "C10: this heading (#$anchor) has no entry in the table of contents of README.md; add one"
      missing=1
    fi
  done <<< "$heads"
  [ "$missing" = 0 ] || return 0
  # The entries that name a heading, in the order the table lists them, against the headings' order.
  slugs=$(awk -F'\t' '{ print $2 }' <<< "$heads")
  order=$(H="$slugs" awk 'BEGIN { n = split(ENVIRON["H"], h, "\n"); for (i = 1; i <= n; i++) want[h[i]] = 1 } ($0 in want) { print }' <<< "$toc")
  first=$(H="$slugs" O="$order" awk 'BEGIN { n = split(ENVIRON["H"], h, "\n"); split(ENVIRON["O"], o, "\n"); for (i = 1; i <= n; i++) if (h[i] != o[i]) { print h[i]; exit } }')
  if [ -n "$first" ]; then
    report "$readme" "$toc_line" "C10: the table of contents of README.md is out of order at #$first; list the headings in the order they come"
  fi
}

# --- C11: the prerequisites -----------------------------------------------------------------------
check_prerequisites() {
  local home bullets cells at name cell ok
  need "$development" C11 || return 0
  home=$(awk '/^## Prerequisites[[:space:]]*$/ { s = 1; next } s && /^## / { exit } s && /^[|] [*][*]/ { n = $0; sub(/^[|] [*][*]/, "", n); sub(/[*][*].*/, "", n); print n }' "$development")
  if [ -z "$home" ]; then
    report "$development" 1 "C11: read no tools ('| **Name**' rows under '## Prerequisites') from $development (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  if need "$readme" C11; then
    bullets=$(awk '/^### Before you start[[:space:]]*$/ { s = 1; next } s && /^#/ { exit } s && /^- [*][*]/ { n = $0; sub(/^- [*][*]/, "", n); sub(/[*][*].*/, "", n); print NR "\t" n }' "$readme")
    at=$(line_of "$readme" '### Before you start')
    if [ -z "$bullets" ]; then
      report "$readme" "${at:-1}" "C11: read no '- **Tool**' bullets under '### Before you start' in README.md (format changed?); teach scripts/check-readme-sync.sh"
    else
      while IFS=$'\t' read -r at name; do
        [ -n "$at" ] || continue
        in_list "$name" "$home" || report "$readme" "$at" "C11: **$name** is no tool of the prerequisites table in $development; the home is that table; update the copy"
      done <<< "$bullets"
      while IFS= read -r name; do
        [ -n "$name" ] || continue
        in_list "$name" "$(awk -F'\t' '{ print $2 }' <<< "$bullets")" ||
          report "$readme" "$(line_of "$readme" '### Before you start')" "C11: 'Before you start' has no bullet for **$name**, a tool of the prerequisites table in $development; add it"
      done <<< "$home"
    fi
  fi
  if need "$install_page" C11; then
    cells=$(awk '/^## Prerequisites[[:space:]]*$/ { s = 1; next } s && /^## / { exit } s && /^[|]/ { rows++; if (rows == 1 || $0 ~ /^[|][-:| ]*$/) next; c = $0; sub(/^[|][[:space:]]*/, "", c); sub(/[[:space:]]*[|].*/, "", c); print NR "\t" c }' "$install_page")
    if [ -z "$cells" ]; then
      report "$install_page" 1 "C11: read no prerequisites table under '## Prerequisites' in $install_page (format changed?); teach scripts/check-readme-sync.sh"
      return 0
    fi
    while IFS=$'\t' read -r at cell; do
      [ -n "$at" ] || continue
      ok=""
      while IFS= read -r name; do
        case "$cell" in "$name"*) ok=1 ;; esac
      done <<< "$home"
      [ -n "$ok" ] || report "$install_page" "$at" "C11: '$cell' opens with no tool name of the prerequisites table in $development; start the cell with the name as that table has it"
    done <<< "$cells"
    while IFS= read -r name; do
      [ -n "$name" ] || continue
      ok=""
      while IFS=$'\t' read -r at cell; do
        case "$cell" in "$name"*) ok=1 ;; esac
      done <<< "$cells"
      [ -n "$ok" ] || report "$install_page" 1 "C11: the Install page's prerequisites table has no row for $name, a tool of the prerequisites table in $development; add one"
    done <<< "$home"
  fi
}

# --- C12: the ports -------------------------------------------------------------------------------
check_ports() {
  local homes file at n
  need "$readme" C12 || return 0
  homes=""
  while IFS= read -r file; do
    [ -n "$file" ] || continue
    if need "$file" C12; then
      homes="$homes
$(grep -F '"applicationUrl"' "$file" | grep -oE '(localhost|0[.]0[.]0[.]0):[0-9]+' | sed 's/.*://' || true)"
    fi
  done <<< "$launch_settings"
  if need "$compose" C12; then
    homes="$homes
$(grep -oE '[$][{][A-Za-z0-9_]+:-[0-9]+[}]:' "$compose" | sed -e 's/.*:-//' -e 's/[}]://' || true)"
  fi
  while IFS=$'\t' read -r at n; do
    [ -n "$at" ] || continue
    in_list "$n" "$homes" || report "$readme" "$at" "C12: port $n is in no applicationUrl of a launchSettings.json and is no default port of $compose; the home is the launch settings or the compose file; update the copy"
  done <<< "$(awk '{ s = $0; while (match(s, /(localhost:|host port )[0-9]+/)) { v = substr(s, RSTART, RLENGTH); sub(/^[^0-9]*/, "", v); print NR "\t" v; s = substr(s, RSTART + RLENGTH) } }' "$readme")"
}

# --- C13: the agent toolkit -----------------------------------------------------------------------

# excluded PATH: true when an unconditional exclude glob of template.json covers PATH.
excluded() {
  [ -f "$template" ] || return 1
  F="$1" awk '
    function re(g,   out, i, c) {
      out = "^"
      for (i = 1; i <= length(g); i++) {
        c = substr(g, i, 1)
        if (c == "*" && substr(g, i + 1, 1) == "*") { out = out ".*"; i++ }
        else if (c == "*") out = out "[^/]*"
        else if (c == "?") out = out "[^/]"
        else if (index(".+^$(){}|[]\\", c) > 0) out = out "\\" c
        else out = out c
      }
      return out "$"
    }
    /^      "exclude": \[/ { e = 1; next }
    e && /^      \]/ { exit }
    e { g = $0; sub(/^[[:space:]]*"/, "", g); sub(/",?[[:space:]]*$/, "", g); if (ENVIRON["F"] ~ re(g)) { hit = 1; exit } }
    END { exit hit ? 0 : 1 }
  ' "$template"
}

check_agents() {
  local section f name servers bullet list
  need "$readme" C13 || return 0
  if [ -f "$template" ] && ! grep -q '^      "exclude": \[' "$template"; then
    report "$template" 1 "C13: read no exclude list ('      \"exclude\": [') from $template (format changed?), so every skill and reviewer counts as shipped; teach scripts/check-readme-sync.sh"
  fi
  section=$(awk '/^## Built for AI agents/ { s = 1; next } s && /^## / { exit } s' "$readme")
  if [ -z "$section" ]; then
    report "$readme" 1 "C13: read no '## Built for AI agents' section from README.md (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  local at
  at=$(line_of "$readme" '## Built for AI agents')
  for f in .claude/skills/*/SKILL.md; do
    [ -e "$f" ] || continue
    if excluded "$f"; then continue; fi
    name=${f#.claude/skills/}
    name=${name%/SKILL.md}
    grep -qF -- "($f)" <<< "$section" ||
      report "$readme" "$at" "C13: 'Built for AI agents' does not link the skill $name ($f); the home is the skill's folder; add the link"
  done
  for f in .claude/agents/*.md; do
    [ -e "$f" ] || continue
    if excluded "$f"; then continue; fi
    name=$(basename "$f" .md)
    grep -qF -- "($f)" <<< "$section" ||
      report "$readme" "$at" "C13: 'Built for AI agents' does not link the reviewer $name ($f); the home is the reviewer's file; add the link"
  done
  need .mcp.json C13 || return 0
  servers=$(awk '/"mcpServers": [{]/ { s = 1; next } s && /^    "[^"]*": [{]/ { k = $0; sub(/^    "/, "", k); sub(/".*/, "", k); print k }' .mcp.json)
  if [ -z "$servers" ]; then
    report .mcp.json 1 "C13: read no servers under \"mcpServers\" from .mcp.json (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  # The servers are named in the bullet that links .mcp.json, in the words before "MCP server", code spans
  # aside: elsewhere in the section a name can stand for something else (the Playwright MCP of a
  # reviewer, `aspire run`).
  bullet=$(awk 'index($0, "(.mcp.json)") && !b { b = 1; print; next } b && (/^- / || /^[[:space:]]*$/ || /^#/) { exit } b { print }' <<< "$section")
  if [ -z "$bullet" ]; then
    report "$readme" "$at" "C13: 'Built for AI agents' has no bullet that links (.mcp.json) and names its servers; add one"
    return 0
  fi
  list=$(tr '\n' ' ' <<< "$bullet" | sed 's/`[^`]*`//g' | awk '{ i = index(tolower($0), "mcp server"); if (i) print substr($0, 1, i - 1) }')
  if [ -z "$list" ]; then
    report "$readme" "$(line_of "$readme" '(.mcp.json)')" "C13: read no 'MCP server' in the bullet that links .mcp.json (format changed?); name the servers before those words, or teach scripts/check-readme-sync.sh"
    return 0
  fi
  while IFS= read -r name; do
    [ -n "$name" ] || continue
    grep -qiwF -- "$(sed 's/-/ /g' <<< "$name")" <<< "$list" ||
      report "$readme" "$(line_of "$readme" '(.mcp.json)')" "C13: the bullet that links .mcp.json does not name the MCP server $name before the words 'MCP server'; the home is .mcp.json; name it"
  done <<< "$servers"
}

# --- C14: the walkthrough's step count ------------------------------------------------------------
check_steps() {
  local count file at word n
  need "$invoices_doc" C14 || return 0
  count=$(awk '/^## Try it[[:space:]]*$/ { s = 1; next } s && /^## / { exit } s && /^[|] [0-9]+ [|]/ { n++ } END { print n + 0 }' "$invoices_doc")
  if [ "$count" = 0 ]; then
    report "$invoices_doc" 1 "C14: read no '| <n> |' rows under '## Try it' from $invoices_doc (format changed?); teach scripts/check-readme-sync.sh"
    return 0
  fi
  for file in "$landing" "$install_page"; do
    [ -f "$file" ] || continue
    while IFS=$'\t' read -r at word; do
      [ -n "$at" ] || continue
      n=$(awk -v w="$word" 'BEGIN { split("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty", a, " "); for (i = 1; i <= 20; i++) if (a[i] == w) print i }')
      [ -n "$n" ] || continue
      if [ "$n" != "$count" ]; then
        report "$file" "$at" "C14: '$word' steps, and the Try it table of $invoices_doc has $count rows; the home is that table; update the copy"
      fi
    done <<< "$(awk '{ s = $0; while (match(s, /[A-Za-z]+(-step| requests)/)) { v = substr(s, RSTART, RLENGTH); sub(/(-step| requests)$/, "", v); print NR "\t" tolower(v); s = substr(s, RSTART + RLENGTH) } }' "$file")"
  done
}

# --- C15: the options of the overview ------------------------------------------------------------

# overview_bullets: "NR<US>flag<US>condition<US>closed" for each bullet of "## With an option": its
# `--flag` (empty when it opens with none), the condition of an <!--#if (...) --> on the line right above
# it (empty when there is none), and whether an <!--#endif --> is the line right after it.
overview_bullets() {
  awk '
    function flush(next_line) {
      if (open_b) print bnr "\037" flag "\037" cond "\037" (next_line ~ /^<!--#endif -->[[:space:]]*$/ ? "closed" : "open")
      open_b = 0
    }
    /^## With an option[[:space:]]*$/ { s = 1; next }
    s && /^## / { flush(""); exit }
    !s { next }
    open_b && /^  [^ ]/ { next }
    {
      flush($0)
      if ($0 ~ /^- /) {
        flag = ""
        if ($0 ~ /^- `--[a-z][a-z0-9-]*`:/) { flag = $0; sub(/^- `/, "", flag); sub(/`.*/, "", flag) }
        cond = prev
        if (cond ~ /^<!--#if [(].*[)] -->[[:space:]]*$/) { sub(/^<!--#if [(]/, "", cond); sub(/[)] -->[[:space:]]*$/, "", cond) } else cond = ""
        open_b = 1; bnr = NR
      }
      prev = $0
    }
    END { flush("") }
  ' "$overview"
}

check_overview_options() {
  local at bullets name flag line cond closed toggle_flags
  need "$overview" C15 || return 0
  [ -n "$params" ] || return 0   # check_options has said why
  at=$(line_of "$overview" '## With an option')
  if [ -z "$at" ]; then
    report "$overview" 1 "C15: read no '## With an option' section from $overview (format changed?); it lists what each option adds"
    return 0
  fi
  bullets=$(overview_bullets)
  toggle_flags=""
  while IFS="$us" read -r name _; do
    [ -n "$name" ] && [ "$name" != skipRestore ] || continue
    flag=$(long_name "$name")
    [ -n "$flag" ] || continue   # check_options has said so
    flag="--$flag"
    toggle_flags="$toggle_flags
$flag$us$name"
    awk -F"$us" -v f="$flag" '$2 == f { x = 1 } END { exit x ? 0 : 1 }' <<< "$bullets" ||
      report "$overview" "$at" "C15: 'With an option' in $overview has no bullet for $flag ($name); add '- \`$flag\`: ...' alone in a <!--#if ($name) --> block (the add-toggle skill)"
  done <<< "$params"
  while IFS="$us" read -r line flag cond closed; do
    [ -n "$line" ] || continue
    if [ -z "$flag" ]; then
      report "$overview" "$line" "C15: this bullet of 'With an option' opens with no \`--flag\` and a colon; each bullet there is one option's"
      continue
    fi
    name=$(awk -F"$us" -v f="$flag" '$1 == f { print $2; exit }' <<< "$toggle_flags")
    if [ -z "$name" ]; then
      report "$overview" "$line" "C15: \`$flag\` is no option of template.json (its flag in $cli_host); the home is template.json; update the copy"
    elif [ -z "$cond" ] || [ "$closed" != closed ]; then
      report "$overview" "$line" "C15: the bullet for $flag is not alone in an #if block; put <!--#if ($name) --> on the line above it and <!--#endif --> on the line below"
    elif ! grep -qwF -- "$name" <<< "$cond"; then
      report "$overview" "$line" "C15: the bullet for $flag sits in a block on ($cond), which does not name $name; a solution without the option would list it"
    fi
  done <<< "$bullets"
}

run_checks() {
  check_pitch
  check_pitch_copies
  check_options
  check_commands
  check_use_cases
  check_login
  check_gate_numbers
  check_links
  check_pictures
  check_index
  check_anchors
  check_prerequisites
  check_ports
  check_agents
  check_steps
  check_overview_options
  if [ "$fail" -eq 0 ]; then
    echo "readme-sync check passed — the pitch, the options, the commands, the use cases, the seeded login, the gate numbers, the prerequisites, the ports, the agent toolkit, the links and their anchors, the pictures and their words, the index and the overview's options agree with their sources."
  fi
  return "$fail"
}

# --- the self-test --------------------------------------------------------------------------------

# put PATH: writes stdin to PATH under the self-test's tree, making its folder.
put() { mkdir -p "$(dirname "$tree/$1")"; cat > "$tree/$1"; }
# edit PATH SED-SCRIPT: rewrites PATH under the tree with sed (no -i: BSD and GNU sed differ). An edit
# that changes nothing fails the next case: a plant that missed its line would prove nothing.
edit() {
  sed -e "$2" "$tree/$1" > "$tree/$1.new"
  if cmp -s "$tree/$1" "$tree/$1.new"; then
    echo "self-test: the edit '$2' of $1 changed nothing"
    unchanged=1
  fi
  mv "$tree/$1.new" "$tree/$1"
}

# seed: a tree in which every comparison agrees.
seed() {
  rm -rf "$tree"
  mkdir -p "$tree/scripts"
  cp "$self" "$tree/scripts/check-readme-sync.sh"
  put eng/readme/pitch.md <<'EOF'
Sample is a dotnet new template for .NET 10 with *Clean Architecture the tests enforce*.
EOF
  put eng/readme/render-diagrams.py <<'EOF'
PITCH_FILE = "eng/readme/pitch.md"
PROJECT_PLACEHOLDER = "Your.Project"
EOF
  : > "$tree/eng/readme/title-light.svg"
  : > "$tree/eng/readme/title-dark.svg"
  mkdir -p "$tree/eng/readme/shots"
  : > "$tree/eng/readme/shots/list.png"
  : > "$tree/eng/readme/shots/phone.png"
  put README.md <<'EOF'
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="eng/readme/title-dark.svg">
  <img alt="Sample: Clean Architecture the tests enforce, beside a terminal" src="eng/readme/title-light.svg" width="100%">
</picture>

```bash
dotnet new install Sample.Templates
dotnet new sample -n Your.Project
```

[![CI](https://github.com/example/Sample/actions/workflows/ci.yml/badge.svg)](https://github.com/example/Sample/actions/workflows/ci.yml)

Documentation: https://example.github.io/Sample/

**Sample** is a `dotnet new` template for .NET 10 with
*Clean Architecture the tests enforce*.

| Option | Default | What it does |
|---|---|---|
| [`--include-ui`](https://example.github.io/Sample/start/options/#--include-ui) | on | Include the UI. |
| [`--use-thing`](https://example.github.io/Sample/start/options/#--use-thing) | off | Add a "thing". |
| [`--skip-restore`](https://example.github.io/Sample/start/options/#--skip-restore) | off | Skip the restore. |

## Table of contents 🧭

- [Getting started 🚀](#getting-started-)
  - [Before you start](#before-you-start)
  - [Install, update, uninstall](#install-update-uninstall)
- [Use cases](#use-cases)
- [Built for AI agents 🤖](#built-for-ai-agents-)
- [Credits](#credits)

## Getting started 🚀

### Before you start

- **Docker**: the database needs it.
- **Aspire CLI** for `aspire run`.

### Install, update, uninstall

```bash
dotnet new install Sample.Templates     # the latest release
dotnet new update --check-only
dotnet new update
dotnet new uninstall Sample.Templates
git clone https://github.com/example/Sample.git
dotnet new install ./Sample
dotnet new sample -n Acme.Shop -o Acme.Shop
```

Sign in as `admin@example.test` / `Pass123!`. The dashboard is at <https://localhost:17000>, the API at
<http://localhost:5171>, and PostgreSQL on host port 5433. See [the guide](docs/guide.md#a-section),
[the comparison](https://example.github.io/Sample/compare/), the
[guide on GitHub](https://github.com/example/Sample/blob/main/docs/guide.md#a-section) and
[the credits](https://github.com/example/Sample#credits).

<img alt="The list" src="eng/readme/shots/list.png" width="100%">

The pictures show a first run, with `--use-thing` off. `GET /api/v1/things/{id}` reads one.

## Use cases

| Use case | Request | Permission |
|---|---|---|
| **CreateThing**: make one | `POST /api/v1/things`, with a key | `things:write` |
| **GetThing**: read one | `GET /api/v1/things/{id}?view=full` | `things:read` |

| Use case | Request | Who may call it |
|---|---|---|
| **Login**: sign in | `POST /api/v1/auth/login` | anyone |

## Built for AI agents 🤖

- [`run-app`](.claude/skills/run-app/SKILL.md) and [`ui-reviewer`](.claude/agents/ui-reviewer.md), which
  look things up through Context Seven.
- **[.mcp.json](.mcp.json)** wires up the Context Seven MCP server, for `context seven` lookups.

<a name="credits"></a>

## Credits
EOF
  put eng/Trellis.TemplatePack/README.md <<'EOF'
# Sample.Templates

Sample is a dotnet new template for .NET 10 with Clean Architecture the tests enforce.

```bash
dotnet new install Sample.Templates
dotnet new sample -n Your.Project
```

| Option | Default | What it does |
|---|---|---|
| [`--include-ui`](https://example.github.io/Sample/start/options/#--include-ui) | on | Include the UI. |
| [`--use-thing`](https://example.github.io/Sample/start/options/#--use-thing) | off | Add a "thing". |
| [`--skip-restore`](https://example.github.io/Sample/start/options/#--skip-restore) | off | Skip the restore. |

Credits: <https://github.com/example/Sample#credits>.
EOF
  put eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <Description>Sample is a dotnet new template for .NET 10 with Clean Architecture the tests enforce.</Description>
    <PackageProjectUrl>https://example.github.io/Sample/</PackageProjectUrl>
    <RepositoryUrl>https://github.com/example/Sample</RepositoryUrl>
  </PropertyGroup>
</Project>
EOF
  put .template.config/template.json <<'EOF'
{
  "shortName": "sample",
  "description": "Sample is a dotnet new template for .NET 10 with Clean Architecture the tests enforce.",
  "symbols": {
    "IncludeUI": {
      "type": "parameter",
      "datatype": "bool",
      "defaultValue": "true",
      "displayName": "Include UI",
      "description": "Include the UI."
    },
    "skipRestore": {
      "type": "parameter",
      "datatype": "bool",
      "defaultValue": "false",
      "displayName": "Skip restore",
      "description": "Skip the restore."
    },
    "UseThing": {
      "type": "parameter",
      "datatype": "bool",
      "defaultValue": "false",
      "displayName": "Use a thing",
      "description": "Add a \"thing\"."
    },
    "templatePackageId": {
      "type": "generated",
      "generator": "constant",
      "parameters": {
        "value": "Sample.Templates"
      },
      "description": "The package id."
    }
  },
  "sources": [
    {
      "exclude": [
        "site/**",
        ".claude/skills/maint-only/**"
      ],
      "modifiers": [
        {
          "condition": "(!IncludeUI)",
          "exclude": [
            ".claude/agents/ui-reviewer.md"
          ]
        }
      ]
    }
  ],
  "postActions": [
    {
      "text": "Seeded admin: admin@example.test / Pass123!"
    }
  ]
}
EOF
  put .template.config/dotnetcli.host.json <<'EOF'
{
  "symbolInfo": {
    "IncludeUI": {
      "longName": "include-ui"
    },
    "skipRestore": {
      "longName": "skip-restore"
    },
    "UseThing": {
      "longName": "use-thing"
    }
  }
}
EOF
  put .template.config/ide.host.json <<'EOF'
{
  "symbolInfo": [
    {
      "id": "IncludeUI",
      "name": {
        "text": "Include UI"
      }
    },
    {
      "id": "skipRestore",
      "name": {
        "text": "Skip restore"
      }
    },
    {
      "id": "UseThing",
      "name": {
        "text": "Use a thing"
      }
    }
  ]
}
EOF
  put .template.config/consumer/README.md <<'EOF'
Sign in as admin@example.test / Pass123!.
EOF
  put site/trellis.config.mjs <<'EOF'
export const site = 'https://example.github.io';
export const base = '/Sample';
const pitchFile = 'eng/readme/pitch.md';
EOF
  put site/astro.config.mjs <<'EOF'
export default {
    description: pitch,
};
EOF
  put site/src/content/docs/index.mdx <<'EOF'
---
title: Sample
head:
  - tag: title
    content: Sample — Clean Architecture the tests enforce
hero:
  title: 'Clean Architecture <em>the tests enforce.</em>'
---

import phone from '@repo/eng/readme/shots/phone.png';

Walk the three-step tour.

<p class="sub">The pictures show a first run, with `--use-thing` off. `GET /api/v1/things/{id}` reads one.</p>
<img alt="The list" src={phone.src} />
EOF
  put site/src/content/docs/start/install.mdx <<'EOF'
---
title: Install
---

## Prerequisites

| Tool | Needed for | Check it |
|---|---|---|
| Docker, any engine | everything | `docker version` |
| Aspire CLI 13.5 or later, optional | `aspire run` | `aspire --version` |

## Install

The lifecycle in three requests.
EOF
  put site/src/content/docs/start/options.mdx <<'EOF'
---
title: Options
---
EOF
  put site/src/content/docs/compare.mdx <<'EOF'
---
title: Compared
---
EOF
  put site/src/data/repo.mjs <<'EOF'
// readme-sync:quickstart
export const quickstart = {
  install: `dotnet new install ${packageId}`,
  create: `dotnet new ${templateShortName} -n Acme.Shop -o Acme.Shop`,
  update: [`dotnet new update --check-only`, `dotnet new update`],
  uninstall: `dotnet new uninstall ${packageId}`,
  fromClone: ['git clone https://github.com/example/Sample.git', 'dotnet new install ./Sample', `dotnet new ${templateShortName} -n Acme.Shop -o Acme.Shop`],
};
// readme-sync:end
EOF
  put docs/api/openapi.v1.json <<'EOF'
{
  "paths": {
    "/api/v1/things": {
      "post": {
        "operationId": "CreateThing"
      }
    },
    "/api/v1/things/{id}": {
      "get": {
        "operationId": "GetThing"
      }
    },
    "/api/v1/auth/login": {
      "post": {
        "operationId": "Login"
      }
    }
  }
}
EOF
  mkdir -p "$tree/src/Core/Trellis.Application/Features/Things/CreateThing" \
    "$tree/src/Core/Trellis.Application/Features/Things/GetThing" \
    "$tree/src/Core/Trellis.Application/Features/Auth/Login"
  put tests/Trellis.ApiTests/InProcess/EndpointMetadataTests.cs <<'EOF'
    private static readonly Dictionary<string, string> ExpectedPermissions = new(StringComparer.Ordinal)
    {
        ["POST things"] = Permissions.Things.Write,
        ["GET things/{id}"] = Permissions.Things.Read,
    };
EOF
  put src/Core/Trellis.Application/Authorization/Permissions.cs <<'EOF'
public static class Permissions
{
    public static class Things
    {
        public const string Read = "things:read";
        public const string Write = "things:write";
    }
}
EOF
  put tools/Trellis.MigrationService/SeedOptions.cs <<'EOF'
    public const string DefaultAdminPassword = "Pass123!";
    public string AdminEmail { get; init; } = "admin@example.test";
EOF
  put tools/Trellis.AppHost/Properties/launchSettings.json <<'EOF'
{ "applicationUrl": "https://localhost:17000" }
EOF
  put src/Presentation/Api/Trellis.Api/Properties/launchSettings.json <<'EOF'
{ "applicationUrl": "http://localhost:5171" }
EOF
  put src/Presentation/UI/Trellis.UI.Web/Properties/launchSettings.json <<'EOF'
{
  "applicationUrl": "http://0.0.0.0:5170",
  "Services__api__http__0": "http://localhost:5171"
}
EOF
  put docker-compose.yml <<'EOF'
    ports:
      - "${POSTGRES_PORT:-5433}:5432"
EOF
  put AGENTS.md <<'EOF'
Seeded admin: `admin@example.test` / `Pass123!`.
EOF
  put docs/INDEX.md <<'EOF'
- [guide.md](guide.md)
- [development.md](development.md)
- [deployment.md](deployment.md)
- [overview.md](overview.md)
EOF
  put docs/overview.md <<'EOF'
# What you get

## With an option

Each option this solution has is listed here.

<!--#if (IncludeUI) -->
- `--include-ui`: the UI.
<!--#endif -->
<!--#if (UseThing) -->
- `--use-thing`: a thing,
  on two lines.
<!--#endif -->

## Make it yours
EOF
  put docs/guide.md <<'EOF'
# Guide

## A section
EOF
  put docs/deployment.md <<'EOF'
The seeded admin, admin@example.test, takes its password from the environment.
EOF
  put docs/development.md <<'EOF'
# Development

## Prerequisites

| Tool | Needed for | Minimum |
|---|---|---|
| **Docker** (any engine) | everything | a running engine |
| **Aspire CLI**: `dotnet tool install -g Aspire.Cli` | `aspire run` | 13.5 |

Seeded admin: `admin@example.test` / `Pass123!`.

## Tests

### Gates beyond the tests

- Line coverage has a floor per ring: Core 90 %, Shared 80 %.

## Agents

The aspire server needs the Aspire CLI 13.5 or later.
EOF
  put docs/features/auth.md <<'EOF'
Seeded admin: admin@example.test / Pass123!.
EOF
  put docs/features/invoices.md <<'EOF'
Seeded admin: admin@example.test / Pass123!.

## Try it

| # | Request | Answer |
|---|---|---|
| 1 | sign in | 200 |
| 2 | create | 201 |
| 3 | read | 200 |

## Where the code lives
EOF
  put .github/workflows/ci.yml <<'EOF'
      - name: Coverage summary and ring floors
        run: coverage --min-line-rate src/Core=90 --min-line-rate src/Shared=80
      - name: Sign in
        run: login admin@example.test
EOF
  put scripts/doctor.sh <<'EOF'
aspire_floor=13.5 # docs/development.md, Prerequisites
EOF
  put .claude/skills/run-app/SKILL.md <<'EOF'
Sign in as admin@example.test / Pass123!.
EOF
  put .claude/skills/maint-only/SKILL.md <<'EOF'
A skill the template does not ship.
EOF
  put .claude/agents/ui-reviewer.md <<'EOF'
Sign in as admin@example.test / Pass123!.
EOF
  put .mcp.json <<'EOF'
{
  "mcpServers": {
    "context-seven": {
      "command": "npx"
    }
  }
}
EOF
}

self_test() {
  tree="$(mktemp -d)/tree"   # global: the EXIT trap reads it after this function returns
  trap 'rm -rf "$(dirname "$tree")"' EXIT
  self="$PWD/scripts/check-readme-sync.sh"
  local cases=0 failed=0

  # expect NAME pass|fail [TEXT [ABSENT]]: runs the tree's copy of the check; for a failure, TEXT must be
  # in its output, and ABSENT, when given, must not be. The tree is seeded again before the next case,
  # so every plant is undone; a case whose edit changed nothing fails.
  unchanged=0
  expect() {
    local name="$1" want="$2" text="${3:-}" absent="${4:-}" out got
    cases=$((cases + 1))
    if out="$(GITHUB_ACTIONS='' bash "$tree/scripts/check-readme-sync.sh" 2>&1)"; then got=pass; else got=fail; fi
    if [ "$unchanged" = 1 ]; then
      echo "self-test case '$name': an edit of its plant matched nothing, so the case proves nothing"
      failed=$((failed + 1))
    elif [ "$got" != "$want" ]; then
      echo "self-test case '$name': expected $want, got $got:"; printf '%s\n' "$out" | sed 's/^/  /'
      failed=$((failed + 1))
    elif [ -n "$text" ] && ! grep -qF -- "$text" <<< "$out"; then
      echo "self-test case '$name': the output does not say '$text':"; printf '%s\n' "$out" | sed 's/^/  /'
      failed=$((failed + 1))
    elif [ -n "$absent" ] && grep -qF -- "$absent" <<< "$out"; then
      echo "self-test case '$name': the output says '$absent', which it must not:"; printf '%s\n' "$out" | sed 's/^/  /'
      failed=$((failed + 1))
    fi
    unchanged=0
    seed
  }

  seed
  expect "the agreeing tree" pass "readme-sync check passed"

  printf '%s\n' 'A second sentence.' >> "$tree/eng/readme/pitch.md"
  expect "C1a: a second line in pitch.md" fail "C1a: the pitch is one line"
  edit eng/readme/pitch.md 's/for .NET 10/for .NET 10 \& more/'
  expect "C1a: a forbidden character in pitch.md" fail "C1a: the pitch holds one of"
  edit README.md 's/^\*\*Sample\*\* is a `dotnet new` template/**Sample** is the `dotnet new` template/'
  expect "C1b: a word of the README's pitch" fail "C1b: README.md does not contain the pitch"
  edit eng/Trellis.TemplatePack/README.md 's/^Sample is a dotnet new/Sample is the dotnet new/'
  expect "C1c: a word of the package README's first paragraph" fail "C1c: the package README's first paragraph"
  edit eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj 's/<Description>Sample is a/<Description>Sample, a/'
  expect "C1d: the csproj description" fail "C1d: <Description> is"
  edit .template.config/template.json 's/^  "description": "Sample is a/  "description": "Sample, a/'
  expect "C1e: template.json's description" fail "C1e: the template's description is"
  edit site/trellis.config.mjs '/pitch.md/d'
  expect "C1f: trellis.config.mjs does not read pitch.md" fail "C1f: site/trellis.config.mjs does not read"
  edit site/astro.config.mjs 's/description: pitch,/description: "Something else",/'
  expect "C1f: astro.config.mjs has no description: pitch," fail "C1f: site/astro.config.mjs has no line"
  edit site/src/content/docs/index.mdx 's/^title: Sample$/title: Sample\
description: A description of its own./'
  expect "C1f: index.mdx has a description of its own" fail "C1f: the landing page's frontmatter has a description"
  edit site/src/content/docs/index.mdx "s/^  title: 'Clean Architecture/  title: 'Clean Code/"
  expect "C1g: the hero title" fail "C1g: the hero title reads"
  edit site/src/content/docs/index.mdx 's/^    content: Sample — Clean Architecture the tests enforce/    content: Sample, a template/'
  expect "C1h: the head title" fail "C1h: the head title"
  edit README.md 's/alt="Sample: Clean Architecture the tests enforce,/alt="Sample, a template,/'
  expect "C1i: the alt text of the banner" fail "C1i: the alt text of the title picture"

  edit README.md 's/| off | Add a "thing". |/| off | Add a thing. |/'
  expect "C2: an option's description in the README" fail "C2: the description of --use-thing in README.md"
  edit README.md '/#--use-thing)/d'
  expect "C2: a boolean parameter without a row" fail "C2: the options table of README.md has 2 rows"
  edit README.md 's/#--include-ui) | on |/#--include-ui) | off |/'
  expect "C2: a flipped default" fail "C2: the default of --include-ui in README.md"
  edit eng/Trellis.TemplatePack/README.md 's/| on | Include the UI. |/| on | Include a UI. |/'
  expect "C2: the package README's table" fail "C2: the description of --include-ui in eng/Trellis.TemplatePack/README.md"
  edit .template.config/ide.host.json 's/"text": "Use a thing"/"text": "Use thing"/'
  expect "C2b: a name.text that differs" fail "C2b: the dialog label of UseThing"
  edit .template.config/template.json '/"displayName": "Use a thing",/d'
  expect "C2b: a parameter without a displayName" fail "C2b: the boolean parameter UseThing has no displayName" "C2: the description of --use-thing"
  edit .template.config/template.json '/"defaultValue": "false",/d'
  expect "C2: parameters without a defaultValue, read as off" pass "readme-sync check passed"
  edit README.md 's/with `--use-thing` off/with `--use-other` off/'
  expect "C2c: a flag in the README's prose that is no option" fail "C2c: \`--use-other\` is no flag"

  edit README.md 's/^dotnet new update$/dotnet new update\
dotnet new sample -n Other.Name/'
  expect "C3: an unknown dotnet new line in the README" fail "C3: 'dotnet new sample -n Other.Name' is no quickstart command"
  edit README.md '/^dotnet new update$/d'
  expect "C3: a removed update line" fail "C3: README.md shows no 'dotnet new update'"
  edit eng/Trellis.TemplatePack/README.md '/^dotnet new install Sample.Templates$/d'
  expect "C3: the package README's install line" fail "C3: eng/Trellis.TemplatePack/README.md shows no 'dotnet new install Sample.Templates'"
  edit site/src/data/repo.mjs '/^\/\/ readme-sync:quickstart$/d'
  expect "C3: a removed marker comment" fail "C3: read no '// readme-sync:quickstart' marker"
  edit site/src/data/repo.mjs 's/^  update: \[/  update: [\
    /'
  expect "C3: a quickstart value continued on the next line" fail "C3: this quickstart line holds what this script cannot read"
  edit site/src/data/repo.mjs 's/`dotnet new update`\]/"dotnet new update"]/'
  expect "C3: a double-quoted quickstart string" fail "C3: this quickstart line holds what this script cannot read"
  edit site/src/data/repo.mjs 's/^  uninstall:/  remove:/'
  edit README.md '/^dotnet new uninstall Sample.Templates$/d'
  expect "C3: a quickstart key renamed, its README line gone" fail "C3: read no uninstall string"
  edit eng/readme/render-diagrams.py 's/"Your.Project"/"My.Project"/'
  edit README.md 's/^dotnet new sample -n Your.Project$/dotnet new sample -n My.Project/'
  edit eng/Trellis.TemplatePack/README.md 's/^dotnet new sample -n Your.Project$/dotnet new sample -n My.Project/'
  expect "C3: the banner's placeholder renamed with its copies" pass "readme-sync check passed"

  edit README.md '/^| \*\*GetThing\*\*/d'
  expect "C4: a use-case row removed" fail "C4: the operation \`GET /api/v1/things/{id}\` of docs/api/openapi.v1.json has no row"
  edit README.md 's/^| \*\*Login\*\*: sign in | `POST \/api\/v1\/auth\/login` | anyone |$/&\
| **Login**: sign out | `POST \/api\/v1\/auth\/logout` | anyone |/'
  expect "C4: a row for an operation the snapshot lacks" fail "C4: \`POST /api/v1/auth/logout\` is no operation"
  edit README.md 's/^| \*\*CreateThing\*\*/| **MakeThing**/'
  expect "C4: a Name with no folder" fail "C4: **MakeThing** is no use-case folder"
  edit README.md 's/^## Use cases$/## Use cases\
\
| Ring | Projects |\
|---|---|\
| **Core** | Domain |/'
  expect "C4: a bold row without a request span" pass "readme-sync check passed"
  edit README.md 's/`things:read` |$/`things:write` |/'
  expect "C4b: a permission changed in the README" fail "C4b: 'GET things/{id}' needs \`things:read\`"
  edit README.md 's/^| \*\*Login\*\*: sign in/| Login: sign in/'
  expect "C4: a row without a Name" fail "C4: this use-case row opens with no **Name**"
  edit README.md 's/`GET \/api\/v1\/things\/{id}` reads one/`GET \/api\/v1\/bills\/{id}` reads one/'
  expect "C4: a request in the prose that is no operation" fail "C4: \`GET /api/v1/bills/{id}\` is no operation"
  edit README.md 's/| `POST \/api\/v1\/auth\/login` | anyone |/| `POST \/api\/v1\/auth\/login` | `auth:login` |/'
  expect "C4b: a permission for an endpoint the test does not list" fail "C4b: README.md says \`POST /api/v1/auth/login\` needs \`auth:login\`"
  edit src/Core/Trellis.Application/Authorization/Permissions.cs 's/"things:write"/"thing-items:write"/'
  edit README.md 's/`things:write`/`thing-items:write`/'
  expect "C4b: a permission with a hyphen" pass "readme-sync check passed"

  edit docs/features/auth.md 's/Pass123!/Pass456!/'
  expect "C5: the password in one listed copy" fail "C5: docs/features/auth.md does not hold the seeded password"
  put docs/extra.md <<'EOF'
Sign in as admin@example.test.
EOF
  printf '%s\n' '- [extra.md](extra.md)' >> "$tree/docs/INDEX.md"
  expect "C5: a new file that holds the e-mail" fail "C5: docs/extra.md states the seeded login and is not in login_copies"

  edit .github/workflows/ci.yml 's/src\/Core=90/src\/Core=85/'
  expect "C6a: a coverage floor in ci.yml" fail "C6a: 'Gates beyond the tests' does not say 'Core 85 %'"
  edit scripts/doctor.sh 's/^aspire_floor=13.5/aspire_floor=13.4/'
  expect "C6b: aspire_floor" fail "C6b: aspire_floor is 13.4"
  edit docs/development.md 's/the Aspire CLI 13.5 or later/the Aspire CLI 13.6 or later/'
  expect "C6b: an 'Aspire CLI <n> or later' in a doc" fail "C6b: 'Aspire CLI 13.6 or later'"

  edit README.md 's/(https:\/\/example.github.io\/Sample\/compare\/)/(https:\/\/example.github.io\/Sample\/develop\/guide\/)/'
  expect "C7: a README link to a site page with no file" fail "C7: https://example.github.io/Sample/develop/guide/ names a page the site has no file for"
  edit README.md 's/(https:\/\/example.github.io\/Sample\/compare\/)/(https:\/\/example.github.io\/Sample\/compare\/#a-row)/'
  expect "C7: an anchor on a page other than Options" fail "C7: the anchor #a-row of"
  edit README.md 's/blob\/main\/docs\/guide.md#a-section/blob\/main\/docs\/gone.md/'
  expect "C7: a blob/main path that does not exist" fail "C7: https://github.com/example/Sample/blob/main/docs/gone.md names docs/gone.md, which does not exist"
  edit README.md 's/actions\/workflows\/ci.yml)/actions\/workflows\/gone.yml)/'
  expect "C7: a badge for a workflow that does not exist" fail "names .github/workflows/gone.yml, which does not exist"
  edit README.md 's/blob\/main\/docs\/guide.md#a-section/blob\/v1\/docs\/guide.md/'
  expect "C7: a link to a ref other than main" fail "links a ref other than main"

  rm "$tree/eng/readme/title-dark.svg"
  expect "C8: a missing picture" fail "C8: the picture eng/readme/title-dark.svg does not exist"
  : > "$tree/eng/readme/shots/unused.png"
  expect "C8: an unused screenshot" fail "C8: eng/readme/shots/unused.png is used neither"
  edit README.md 's/<img alt="The list"/<img alt="The whole list"/'
  expect "C8: a screenshot's alt text the landing page does not have" fail "C8: the alt text \"The whole list\""
  edit site/src/content/docs/index.mdx 's/The pictures show a first run/The pictures show the first run/'
  expect "C8: a caption the landing page does not have" fail "C8: the caption under the screenshots"
  : > "$tree/docs/orphan.md"
  expect "C9: a guide missing from the index" fail "C9: docs/orphan.md is not listed"

  edit docs/guide.md 's/^## A section$/## Another section/'
  expect "C10: a heading renamed under a linked anchor" fail "C10: docs/guide.md has no heading for #a-section"
  edit README.md 's/^## Credits$/## Credits\
\
## License/'
  expect "C10: a heading missing from the table of contents" fail "C10: this heading (#license) has no entry"
  edit README.md '/^  - \[Before you start\](#before-you-start)$/d'
  edit README.md 's/^  - \[Install, update, uninstall\](#install-update-uninstall)$/&\
  - [Before you start](#before-you-start)/'
  expect "C10: the table of contents out of order" fail "C10: the table of contents of README.md is out of order at #before-you-start"

  edit README.md 's/^- \*\*Aspire CLI\*\* for/- **Aspire** for/'
  expect "C11: a tool renamed in the README's bullets" fail "C11: **Aspire** is no tool of the prerequisites table"
  edit site/src/content/docs/start/install.mdx 's/^| Docker, any engine/| Podman, any engine/'
  expect "C11: a row of the Install page that names no tool" fail "C11: 'Podman, any engine' opens with no tool name"
  edit README.md 's/localhost:5171/localhost:5172/'
  expect "C12: a port changed in the README" fail "C12: port 5172 is in no applicationUrl"
  edit src/Presentation/Api/Trellis.Api/Properties/launchSettings.json 's/localhost:5171/localhost:5181/'
  expect "C12: a port only another project's environment still names" fail "C12: port 5171 is in no applicationUrl"

  edit README.md 's/\[`run-app`\](.claude\/skills\/run-app\/SKILL.md) and //'
  expect "C13: a skill without a link" fail "C13: 'Built for AI agents' does not link the skill run-app"
  edit README.md 's/the Context Seven MCP server/the MCP servers/'
  expect "C13: a server named elsewhere in the section only" fail "C13: the bullet that links .mcp.json does not name the MCP server context-seven"
  edit README.md 's/the Context Seven MCP server, for `context seven` lookups/the `context seven` MCP server/'
  expect "C13: a server named in a code span only" fail "C13: the bullet that links .mcp.json does not name the MCP server context-seven"
  edit site/src/content/docs/index.mdx 's/three-step tour/four-step tour/'
  expect "C14: the step word" fail "C14: 'four' steps"

  edit docs/overview.md '/^<!--#if (UseThing) -->$/,/^<!--#endif -->$/d'
  expect "C15: an option without its bullet" fail "C15: 'With an option' in docs/overview.md has no bullet for --use-thing"
  edit docs/overview.md 's/^<!--#if (UseThing) -->$/<!--#if (IncludeUI) -->/'
  expect "C15: a bullet in a block that does not name its option" fail "C15: the bullet for --use-thing sits in a block on (IncludeUI)"
  edit docs/overview.md 's/^- `--use-thing`:/- `--use-other`:/'
  expect "C15: a bullet for a flag no option has" fail "C15: \`--use-other\` is no option"
  edit docs/overview.md '/^<!--#if (UseThing) -->$/d'
  expect "C15: a bullet outside an #if block" fail "C15: the bullet for --use-thing is not alone in an #if block"

  edit .template.config/template.json 's/^    "\([A-Za-z]*\)": {$/  "\1": {/'
  expect "template.json re-indented, so no symbol is read" fail "C2: read no boolean parameters from .template.config/template.json"

  # Under GitHub Actions a failure is an annotation; a bad argument is the usage and exit 2.
  local out code
  cases=$((cases + 1))
  edit docs/features/auth.md 's/Pass123!/Pass456!/'
  out="$(GITHUB_ACTIONS=true bash "$tree/scripts/check-readme-sync.sh" 2>&1)" && code=0 || code=$?
  if [ "$code" != 1 ] || ! grep -qF '::error file=docs/features/auth.md,line=' <<< "$out"; then
    echo "self-test case 'an annotation under GitHub Actions': exit $code:"; printf '%s\n' "$out" | sed 's/^/  /'
    failed=$((failed + 1))
  fi
  seed
  cases=$((cases + 1))
  out="$(bash "$tree/scripts/check-readme-sync.sh" --bogus 2>&1)" && code=0 || code=$?
  if [ "$code" != 2 ] || ! grep -qF 'usage: scripts/check-readme-sync.sh [--self-test]' <<< "$out"; then
    echo "self-test case 'a bad argument': exit $code:"; printf '%s\n' "$out" | sed 's/^/  /'
    failed=$((failed + 1))
  fi

  if [ "$failed" -eq 0 ]; then
    echo "readme-sync self-test passed: $cases cases"
    return 0
  fi
  echo "readme-sync self-test failed: $failed of $cases"
  return 1
}

case "${1:-}" in
  "") run_checks ;;
  --self-test) self_test ;;
  *) usage ;;
esac
