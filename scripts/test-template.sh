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
# on the default leg runs scripts/ef.sh's first-migration command on the unbuilt tree, regenerates
# the initial migration where the leg says so, builds Release, runs the generated ci.yml's format
# check (dotnet format --verify-no-changes) and, on the legs Template CI tests, runs dotnet test. A
# table of results follows, and the exit code is 1 when anything failed. The working folder (under
# $TMPDIR) keeps every log and generated solution after a failure.
#
# Because it packs, the legs get the package's content, as an install from nuget.org does and as the
# last step of Template CI's pack job checks; the instantiate job installs the source folder instead.
# The pack csproj's TemplateContentExclude decides what the package holds, template.json's
# sources[0].exclude what a folder install holds, and the two lists are kept in step by hand: a file
# missing from one of them ships by one route and not by the other.
#
# The generated tests include the Aspire E2E, which boots each generated AppHost as Template CI does
# on its Linux runners, in a run of its own after the suites (its processes and Testcontainers would
# otherwise race for the same host ports); RUN_ASPIRE_E2E=0 skips it. The browser tests of a leg that
# has them (browser: true) run only when you ask, with RUN_UI_TESTS=1: once every earlier step of the
# leg passed, as in Template CI (after the E2E, or after the format check on a leg it does not test),
# Chromium is installed into your Playwright cache through the generated playwright.ps1 (pwsh is
# needed) and the two browser steps run, each starting the generated AppHost; the run says so before
# the pack. The variable is read and unset first, so the suites never start a browser beside
# Testcontainers; without it the BROWSER column says "skip".
# Needs a .NET SDK that satisfies global.json, a running Docker (Testcontainers, the E2E), and jq and
# git (for the toggle check). Template CI's content gates (leftovers of the template's name, the
# consumer files, actionlint over the generated workflows) and its Windows job run only in CI.
#
# KEEP IN SYNC with .github/workflows/template-ci.yml. The legs and their flags are read from its
# instantiate matrix (keys name, args, out, test, regenerate-migration and browser, one per line, args in
# double quotes), so a leg added there runs here with no edit, and a key this script does not know,
# in a leg or beside include (an exclude list, a dimension such as os), stops it until it learns
# what the key means. The commands in run_leg mirror that job's steps.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

workflow=.github/workflows/template-ci.yml

usage() {
  sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'
}

