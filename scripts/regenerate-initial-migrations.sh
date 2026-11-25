#!/usr/bin/env bash
# Regenerates the initial migrations the template ships, on the EF Core it pins. Maintainer-only:
# template.json and the pack csproj exclude it from generated projects.
#
#   scripts/regenerate-initial-migrations.sh            both sets: after a model change or an EF Core bump
#   scripts/regenerate-initial-migrations.sh tenancy    the multi-tenant set alone
#   scripts/regenerate-initial-migrations.sh default    the default set alone
#   scripts/regenerate-initial-migrations.sh --check    writes nothing: fails unless both committed sets
#                                                       are what the script generates, but for the id
#
# The template ships two sets and a generated project gets one (template.json):
#   default   src/Infrastructure/Trellis.Persistence/Migrations/   the model without multi-tenancy
#   tenancy   .template.config/tenancy/Migrations/                 the model with --use-multitenancy
# A model change or an EF Core bump regenerates both, in one run, so they never describe two models:
# MigrationSnapshotTests (ArchitectureTests) fails while they differ by more than the tenant seam, or
# while either was written by another EF Core than Directory.Packages.props pins.
#
# Each set is generated where its model exists, and never in this tree: the template is packed from the
# working tree (untracked files included) and installed into a private hive (--debug:custom-hive, so
# your own template list is never touched), a project is created from it under the template's own
# name, with multi-tenancy on for the second set, which keeps every namespace and the CLR names in the
# snapshot as they must ship, and its initial migration is deleted and added again there. The three
# files replace a set's folder only when every set that was asked for has a migration that can ship,
# so a run that stops leaves the tree as it was.
#
# An id that holds a literal the template engine rewrites (a port symbol's 'replaces' value in
# template.json: 17171 and its neighbours) cannot ship. An id is a UTC timestamp, yyyyMMddHHmmss, so
# such a literal sits in it for ten seconds or, on a few days of a month, for ten minutes: the script
# waits a minute and generates again, up to twelve times, and then stops without changing anything.
#
# --check is the script's own test, and Template CI's pack job runs it on every pull request: it
# generates both sets the same way, writes nothing, and compares each file with the committed one once
# the id is taken out of both. So the script cannot rot unnoticed, and a committed set that was edited
# by hand or left behind by a model change fails there too.
#
# No database is contacted, and none can be. The old files are deleted, not taken away with
# 'migrations remove': that command asks a database whether the migration was applied, and with
# --force it reverts the migration there. dotnet-ef runs on the host here, where the design-time
# default (localhost) may be a database someone keeps, unlike in the SDK container scripts/ef.sh
# uses, where localhost is the container itself. 'migrations add' reads the model and never connects;
# TRELLIS_DESIGNTIME_DB is still set to an address nothing listens on.
#
# Needs a .NET SDK that satisfies global.json on PATH (dotnet new, dotnet pack and dotnet-ef run on
# the host, as in scripts/test-template.sh) and jq; no Docker. Afterwards:
#   ./scripts/dotnet.sh test tests/Trellis.ArchitectureTests --filter-class '*MigrationSnapshotTests'
#   ./scripts/dotnet.sh test tests/Trellis.IntegrationTests --filter-class '*MigrationDriftTests'
#   ./scripts/test-template.sh default no-ui-with-multitenancy all-options
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

persistence=src/Infrastructure/Trellis.Persistence
default_set=$persistence/Migrations
tenancy_set=.template.config/tenancy/Migrations
config=.template.config/template.json
attempts=12

# By its path from the root, where the cd above left us: "$0" may be relative to another folder.
usage() { sed -n '2,9p' scripts/regenerate-initial-migrations.sh | sed 's/^# \{0,1\}//'; }

want_default=0
want_tenancy=0
check=0
for arg in "$@"; do
  case "$arg" in
    -h|--help) usage; exit 0 ;;
    --check) check=1 ;;
    default) want_default=1 ;;
    tenancy) want_tenancy=1 ;;
    *) echo "error: unknown argument '$arg' (default, tenancy or --check)" >&2; usage >&2; exit 2 ;;
  esac
done
if [ "$check" -eq 1 ] && [ $((want_default + want_tenancy)) -gt 0 ]; then
  echo "error: --check always compares both sets; it takes no set name" >&2
  exit 2
fi
if [ $((want_default + want_tenancy)) -eq 0 ]; then
  want_default=1
  want_tenancy=1
fi

command -v dotnet >/dev/null 2>&1 || { echo "error: dotnet is not on PATH" >&2; exit 1; }
command -v jq >/dev/null 2>&1 || { echo "error: jq is not on PATH" >&2; exit 1; }
export TRELLIS_DESIGNTIME_DB='Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none'
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

literals=$(jq -r '.symbols | to_entries[] | select(.value.generator == "port") | .value.replaces' "$config")
[ -n "$literals" ] || { echo "error: read no port literal from $config (did the AppHost ports move?)" >&2; exit 1; }

