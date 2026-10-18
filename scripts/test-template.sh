#!/usr/bin/env bash
# Runs Template CI's legs on this machine before the push: each leg of the instantiate job in
# .github/workflows/template-ci.yml, instantiated from the packed template rather than the folder.
# Maintainer-only: template.json and the pack csproj exclude it from generated projects.
#
#   scripts/test-template.sh                       every leg of the matrix (a few minutes a leg)
#   scripts/test-template.sh default all-options   the named legs only
#   scripts/test-template.sh --list                the legs and their flags
#   scripts/test-template.sh --keep [leg...]       keep the working folder even when every leg passes
#
# It runs scripts/check-template-toggles.sh, packs the template once (the working tree as it is,
# untracked files included), installs the package into a private hive (--debug:custom-hive), so your
# own template list is never touched, and then for each leg: instantiates it with the leg's flags,
# regenerates the initial migration where the leg says so, builds Release and, on the legs Template
# CI tests, runs dotnet test. A table of results follows, and the exit code is 1 when anything
# failed. The working folder (under $TMPDIR) keeps every log and generated solution after a failure.
#
# Because it packs, the legs get the package's content, as an install from nuget.org does and as the
# last step of Template CI's pack job checks; the instantiate job installs the source folder instead.
# The pack csproj's TemplateContentExclude decides what the package holds, template.json's
# sources[0].exclude what a folder install holds, and the two lists are kept in step by hand: a file
# missing from one of them ships by one route and not by the other.
#
# The generated tests include the Aspire E2E, which boots each generated AppHost as Template CI does
# on its Linux runners; RUN_ASPIRE_E2E=0 skips it. The browser tests stay off, as in CI. Needs a .NET
# SDK that satisfies global.json, a running Docker (Testcontainers, the E2E), and jq and git (for the
# toggle check). Template CI's content gates (leftovers of the template's name, the consumer files,
# actionlint over the generated workflows) and its Windows job run only in CI.
#
# KEEP IN SYNC with .github/workflows/template-ci.yml. The legs and their flags are read from its
# instantiate matrix (keys name, args, out, test and regenerate-migration, one per line, args in
# double quotes), so a leg added there runs here with no edit, and a key this script does not know,
# in a leg or beside include (an exclude list, a dimension such as os), stops it until it learns
# what the key means. The commands in run_leg mirror that job's steps.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

workflow=.github/workflows/template-ci.yml

usage() {
  sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'
}