# One record per leg of the instantiate job's matrix: LEG|name|out|test|regenerate|browser|args, test,
# regenerate and browser as 1 or 0. ERR|message when the matrix holds something this reader does not
# understand: a key in a leg it does not know, or anything in the matrix beside include.
read_legs() {
  awk '
    function indent(s) { match(s, /^ */); return RLENGTH }
    function flush() {
      if (name == "") return
      if (out == "") { printf "ERR|the leg %s has no out key\n", name }
      printf "LEG|%s|%s|%d|%d|%d|%s\n", name, out, (test == "true"), (regen == "true"), (browser == "true"), args
      name = ""; out = ""; args = ""; test = ""; regen = ""; browser = ""
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
      else if (key == "browser") browser = value
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
  printf '%-26s %-6s %-10s %-7s %s\n' LEG TESTED MIGRATION BROWSER FLAGS
  while IFS='|' read -r name _ test regen browser args; do
    printf '%-26s %-6s %-10s %-7s %s\n' "$name" "$([ "$test" = 1 ] && echo yes || echo no)" \
      "$([ "$regen" = 1 ] && echo regen || { [ "$name" = default ] && echo probe; } || echo -)" \
      "$([ "$browser" = 1 ] && echo yes || echo -)" "${args:-(defaults)}"
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

# What the generated tests read from the environment, set as Template CI sets it. RUN_UI_TESTS is
# read here and then unset: the suites must not start browsers or an AppHost beside Testcontainers,
# and the browser steps set it for themselves.
e2e="${RUN_ASPIRE_E2E-1}"
browser_tests="${RUN_UI_TESTS-0}"
unset RUN_UI_TESTS UI_TESTS_BASE_URL UI_TESTS_FIRST_RUN UI_TESTS_SCREENSHOTS UI_TESTS_LOGS UPDATE_OPENAPI_SNAPSHOT RUN_LIVE_SMOKE_TESTS

# 1 when a selected leg will run its browser steps: it has them and RUN_UI_TESTS=1 asked for them.
browser_legs=0
if [ "$browser_tests" = 1 ] && cut -d'|' -f5 <<< "$legs" | grep -qx 1; then
  browser_legs=1
fi

if { cut -d'|' -f3 <<< "$legs" | grep -qx 1 || [ "$browser_legs" = 1 ]; } && ! docker info >/dev/null 2>&1; then
  echo "error: Docker is not running; the generated tests need it (Testcontainers, the Aspire E2E, the browser tests' AppHost)" >&2
  exit 1
fi

if [ "$browser_legs" = 1 ]; then
  if ! command -v pwsh >/dev/null 2>&1; then
    echo "error: RUN_UI_TESTS=1 needs PowerShell (pwsh) to install the browser for the legs with browser: true" >&2
    exit 1
  fi
  # Said now, not minutes in at the leg's step: what the steps do outside the working folder.
  echo "==> RUN_UI_TESTS=1: each leg with browser: true downloads Chromium into your Playwright cache (it stays"
  echo "    after the run) and starts the generated AppHost twice, its containers in your Docker; unset RUN_UI_TESTS to skip."
fi

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

# Mirrors template-ci.yml's "First migration command on an unbuilt tree" step (the default leg
# only), which is the command inside scripts/ef.sh's sh -c; runs in the generated folder.
first_migration_probe() {
  rm -rf artifacts &&
    dotnet tool restore &&
    dotnet restore ./*.slnx --verbosity quiet &&
    dotnet dotnet-ef migrations has-pending-model-changes --project src/Infrastructure/*.Persistence
}
# What template-ci.yml's "Regenerate the initial migration" step does, without its first command; runs
# in the generated folder. 'migrations remove --force' asks a database whether the migration was applied
# and reverts it there when it was (InitialCreate's Down drops every table), and the design-time default
# is localhost:5432 with the development credentials: in the SDK container and on a CI runner nothing
# answers, on your machine your own PostgreSQL may. This script runs dotnet-ef on the host, so it deletes
# the generated migration files instead, which is all 'remove' does to the tree of a project with one
# migration; 'migrations add' reads the model and never connects.
regenerate_migration() {
  dotnet tool restore &&
    rm -f src/Infrastructure/*.Persistence/Migrations/*.cs &&
    dotnet dotnet-ef migrations add InitialCreate --project src/Infrastructure/*.Persistence
}
build_solution() { dotnet build ./*.slnx -c Release; }
# Mirrors template-ci.yml's "Format check (generated)" step, the generated ci.yml's Format check.
format_solution() { dotnet format ./*.slnx --no-restore --verify-no-changes --severity warn; }
test_solution() {
  dotnet test ./*.slnx -c Release --no-build || return
  [ "$e2e" = 1 ] || return 0
  # The E2E alone, after the suites, as Template CI's second step runs it; exit code 8 is "zero
  # tests", which a project that removed the reference flow answers.
  RUN_ASPIRE_E2E=1 dotnet test ./tests/*.ApiTests -c Release --no-build -- \
    --filter-trait Category=AspireE2E --ignore-exit-code 8
}
# Mirrors template-ci.yml's three browser steps (install the browser, the first-run pictures in a run
# of their own, then every browser test); runs in the generated folder. $1 = where the screenshots and
# the application's logs go.
browser_solution() {
  local out="$1"
  pwsh artifacts/bin/*.UiTests/release/playwright.ps1 install chromium || return
  RUN_UI_TESTS=1 UI_TESTS_FIRST_RUN=1 UI_TESTS_SCREENSHOTS="$out/screenshots" UI_TESTS_LOGS="$out/applogs/first-run" \
    dotnet test ./tests/*.UiTests -c Release --no-build --filter-class '*FirstRunPicturesTests' || return
  RUN_UI_TESTS=1 UI_TESTS_SCREENSHOTS="$out/screenshots" UI_TESTS_LOGS="$out/applogs/browser-tests" \
    dotnet test ./tests/*.UiTests -c Release --no-build
}
in_dir() {  # $1 = folder, rest = command (a function above, or a program)
  local dir="$1"
  shift
  (cd "$dir" && "$@")
}

# Results, one entry per leg (indexed arrays: macOS still ships bash 3.2).
r_name=() r_inst=() r_mig=() r_build=() r_fmt=() r_test=() r_browser=() r_time=()
failed=0

run_leg() {  # name out test regen browser args
  local name="$1" out="$2" test="$3" regen="$4" browser="$5" args="$6"
  local dir="$work/$name" log="$work/logs/$name.log" started=$SECONDS
  local inst=- mig=- build=- fmt=- tst=- brw=" " flags=()
  # MIGRATION is the regeneration on the legs that regenerate, and the first-migration probe on the
  # default leg, as in Template CI.
  [ "$regen" = 1 ] || [ "$name" = default ] || mig=" "
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

  if [ "$inst" = ok ] && [ "$name" = default ]; then
    echo "==> $name: the first migration command on an unbuilt tree (scripts/ef.sh's)"
    if logged "$log" in_dir "$dir" first_migration_probe; then mig=ok; else mig=FAIL; fi
  fi

  # A failed probe stays FAIL: the regeneration would only overwrite the result.
  if [ "$inst" = ok ] && [ "$regen" = 1 ] && [ "$mig" != FAIL ]; then
    echo "==> $name: regenerate the initial migration"
    if logged "$log" in_dir "$dir" regenerate_migration; then mig=ok; else mig=FAIL; fi
  fi

  if [ "$inst" = ok ] && [ "$mig" != FAIL ]; then
    echo "==> $name: dotnet build -c Release"
    if logged "$log" in_dir "$dir" build_solution; then build=ok; else build=FAIL; fi
  fi

  if [ "$build" = ok ]; then
    echo "==> $name: dotnet format --verify-no-changes"
    if logged "$log" in_dir "$dir" format_solution; then fmt=ok; else fmt=FAIL; fi
  fi

  if [ "$fmt" = ok ] && [ "$test" = 1 ]; then
    echo "==> $name: dotnet test (RUN_ASPIRE_E2E=$e2e)"
    if logged "$log" in_dir "$dir" test_solution; then tst=ok; else tst=FAIL; fi
  fi

  # The browser steps, on a leg that has them, only when asked (they install a browser and start the
  # generated AppHost twice), and, as Template CI runs them, once every earlier step of the leg passed:
  # the tests on a leg that has them, the format check on one that does not. Their stdin is /dev/null:
  # left alone it is the leg list of the loop below, and a command that read it would drop the legs after
  # this one.
  if [ "$browser" = 1 ]; then
    brw=-
    if [ "$browser_tests" != 1 ]; then
      brw=skip
    elif [ "$fmt" = ok ] && { [ "$test" != 1 ] || [ "$tst" = ok ]; }; then
      echo "==> $name: the browser tests (RUN_UI_TESTS=1)"
      if logged "$log" in_dir "$dir" browser_solution "$work/browser/$name" </dev/null; then brw=ok; else brw=FAIL; fi
    fi
  fi

  local elapsed=$((SECONDS - started))
  r_name+=("$name")
  r_inst+=("$inst")
  r_mig+=("$mig")
  r_build+=("$build")
  r_fmt+=("$fmt")
  r_test+=("$tst")
  r_browser+=("$brw")
  r_time+=("$((elapsed / 60))m$(printf '%02d' $((elapsed % 60)))s")
  case "$inst $mig $build $fmt $tst $brw" in
    *FAIL*) failed=1 ;;
  esac
}

while IFS='|' read -r name out test regen browser args; do
  run_leg "$name" "$out" "$test" "$regen" "$browser" "$args"
done <<< "$legs"

echo
printf '%-26s %-12s %-10s %-6s %-7s %-6s %-8s %s\n' LEG INSTANTIATE MIGRATION BUILD FORMAT TEST BROWSER TIME
for i in "${!r_name[@]}"; do
  printf '%-26s %-12s %-10s %-6s %-7s %-6s %-8s %s\n' \
    "${r_name[$i]}" "${r_inst[$i]}" "${r_mig[$i]}" "${r_build[$i]}" "${r_fmt[$i]}" "${r_test[$i]}" "${r_browser[$i]}" "${r_time[$i]}"
done
echo "(blank: the leg does not have that step; -: skipped after an earlier failure; skip: the browser tests run with RUN_UI_TESTS=1)"

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