# Prints the port literal the id holds, if any.
rewritten_literal() {  # $1 = migration id
  local literal
  for literal in $literals; do
    case "$1" in *"$literal"*) echo "$literal"; return 0 ;; esac
  done
  return 0
}

# The id of the one migration in a folder, or nothing when it holds none.
migration_id() {  # $1 = migrations folder
  local designer
  designer=$(find "$1" -maxdepth 1 -name '*_InitialCreate.Designer.cs' 2>/dev/null | head -n 1)
  [ -n "$designer" ] || return 0
  basename "$designer" _InitialCreate.Designer.cs
}

# Deletes the migrations of the project in the current folder and adds its initial migration again.
# When the files are to be kept ($1 = keep), it goes on until the id holds no port literal; a check
# compares nothing but the id-free text, so one migration is enough for it.
generate() {  # $1 = keep | compare
  local id="" literal attempt
  dotnet tool restore >/dev/null || return 1
  dotnet restore ./*.slnx --verbosity quiet || return 1
  for attempt in $(seq 1 "$attempts"); do
    mkdir -p "$persistence/Migrations" || return 1
    find "$persistence/Migrations" -maxdepth 1 -name '*.cs' -delete || return 1
    # dotnet-ef says only "Build failed" when the project does not compile, and this project goes with
    # the temp folder. Its build prints the errors while it is still here: the multi-tenant set's
    # project compiles the code under the UseMultitenancy symbol, which this tree's own build never does.
    if ! dotnet dotnet-ef migrations add InitialCreate --project "$persistence"; then
      dotnet build "$persistence" --nologo --verbosity quiet >&2 || true
      echo "error: migrations add failed in the project created for this set: after its 'Build failed' come the compiler's errors (a line in that project, whose files are this tree's with the template's options applied); without it, dotnet-ef's own output above names the cause." >&2
      return 1
    fi
    id=$(migration_id "$persistence/Migrations")
    [ -n "$id" ] || { echo "error: migrations add wrote no InitialCreate under $persistence/Migrations" >&2; return 1; }
    [ "$1" = keep ] || return 0
    literal=$(rewritten_literal "$id")
    [ -n "$literal" ] || return 0
    # The last attempt promises no next one: the error below follows it.
    [ "$attempt" -lt "$attempts" ] || break
    echo "    the id $id holds $literal, which the template engine rewrites; generating again in a minute (attempt $attempt of $attempts)"
    sleep 60
  done
  echo "error: $attempts ids in a row held a port literal, the last one $id. Nothing was changed. An id holds one for ten minutes at most: run the script again later." >&2
  return 1
}

# Creates the project a set is generated in and generates the set there. The generated files are then
# in "$work/$1/$persistence/Migrations".
generate_set() {  # $1 = default | tenancy, $2 = keep | compare
  local flags=(--include-ui false) generated count
  # The UI changes nothing in the model, so it is left out of both projects.
  [ "$1" = default ] || flags+=(--use-multitenancy true)
  # Under the template's own name: nothing is renamed, so the files are the ones that must ship.
  dotnet new trellis -n Trellis -o "$work/$1" "${flags[@]}" --skip-restore true \
    --debug:custom-hive "$work/hive" >"$work/new-$1.log" 2>&1 \
    || { cat "$work/new-$1.log" >&2; echo "error: the project for the $1 set was not created" >&2; return 1; }

  (cd "$work/$1" && generate "$2") || return 1

  generated="$work/$1/$persistence/Migrations"
  count=$(find "$generated" -maxdepth 1 -name '*.cs' | wc -l | tr -d ' ')
  [ "$count" -eq 3 ] || { echo "error: expected one migration, its designer and a snapshot under $generated, found $count file(s)" >&2; return 1; }
  if [ "$1" = tenancy ] && ! grep -q '"TenantId"' "$generated/AppDbContextModelSnapshot.cs"; then
    echo "error: the snapshot generated for the tenancy set holds no tenant column" >&2
    return 1
  fi
  if [ "$1" = default ] && grep -q '"TenantId"' "$generated/AppDbContextModelSnapshot.cs"; then
    echo "error: the snapshot generated for the default set holds a tenant column" >&2
    return 1
  fi
}

# Replaces a set's folder in this tree with what was generated for it.
keep_set() {  # $1 = default | tenancy, $2 = the set's folder
  find "$2" -maxdepth 1 -name '*.cs' -delete
  cp "$work/$1/$persistence/Migrations"/*.cs "$2"/
}

# Compares what was generated for a set with the committed files, the id taken out of both. Prints the
# differences ('<' generated, '>' committed), each with the committed file under it, and fails when
# there is one.
compare_set() {  # $1 = default | tenancy, $2 = the set's folder
  local generated="$work/$1/$persistence/Migrations" new old suffix same=0
  new=$(migration_id "$generated")
  old=$(migration_id "$2")
  [ -n "$old" ] || { echo "error: $2 holds no initial migration" >&2; return 1; }
  [ "$(find "$2" -maxdepth 1 -name '*.cs' | wc -l | tr -d ' ')" -eq 3 ] \
    || { echo "error: $2 does not hold exactly one migration, its designer and the snapshot" >&2; same=1; }
  for suffix in _InitialCreate.cs _InitialCreate.Designer.cs; do
    diff <(sed "s/$new/<id>/g" "$generated/$new$suffix") <(sed "s/$old/<id>/g" "$2/$old$suffix") \
      || { echo "    ^ $2/$old$suffix"; same=1; }
  done
  diff "$generated/AppDbContextModelSnapshot.cs" "$2/AppDbContextModelSnapshot.cs" \
    || { echo "    ^ $2/AppDbContextModelSnapshot.cs"; same=1; }
  return "$same"
}

tmp="${TMPDIR:-/tmp}"
work=$(mktemp -d "${tmp%/}/regenerate-migrations.XXXXXX")
seeded=0
# Whatever ends the run: the temp folder goes, the private hive with it, and so does a second set that
# was only seeded (below) and never replaced by a generated one.
cleanup() {
  rm -rf "$work"
  [ "$seeded" -eq 0 ] || { rm -rf "$tenancy_set"; rmdir "$(dirname "$tenancy_set")" 2>/dev/null || true; }
}
trap cleanup EXIT

# template.json names the second set's folder as a source, and a template whose source is missing or
# empty does not load at all (MV012). So the first run, before the set exists, starts from a copy of
# the default set: the project created for it gets that copy, and generate deletes it again. A run that
# stops before the generated set is in place takes the copy away again (cleanup). A folder that holds
# files but no migration is not a first run: it is refused, so nothing of it is lost.
if [ -z "$(migration_id "$tenancy_set")" ]; then
  [ "$check" -eq 0 ] || { echo "error: $tenancy_set holds no initial migration" >&2; exit 1; }
  [ "$want_tenancy" -eq 1 ] || { echo "error: $tenancy_set holds no initial migration yet; generate it first: $0 tenancy" >&2; exit 1; }
  if [ -d "$tenancy_set" ] && [ -n "$(ls -A "$tenancy_set")" ]; then
    echo "error: $tenancy_set holds files but no initial migration; restore it (git checkout -- $tenancy_set) or delete the folder, then run the script again" >&2
    exit 1
  fi
  seeded=1
  mkdir -p "$tenancy_set"
  cp "$default_set"/*.cs "$tenancy_set"/
fi

dotnet pack eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj -c Release -o "$work/nupkg" >"$work/pack.log" 2>&1 \
  || { cat "$work/pack.log" >&2; echo "error: the template did not pack" >&2; exit 1; }
dotnet new install "$(find "$work/nupkg" -name '*.nupkg' | head -n 1)" --debug:custom-hive "$work/hive" >"$work/install.log" 2>&1 \
  || { cat "$work/install.log" >&2; echo "error: the package did not install" >&2; exit 1; }

if [ "$check" -eq 1 ]; then
  failed=0
  for set in default tenancy; do
    echo "==> $set: generating to compare"
    generate_set "$set" compare || exit 1
  done
  compare_set default "$default_set" || failed=1
  compare_set tenancy "$tenancy_set" || failed=1
  if [ "$failed" -eq 1 ]; then
    echo "error: a committed initial migration is not what its model generates (the differences are above: '<' generated, '>' committed). A model change or an EF Core bump regenerates both sets: scripts/regenerate-initial-migrations.sh. A migration is never edited by hand." >&2
    exit 1
  fi
  echo "Both committed sets are what the script generates (default $(migration_id "$default_set"), tenancy $(migration_id "$tenancy_set"))."
  exit 0
fi

# Generate everything that was asked for first, and touch the tree only when all of it can ship.
for set in default tenancy; do
  want=want_$set
  [ "${!want}" -eq 1 ] || continue
  echo "==> $set"
  generate_set "$set" keep || exit 1
done
[ "$want_default" -eq 0 ] || keep_set default "$default_set"
if [ "$want_tenancy" -eq 1 ]; then
  keep_set tenancy "$tenancy_set"
  seeded=0
fi

echo
echo "Regenerated:$([ "$want_default" -eq 1 ] && echo " default $(migration_id "$default_set")")$([ "$want_tenancy" -eq 1 ] && echo " tenancy $(migration_id "$tenancy_set")")"
echo "Now run: ./scripts/dotnet.sh test tests/Trellis.ArchitectureTests --filter-class '*MigrationSnapshotTests'"
echo "         ./scripts/dotnet.sh test tests/Trellis.IntegrationTests --filter-class '*MigrationDriftTests'"
echo "         ./scripts/test-template.sh default no-ui-with-multitenancy all-options"
