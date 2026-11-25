#!/usr/bin/env bash
# Template CI's content gate, on one project the template generated: nothing in it may still carry the
# template's name, unprocessed template syntax, the name of a script the template does not ship, or a
# file that never belongs in a repository, and it holds the one initial migration its options ask for.
# Maintainer-only: template.json and the pack csproj exclude it from generated projects, Template CI's
# must-not-ship loop names it, and its own search for maintainer-only script names (check 5) finds it.
#
#   scripts/check-generated-content.sh DIR NAME [FLAGS...]   check the project dotnet new wrote to DIR
#   scripts/check-generated-content.sh --self-test           plant each defect in a throwaway tree
#
# NAME is the name the project was generated with (dotnet new -n) and FLAGS are the leg's own, word by
# word, as template-ci.yml's matrix passes them to dotnet new (its args); of them only
# "--use-multitenancy true" changes what is checked. Each check reads every file but those under .git,
# artifacts, bin and obj: restore already ran as a post-action, and its output holds absolute paths,
# which contain the repository's name on a runner.
#   1  no file or folder is named after the template (*trellis*, any case), the provenance marker
#      .trellis-template.json aside (Template CI's leftovers step checks the marker)
#   2  no unprocessed template conditional: the engine's token (a hash, then "if (") in any line
#   3  no TRELLIS_ token survived instantiation
#   4  no sentence reads "a NAME ..." after the rename: the engine renames Trellis to the project's
#      name, so "a Trellis change" ships as "a Acme.Shop change"; the source says "a change to Trellis"
#   5  no file names a maintainer-only script (check-template-toggles.sh, test-template.sh,
#      check-readme-sync.sh, regenerate-initial-migrations.sh, this one): a workflow step that runs
#      one belongs in a block conditioned on the TemplateSource symbol (see ci.yml); a hook, an agent
#      file, AGENTS.md or a doc that names one, likewise. Every file is searched, not only .github and
#      .claude: shipped mentions have sat in AGENTS.md, docs/ and a test's comment.
#   6  every migration's [Migration] attribute says its file's name: the engine replaces a port
#      symbol's literal (17171 and the others in template.json) wherever it appears, a migration id (a
#      timestamp) can contain one, and the generated project's 'migrations remove' then finds nothing
#      to remove (MigrationSnapshotTests refuses such an id in the template repository)
#   7  one initial migration shipped (3 files under the Persistence project's Migrations/: it, its
#      designer and the snapshot), with tenant columns in the snapshot exactly when FLAGS hold
#      "--use-multitenancy true": the template keeps two sets, and without template.json's modifier
#      dotnet new still succeeds and ships both
#   8  none of the files that must never ship: .DS_Store, appsettings.*.local.json, *.pfx
# Every check runs. A failure prints what it found, then its message: a GitHub annotation (::error::)
# under GitHub Actions, an "error:" line otherwise. The exit code is 1 when any check failed and 2 for a
# bad argument; a passing project prints nothing.
#
# Template CI's instantiate job runs it on every leg right after dotnet new (its "Content gate" step),
# scripts/test-template.sh runs it at the same point of every leg (the CONTENT column), and ci.yml runs
# the self-test in its template-source block. The checks are the ones the "Content gate" step ran
# inline until it called this script, with the same messages; a new check gets a self-test case that
# plants its defect. bash 3.2, BSD and GNU find, grep and sed.
set -euo pipefail
unset GREP_OPTIONS   # BSD grep honours it: a --color=always there would put escapes into the lines listed
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
self="$here/$(basename "${BASH_SOURCE[0]}")"
# The engine's conditional token is spelled from pieces, here and in the self-test's plants, so the
# engine would find no directive in this file should the template ever stop excluding it.
h='#'
ellipsis=$'\342\200\246'   # U+2026, in check 4's message

usage() {
  echo "usage: scripts/check-generated-content.sh DIR NAME [FLAGS...]" >&2
  echo "       scripts/check-generated-content.sh --self-test" >&2
  exit 2
}

# error [FILE] TEXT: the message of a failed check.
error() {
  local file=""
  if [ "$#" -gt 1 ]; then
    file="$1"
    shift
  fi
  if [ "${GITHUB_ACTIONS:-}" = true ]; then
    echo "::error${file:+ file=$file}::$1"
  else
    echo "error: ${file:+$file: }$1"
  fi
}