# One record per leg of the instantiate job's matrix: LEG|name|out|test|regenerate|args, test and
# regenerate as 1 or 0. ERR|message when the matrix holds something this reader does not understand:
# a key in a leg it does not know, or anything in the matrix beside include.
read_legs() {
  awk '
    function indent(s) { match(s, /^ */); return RLENGTH }
    function flush() {
      if (name == "") return
      if (out == "") { printf "ERR|the leg %s has no out key\n", name }
      printf "LEG|%s|%s|%d|%d|%s\n", name, out, (test == "true"), (regen == "true"), args
      name = ""; out = ""; args = ""; test = ""; regen = ""
    }
    /^  instantiate:[[:space:]]*$/ { job = 1; next }
    job && /^  [^[:space:]#]/      { flush(); job = 0 }
    !job                           { next }
    /^[[:space:]]*(#.*)?$/         { next }
    !matrix {
      if ($0 ~ /^[[:space:]]+matrix:[[:space:]]*(#.*)?$/) { matrix = 1; mbase = indent($0) }
      next
    }
    # A line no deeper than matrix: ends the matrix, and with it all this reader needs of the job.
    indent($0) <= mbase { flush(); job = 0; next }
    # The matrix keys sit one indent below it. include, which holds the legs, is the only one this
    # reader knows; any other (an exclude list, a dimension such as os) changes the legs CI runs.
    !kbase { kbase = indent($0) }
    indent($0) <= kbase {
      flush()
      list = ($0 ~ /^[[:space:]]+include:[[:space:]]*(#.*)?$/)
      if (!list) {
        line = $0
        sub(/^[[:space:]]+/, "", line)
        printf "ERR|the matrix line \"%s\" is new to this script\n", line
      }
      next
    }
    !list { next }
    {
      line = $0
      sub(/^[[:space:]]+/, "", line)
      if (sub(/^-[[:space:]]+/, "", line)) flush()
      key = line; sub(/:.*$/, "", key)
      value = line; sub(/^[^:]*:[[:space:]]*/, "", value)
      if (value ~ /^"/) { value = substr(value, 2); sub(/".*$/, "", value) }
      else { sub(/[[:space:]]+#.*$/, "", value); sub(/[[:space:]]+$/, "", value) }
      if (key == "name") name = value
      else if (key == "args") args = value
      else if (key == "out") out = value
      else if (key == "test") test = value
      else if (key == "regenerate-migration") regen = value
      else printf "ERR|the matrix key %s (leg %s) is new to this script\n", key, name
    }
    END { flush() }
  ' "$workflow"
}

keep=0
list_only=0
selected=()
while [ $# -gt 0 ]; do
  case "$1" in
    -h|--help) usage; exit 0 ;;
    --list) list_only=1 ;;
    --keep) keep=1 ;;
    -*) echo "error: unknown option '$1'" >&2; usage >&2; exit 2 ;;
    *) selected+=("$1") ;;
  esac
  shift
done

records=$(read_legs)
if grep -q '^ERR|' <<< "$records"; then
  grep '^ERR|' <<< "$records" | cut -d'|' -f2- | sed 's/^/error: /' >&2
  echo "error: teach read_legs in scripts/test-template.sh what $workflow's matrix now holds" >&2
  exit 1
fi
records=$(grep '^LEG|' <<< "$records" | cut -d'|' -f2- || true)
if [ -z "$records" ]; then
  echo "error: read no legs from the instantiate job's matrix in $workflow (did the job move?)" >&2
  exit 1
fi

if [ "$list_only" -eq 1 ]; then
  printf '%-26s %-6s %-10s %s\n' LEG TESTED MIGRATION FLAGS
  while IFS='|' read -r name _ test regen args; do
    printf '%-26s %-6s %-10s %s\n' "$name" "$([ "$test" = 1 ] && echo yes || echo no)" \
      "$([ "$regen" = 1 ] && echo regen || echo -)" "${args:-(defaults)}"
  done <<< "$records"
  exit 0
fi

# The legs to run, in the matrix's order: the named ones, or all of them.
legs=""
if [ "${#selected[@]}" -eq 0 ]; then
  legs="$records"
else
  for wanted in "${selected[@]}"; do
    if ! cut -d'|' -f1 <<< "$records" | grep -qxF -- "$wanted"; then
      echo "error: no leg named '$wanted' in $workflow; the legs are:" >&2
      cut -d'|' -f1 <<< "$records" | sed 's/^/  /' >&2
      exit 2
    fi
  done
  while IFS='|' read -r name rest; do
    for wanted in "${selected[@]}"; do
      if [ "$name" = "$wanted" ]; then
        legs="${legs:+$legs
}$name|$rest"
        break
      fi
    done
  done <<< "$records"
fi

command -v dotnet >/dev/null 2>&1 || { echo "error: dotnet is not on PATH" >&2; exit 1; }
if cut -d'|' -f3 <<< "$legs" | grep -qx 1 && ! docker info >/dev/null 2>&1; then
  echo "error: Docker is not running; the generated tests need it (Testcontainers, the Aspire E2E)" >&2
  exit 1
fi

# What the generated tests read from the environment, set as Template CI sets it.
e2e="${RUN_ASPIRE_E2E-1}"
unset RUN_UI_TESTS UI_TESTS_BASE_URL UPDATE_OPENAPI_SNAPSHOT RUN_LIVE_SMOKE_TESTS

echo "==> scripts/check-template-toggles.sh"
./scripts/check-template-toggles.sh

tmp="${TMPDIR:-/tmp}"
work=$(mktemp -d "${tmp%/}/template-legs.XXXXXX")
mkdir -p "$work/logs"
trap 'echo; echo "Interrupted. Working folder: $work" >&2; exit 130' INT TERM

# Runs a command with its output appended to a log; on failure prints the log's tail.
logged() {  # $1 = log file, rest = command
  local log="$1"
  shift
  printf '\n$ %s\n' "$*" >> "$log"
  if "$@" >> "$log" 2>&1; then
    return 0
  fi
  echo "    failed: $*" >&2
  echo "    ---- last lines of $log ----" >&2
  tail -n 40 "$log" | sed 's/^/    /' >&2
  return 1
}

echo "==> pack and install into a private hive ($work)"
logged "$work/logs/pack.log" dotnet pack eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj -c Release -o "$work/nupkg" \
  || { echo "error: the template did not pack; working folder: $work" >&2; exit 1; }
nupkg=$(find "$work/nupkg" -name '*.nupkg' | head -n 1)
logged "$work/logs/pack.log" dotnet new install "$nupkg" --debug:custom-hive "$work/hive" \
  || { echo "error: the package did not install; working folder: $work" >&2; exit 1; }

# Mirrors template-ci.yml's "Regenerate the initial migration" step; runs in the generated folder.
regenerate_migration() {
  dotnet tool restore &&
    dotnet dotnet-ef migrations remove --force --project src/Infrastructure/*.Persistence &&
    dotnet dotnet-ef migrations add InitialCreate --project src/Infrastructure/*.Persistence
}
build_solution() { dotnet build ./*.slnx -c Release; }
test_solution() { RUN_ASPIRE_E2E="$e2e" dotnet test ./*.slnx -c Release --no-build; }
in_dir() {  # $1 = folder, rest = command (a function above, or a program)
  local dir="$1"
  shift
  (cd "$dir" && "$@")
}

# Results, one entry per leg (indexed arrays: macOS still ships bash 3.2).
r_name=() r_inst=() r_mig=() r_build=() r_test=() r_time=()
failed=0

run_leg() {  # name out test regen args
  local name="$1" out="$2" test="$3" regen="$4" args="$5"
  local dir="$work/$name" log="$work/logs/$name.log" started=$SECONDS
  local inst=- mig=- build=- tst=- flags=()
  [ "$regen" = 1 ] || mig=" "
  [ "$test" = 1 ] || tst=" "
  read -r -a flags <<< "$args" || true

  echo "==> $name: dotnet new trellis -n $out ${args:-(defaults)}"
  # No --allow-scripts, as in Template CI: dotnet new offers that option only for a template with a
  # scripted post-action, and rejects it as invalid here (chmod is an action of its own, never asks).
  if logged "$log" dotnet new trellis -n "$out" -o "$dir" --debug:custom-hive "$work/hive" \
       ${flags[@]+"${flags[@]}"}; then
    inst=ok
  else
    inst=FAIL
  fi

  if [ "$inst" = ok ] && [ "$regen" = 1 ]; then
    echo "==> $name: regenerate the initial migration"
    if logged "$log" in_dir "$dir" regenerate_migration; then mig=ok; else mig=FAIL; fi
  fi

  if [ "$inst" = ok ] && [ "$mig" != FAIL ]; then
    echo "==> $name: dotnet build -c Release"
    if logged "$log" in_dir "$dir" build_solution; then build=ok; else build=FAIL; fi
  fi

  if [ "$build" = ok ] && [ "$test" = 1 ]; then
    echo "==> $name: dotnet test (RUN_ASPIRE_E2E=$e2e)"
    if logged "$log" in_dir "$dir" test_solution; then tst=ok; else tst=FAIL; fi
  fi

  local elapsed=$((SECONDS - started))
  r_name+=("$name")
  r_inst+=("$inst")
  r_mig+=("$mig")
  r_build+=("$build")
  r_test+=("$tst")
  r_time+=("$((elapsed / 60))m$(printf '%02d' $((elapsed % 60)))s")
  case "$inst $mig $build $tst" in
    *FAIL*) failed=1 ;;
  esac
}

while IFS='|' read -r name out test regen args; do
  run_leg "$name" "$out" "$test" "$regen" "$args"
done <<< "$legs"

echo
printf '%-26s %-12s %-10s %-6s %-6s %s\n' LEG INSTANTIATE MIGRATION BUILD TEST TIME
for i in "${!r_name[@]}"; do
  printf '%-26s %-12s %-10s %-6s %-6s %s\n' \
    "${r_name[$i]}" "${r_inst[$i]}" "${r_mig[$i]}" "${r_build[$i]}" "${r_test[$i]}" "${r_time[$i]}"
done
echo "(blank: the leg does not have that step; -: skipped after an earlier failure)"

if [ "$failed" -ne 0 ]; then
  echo
  echo "FAILED. Logs and generated solutions: $work"
  exit 1
fi
if [ "$keep" -eq 1 ]; then
  echo "All legs passed. Working folder kept: $work"
else
  rm -rf "$work"
  echo "All legs passed."
fi
