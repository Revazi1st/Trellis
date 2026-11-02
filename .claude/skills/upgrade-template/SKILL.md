---
name: upgrade-template
description: Adopt a later version of the template this project was generated from, as docs/upgrading.md describes: read the marker and the changelog between the versions, regenerate with the same name and options into a scratch directory, diff, take, merge or keep each file, settle the schema, run the gates and replace the marker last. Use when you choose to move the project to a newer template version.
argument-hint: "<version>"
disable-model-invocation: true
---

# Upgrade to a later template version

`docs/upgrading.md` is the procedure and its reasons; this is its checklist, run with
`/upgrade-template <version>` and never on the agent's own initiative. The target is `$ARGUMENTS`; with
none given, ask for it. Work on a branch from a clean working tree, and keep the scratch directory
until the upgrade is merged. It needs the `dotnet` CLI on the host or in the dev container:
`scripts/dotnet.sh` mounts only the repository, and the scratch tree lives outside it.

- [ ] **Read the marker**, .trellis\-template.json at the repository root: the template's package,
      short name and version, the repository its changelog lives in, the name you gave and every option.
      ```bash
      jq -r '.template.package, .template.shortName, .template.version, .template.repository, .name' .*-template.json
      grep -E '"(package|shortName|version|repository|name)"' .*-template.json   # without jq
      ```
      A version of `0.0.0-source` was generated from a source checkout: diff against the template
      commit you used, not a package. No marker (a project older than 0.1.0): the tree tells the options
      (`docs/upgrading.md`, step 1). Stop when `$ARGUMENTS` is not newer than the marker's version.
- [ ] **Build the flags** from `options`, every one, defaults included: a missing option takes the new
      version's default, and the diff fills with another option's files.
      ```bash
      jq -r '.options | to_entries | map("--\(.key) \(.value)") | join(" ")' .*-template.json
      sed -n '/"options"/,/}/s/^[[:space:]]*"\([^"]*\)":[[:space:]]*"\{0,1\}\([^",{}][^",{}]*\)"\{0,1\},\{0,1\}[[:space:]]*$/--\1 \2/p' .*-template.json | paste -sd' ' -   # without jq
      ```
      An option you turn on while upgrading (`--use-multitenancy true`) changes its flag here, and the
      schema box below then applies.
- [ ] **Read the changelog** between the versions: every section after the marker's version up to
      `$ARGUMENTS`, from the marker's repository (its release notes carry the same sections).
      ```bash
      curl -fsSL <repository>/raw/v$ARGUMENTS/CHANGELOG.md | sed -n '/^## \[$ARGUMENTS\]/,/^## \[<version>\]/p'
      ```
      Write down every upgrade note (drain the outbox first, regenerate a migration, a renamed token):
      the schema and deploy boxes act on them.
- [ ] **Generate the target into a scratch directory** outside the repository, with the marker's short
      name, the same name and every flag. The private hive leaves the templates you have installed as
      they are; drop `--debug:custom-hive` from both lines to install the version for good.
      ```bash
      mktemp -d   # prints the scratch directory, <scratch> below
      dotnet new install <package>@$ARGUMENTS --debug:custom-hive <scratch>/hive
      dotnet new <shortName> -n <name> -o <scratch>/<name> <flags> --skip-restore true --debug:custom-hive <scratch>/hive
      ```
      Same name and same options, or the diff drowns in renames and conditional content.
- [ ] **Diff** from the repository root:
      ```bash
      git diff --no-index --stat . <scratch>/<name>
      git diff --no-index <path> <scratch>/<name>/<path>   # one file
      ```
      Exit code 1 only means the trees differ. `.git/`, `artifacts/` and other ignored output show as
      deleted: they are not the template's, skip them. Three things differ on every generation and stay
      yours: the development signing key in the API's `appsettings.Development.json`, the AppHost's
      ports in `Properties/launchSettings.json` and the dashboard URLs `docs/development.md` names from
      them.
- [ ] **Take, merge, keep** by the table in `docs/upgrading.md` (step 4); commit the takes first, then
      merge file by file:
      - take the template's side: `scripts/`, and every file you never touched;
      - merge by hand: `.claude/` (the rules describe your code, and you may have adapted a skill or an
        agent), `docs/adr/` and `docs/INDEX.md` (the template's ADRs, 0001–0099, arrive as new files or
        dated amendments; yours start at 0100), `AGENTS.md`, `docs/*.md`, the workflows, your feature
        slices and their `docs/features/*.md`, your `.csproj` and `Directory.Packages.props` additions,
        `appsettings*.json` values, your endpoints and DI;
      - keep yours: `Migrations/` (never overwritten; the edit hook asks before any edit there),
        `CHANGELOG.md` and `README.md`.

      [`./scripts/check-adr-links.sh` fails on two ADRs sharing a number or one missing from
      `docs/INDEX.md`; `MigrationDriftTests` fails when the migrations no longer match the model.]
- [ ] **Settle the schema** (`docs/upgrading.md`, step 5) when the new version changes the persistence
      model or you turned on a schema-affecting option. No deployed database: regenerate the initial
      migration (`./scripts/ef.sh migrations remove --project src/Infrastructure/Trellis.Persistence`,
      then `./scripts/ef.sh migrations add InitialCreate --project src/Infrastructure/Trellis.Persistence`).
      A deployed database: add a migration on top
      (`./scripts/ef.sh migrations add <Name> --project src/Infrastructure/Trellis.Persistence`) and
      never regenerate `InitialCreate`, which the database's `__EFMigrationsHistory` already records
      (the `add-migration` skill) [`MigrationDriftTests` fails while the model and the migrations
      disagree].
- [ ] **Run the gates**: `./scripts/dotnet.sh build Trellis.slnx -c Release`,
      `./scripts/dotnet.sh test Trellis.slnx`, `./scripts/check-adr-links.sh` (a new template ADR needs
      its line in `docs/INDEX.md`, above *Project decisions*, or the check fails) and
      `./scripts/check-docs-drift.sh --working-tree`; `/finish-change` runs them all in CI's order.
- [ ] **Replace the marker last**, once everything above is merged by hand and green, with the new
      tree's copy, so it records the version you now match; it goes in the upgrade's last commit.
      ```bash
      cp <scratch>/<name>/.*-template.json .
      ```
- [ ] **Deploy** only after the upgrade notes' preconditions (`docs/upgrading.md`, step 6): when a note
      asks, drain the outbox (no pending `OutboxMessages` rows) and let in-flight idempotent requests
      finish before switching versions.