# gate: the checks, run in the generated folder; reads $name and $flags.
gate() {
  fail=0
  prune=(-path ./.git -o -path ./artifacts -o -name bin -o -name obj)
  skip=(--exclude-dir=.git --exclude-dir=artifacts --exclude-dir=bin --exclude-dir=obj)

  # 1. The provenance marker is named after the template on purpose (checked in the leftovers step).
  names=$(find . \( "${prune[@]}" \) -prune -o -iname '*trellis*' ! -path ./.trellis-template.json -print)
  if [ -n "$names" ]; then
    error "Generated files or directories still named after the template:"
    echo "$names"
    fail=1
  fi

  # 2.
  if grep -rInF "${skip[@]}" "${h}if (" .; then
    error "Unprocessed template conditionals in the generated output (listed above)."
    fail=1
  fi

  # 3.
  if grep -rInF "${skip[@]}" 'TRELLIS_' .; then
    error "TRELLIS_ tokens survived instantiation (listed above)."
    fail=1
  fi

  # 4. Checked here, on what ships, so every file type and no excluded file counts.
  if grep -rInwiF "${skip[@]}" "a $name" .; then
    error "A sentence reads 'a $name $ellipsis' after the rename (listed above): its source says 'a Trellis $ellipsis'; write 'a change to Trellis' instead."
    fail=1
  fi

  # 5.
  if grep -rInE "${skip[@]}" 'check-template-toggles|test-template\.sh|check-readme-sync|regenerate-initial-migrations|check-generated-content' .; then
    error "Generated files reference a maintainer-only script the template does not ship (listed above)."
    fail=1
  fi

  # 6.
  for designer in src/Infrastructure/*.Persistence/Migrations/*.Designer.cs; do
    [ -e "$designer" ] || continue
    id=$(sed -n 's/.*\[Migration("\([^"]*\)")\].*/\1/p' "$designer" | head -n 1)
    file=$(basename "$designer" .Designer.cs)
    if [ "$id" != "$file" ]; then
      error "$designer" "the [Migration] attribute says '$id' but the file is '$file': a literal in the id was rewritten by the engine (a port symbol's replaces value in template.json); regenerate both shipped sets with scripts/regenerate-initial-migrations.sh, which generates again a minute later until the id holds none."
      fail=1
    fi
  done

  # 7. What shipped is counted: one migration, its designer and the snapshot, with the tenant columns
  # exactly when the leg asked for them.
  set -- src/Infrastructure/*.Persistence/Migrations/*.cs
  tenant_columns=$(grep -c '"TenantId"' src/Infrastructure/*.Persistence/Migrations/*ModelSnapshot.cs || true)
  case " $flags " in
    *" --use-multitenancy true "*) wanted="some" ;;
    *) wanted="no" ;;
  esac
  if [ "$#" -ne 3 ] || { [ "$wanted" = some ] && [ "$tenant_columns" -eq 0 ]; } || { [ "$wanted" = no ] && [ "$tenant_columns" -ne 0 ]; }; then
    error "The generated project holds $# file(s) under Migrations/ and $tenant_columns TenantId line(s) in its snapshot; this leg must get one initial migration (3 files) with $wanted tenant columns. See the sources of .template.config/template.json."
    fail=1
  fi

  # 8.
  junk=$(find . \( "${prune[@]}" \) -prune -o \( -name .DS_Store -o -name 'appsettings.*.local.json' -o -name '*.pfx' \) -print)
  if [ -n "$junk" ]; then
    error "Files that must never ship are in the generated output:"
    echo "$junk"
    fail=1
  fi
}

# --- the self-test --------------------------------------------------------------------------------

# put PATH: writes stdin to PATH under the self-test's tree, making its folder.
put() { mkdir -p "$(dirname "$tree/$1")"; cat > "$tree/$1"; }

# seed [tenant]: a generated project every check passes, with the multi-tenant snapshot when asked. It
# holds what the checks must leave alone: the marker named after the template, the shipped scripts'
# names, "data Acme.Shop" (no word "a"), and a hash with "if" in a comment.
seed() {
  rm -rf "$tree"
  mkdir -p "$tree"
  printf '%s\n' "namespace $name.Persistence.Migrations;" '' 'public partial class InitialCreate : Migration' '{' '}' \
    | put "$migrations/$id.cs"
  printf '%s\n' "namespace $name.Persistence.Migrations;" '' '[DbContext(typeof(AppDbContext))]' "[Migration(\"$id\")]" \
    'partial class InitialCreate' '{' '}' | put "$migrations/$id.Designer.cs"
  {
    printf '%s\n' "namespace $name.Persistence.Migrations;" '' 'partial class AppDbContextModelSnapshot' '{' \
      '    // b.Property<string>("Number");'
    if [ "${1:-}" = tenant ]; then
      printf '%s\n' '    // b.Property<string>("TenantId");' '    // b.HasIndex("TenantId", "Number");'
    fi
    printf '%s\n' '}'
  } | put "$migrations/AppDbContextModelSnapshot.cs"
  printf '%s\n' '{ "template": { "package": "Sample.Templates", "version": "1.0.0" }, "options": {} }' \
    | put .trellis-template.json
  printf '%s\n' "# $name" '' "A change to $name starts with a failing test. Its data $name keeps in PostgreSQL." \
    'Run ./scripts/doctor.sh, ./scripts/check-hooks.sh and ./scripts/check-adr-links.sh before the push.' \
    | put README.md
  printf '%s\n' 'set -euo pipefail' "${h} if the image is missing, build it first" 'IMAGE="${ACME_SHOP_SDK_IMAGE:-acme-shop-sdk:10.0}"' \
    | put scripts/dotnet.sh
}

self_test() {
  local cases=0 failed=0 out code script dir
  st_root="$(mktemp -d)"   # global: the EXIT trap reads it after this function returns
  trap 'rm -rf "$st_root"' EXIT
  tree="$st_root/generated"
  name=Acme.Shop
  migrations="src/Infrastructure/$name.Persistence/Migrations"
  id=20261101090000_InitialCreate

  # expect CASE pass|fail TEXT LISTED [FLAGS...]: runs the gate on the tree as a leg generated with
  # FLAGS. A pass prints nothing; a failure exits 1 with exactly one failed check, whose message holds
  # TEXT and whose listing holds LISTED (when not empty). The tree is seeded again for the next case.
  expect() {
    local case_name="$1" want="$2" text="$3" listed="$4" errors
    shift 4
    cases=$((cases + 1))
    out="$(GITHUB_ACTIONS='' bash "$self" "$tree" "$name" "$@" 2>&1)" && code=0 || code=$?
    errors=$(grep -c '^error: ' <<< "$out" || true)
    if [ "$want" = pass ] && { [ "$code" != 0 ] || [ -n "$out" ]; }; then
      echo "self-test case '$case_name': expected a pass that prints nothing, got exit $code:"
      printf '%s\n' "$out" | sed 's/^/  /'
      failed=$((failed + 1))
    elif [ "$want" = fail ] && { [ "$code" != 1 ] || [ "$errors" != 1 ]; }; then
      echo "self-test case '$case_name': expected exit 1 and one failed check, got exit $code and $errors:"
      printf '%s\n' "$out" | sed 's/^/  /'
      failed=$((failed + 1))
    elif [ -n "$text" ] && ! grep -qF -- "$text" <<< "$out"; then
      echo "self-test case '$case_name': the output does not say '$text':"
      printf '%s\n' "$out" | sed 's/^/  /'
      failed=$((failed + 1))
    elif [ -n "$listed" ] && ! grep -qF -- "$listed" <<< "$out"; then
      echo "self-test case '$case_name': the output does not list '$listed':"
      printf '%s\n' "$out" | sed 's/^/  /'
      failed=$((failed + 1))
    fi
    seed
  }

  seed
  expect "a clean project" pass "" ""
  seed tenant
  expect "a clean multi-tenant project" pass "" "" --include-ui false --use-multitenancy true
  expect "multi-tenancy asked off" pass "" "" --include-ui false --use-multitenancy false

  # Every defect below, under the folders the checks skip: build and restore output, and git's own.
  for dir in .git/logs artifacts/package/release "src/Presentation/Api/$name.Api/bin/Release" "src/Core/$name.Domain/obj"; do
    printf '%s\n' "${h}if (UseRedis)" 'TRELLIS_SDK_IMAGE' "/home/runner/work/Trellis/Trellis: a $name change" \
      './scripts/test-template.sh' | put "$dir/Trellis.Api.deps.json"
    : > "$tree/$dir/.DS_Store"
    : > "$tree/$dir/dev.pfx"
  done
  expect "defects under .git, artifacts, bin and obj only" pass "" ""

  # 1
  printf '%s\n' "namespace $name.Domain;" | put src/Core/Trellis.Domain/Invoice.cs
  expect "1: a folder named after the template" fail "error: Generated files or directories still named after the template:" "./src/Core/Trellis.Domain"
  printf '%s\n' '# Notes' | put docs/TRELLIS-notes.md
  expect "1: a file named after the template, in capitals" fail "error: Generated files or directories still named after the template:" "./docs/TRELLIS-notes.md"

  # 2
  printf '%s\n' 'namespace Acme.Shop.Api;' "${h}if (UseRedis)" 'using StackExchange.Redis;' "${h}endif" \
    | put "src/Presentation/Api/$name.Api/Program.cs"
  expect "2: an unprocessed conditional" fail "error: Unprocessed template conditionals in the generated output (listed above)." \
    "./src/Presentation/Api/$name.Api/Program.cs:2:"

  # 3
  printf '%s\n' 'IMAGE="${TRELLIS_SDK_IMAGE:-acme-shop-sdk:10.0}"' | put scripts/dotnet.sh
  expect "3: a TRELLIS_ token" fail "error: TRELLIS_ tokens survived instantiation (listed above)." "./scripts/dotnet.sh:1:"

  # 4
  printf '%s\n' '# Development' '' "Run the tests before a $name change." | put docs/development.md
  expect "4: a sentence that reads 'a $name'" fail \
    "error: A sentence reads 'a $name $ellipsis' after the rename (listed above): its source says 'a Trellis $ellipsis'; write 'a change to Trellis' instead." \
    "./docs/development.md:3:"

  # 5: this week's defect first, a test's comment that names a script the template does not ship.
  for script in test-template.sh check-template-toggles.sh check-readme-sync.sh regenerate-initial-migrations.sh \
      check-generated-content.sh; do
    printf '%s\n' "namespace $name.ApiTests;" '' "// The legs of scripts/$script run this test too." \
      | put "tests/$name.ApiTests/OpenApiSnapshotTests.cs"
    expect "5: a shipped comment that names scripts/$script" fail \
      "error: Generated files reference a maintainer-only script the template does not ship (listed above)." \
      "./tests/$name.ApiTests/OpenApiSnapshotTests.cs:3:"
  done

  # 6: 17171, a port symbol's literal, inside the id was rewritten to another port.
  rm "$tree/$migrations/$id".*
  id=20261111171711_InitialCreate
  printf '%s\n' 'public partial class InitialCreate : Migration' '{' '}' | put "$migrations/$id.cs"
  printf '%s\n' '[Migration("20261111171611_InitialCreate")]' 'partial class InitialCreate' '{' '}' \
    | put "$migrations/$id.Designer.cs"
  expect "6: a [Migration] attribute that is not its file's name" fail \
    "error: $migrations/$id.Designer.cs: the [Migration] attribute says '20261111171611_InitialCreate' but the file is '$id'" ""
  id=20261101090000_InitialCreate
  seed

  # 7
  printf '%s\n' 'public partial class Second : Migration' '{' '}' | put "$migrations/20261102090000_Second.cs"
  printf '%s\n' '[Migration("20261102090000_Second")]' 'partial class Second' '{' '}' \
    | put "$migrations/20261102090000_Second.Designer.cs"
  expect "7: two migrations shipped" fail \
    "error: The generated project holds 5 file(s) under Migrations/ and 0 TenantId line(s) in its snapshot; this leg must get one initial migration (3 files) with no tenant columns." ""
  rm -r "${tree:?}/$migrations"
  expect "7: no migration shipped" fail \
    "this leg must get one initial migration (3 files) with no tenant columns. See the sources of .template.config/template.json." ""
  seed tenant
  expect "7: tenant columns on a leg without multi-tenancy" fail \
    "error: The generated project holds 3 file(s) under Migrations/ and 2 TenantId line(s) in its snapshot; this leg must get one initial migration (3 files) with no tenant columns." "" \
    --include-ui false
  expect "7: no tenant columns on a leg with multi-tenancy" fail \
    "error: The generated project holds 3 file(s) under Migrations/ and 0 TenantId line(s) in its snapshot; this leg must get one initial migration (3 files) with some tenant columns." "" \
    --include-ui false --use-multitenancy true

  # 8
  mkdir -p "$tree/docs"
  : > "$tree/docs/.DS_Store"
  expect "8: a .DS_Store" fail "error: Files that must never ship are in the generated output:" "./docs/.DS_Store"
  printf '%s\n' '{}' | put "src/Presentation/Api/$name.Api/appsettings.Development.local.json"
  expect "8: a local appsettings file" fail "error: Files that must never ship are in the generated output:" \
    "./src/Presentation/Api/$name.Api/appsettings.Development.local.json"
  : > "$tree/dev-cert.pfx"
  expect "8: a certificate" fail "error: Files that must never ship are in the generated output:" "./dev-cert.pfx"

  # Every check runs after one failed: two defects, two messages.
  cases=$((cases + 1))
  printf '%s\n' 'TRELLIS_DESIGNTIME_DB=1' | put .env
  : > "$tree/.DS_Store"
  out="$(GITHUB_ACTIONS='' bash "$self" "$tree" "$name" 2>&1)" && code=0 || code=$?
  if [ "$code" != 1 ] || [ "$(grep -c '^error: ' <<< "$out")" != 2 ] \
    || ! grep -qF 'error: TRELLIS_ tokens survived' <<< "$out" || ! grep -qF 'error: Files that must never ship' <<< "$out"; then
    echo "self-test case 'two defects, both reported': exit $code:"; printf '%s\n' "$out" | sed 's/^/  /'
    failed=$((failed + 1))
  fi
  seed

  # Under GitHub Actions a failure is an annotation, with the file when the check names one.
  cases=$((cases + 1))
  printf '%s\n' 'TRELLIS_DESIGNTIME_DB=1' | put .env
  out="$(GITHUB_ACTIONS=true bash "$self" "$tree" "$name" 2>&1)" && code=0 || code=$?
  if [ "$code" != 1 ] || ! grep -qxF '::error::TRELLIS_ tokens survived instantiation (listed above).' <<< "$out" \
    || grep -q '^error: ' <<< "$out"; then
    echo "self-test case 'an annotation under GitHub Actions': exit $code:"; printf '%s\n' "$out" | sed 's/^/  /'
    failed=$((failed + 1))
  fi
  seed
  cases=$((cases + 1))
  printf '%s\n' '[Migration("20261101093000_InitialCreate")]' | put "$migrations/$id.Designer.cs"
  out="$(GITHUB_ACTIONS=true bash "$self" "$tree" "$name" 2>&1)" && code=0 || code=$?
  if [ "$code" != 1 ] || ! grep -qF "::error file=$migrations/$id.Designer.cs::the [Migration] attribute says '20261101093000_InitialCreate'" <<< "$out"; then
    echo "self-test case 'an annotation that names its file': exit $code:"; printf '%s\n' "$out" | sed 's/^/  /'
    failed=$((failed + 1))
  fi
  seed

  # A bad argument is the usage and exit 2.
  bad() {  # bad CASE [ARG...]: the script run with these arguments
    local case_name="$1"
    shift
    cases=$((cases + 1))
    out="$(bash "$self" "$@" 2>&1)" && code=0 || code=$?
    if [ "$code" != 2 ] || ! grep -qF 'usage: scripts/check-generated-content.sh DIR NAME [FLAGS...]' <<< "$out"; then
      echo "self-test case '$case_name': expected the usage and exit 2, got exit $code:"; printf '%s\n' "$out" | sed 's/^/  /'
      failed=$((failed + 1))
    fi
  }
  bad "no argument"
  bad "a folder without a name" "$tree"
  bad "an empty name" "$tree" ""
  bad "a folder that is not there" "$st_root/missing" "$name"
  bad "an option it does not know" --bogus
  bad "the self-test with an argument" --self-test "$tree"

  if [ "$failed" -ne 0 ]; then
    echo "content-gate self-test failed: $failed of $cases cases"
    return 1
  fi
  echo "content-gate self-test passed: $cases cases"
}

case "${1:-}" in
  --self-test)
    [ "$#" -eq 1 ] || usage
    self_test
    exit
    ;;
  -h | --help)
    sed -n '2,9p' "$self" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
  "" | -*) usage ;;
esac
[ "$#" -ge 2 ] && [ -n "$2" ] || usage
if [ ! -d "$1" ]; then
  echo "error: $1 is not a folder" >&2
  usage
fi
name="$2"
dir="$1"
shift 2
flags="$*"
cd "$dir"
gate
exit "$fail"
