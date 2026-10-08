# Upgrading a generated project

`dotnet new` takes a snapshot: nothing updates a generated project afterwards. Adopting a later
template version is a regenerate-and-diff exercise, not a package bump.

## 1. Find what you generated from

The marker at the repository root, .trellis\-template.json, records it when the project is
generated: the template package (`template.package`), its `dotnet new` short name
(`template.shortName`), its version (`template.version`), where its changelog lives
(`template.repository`), the name you gave (`name`) and every option, keyed by its flag
(`options`, e.g. `"include-ui": true`). Keep the file and leave it unedited; it changes only when
you finish an upgrade (step 4).

A `template.version` of `0.0.0-source` means the project was generated from a source checkout of
the template, not a released package: diff against the template commit you used instead. A project
generated before the marker existed (pre-0.1.0 snapshots) has none; its tree tells you the options:
`src/Presentation/UI/` exists ⇒ `--include-ui true`, a `tests/*.UiTests` project ⇒
`--include-ui-tests true`, `SystemTenantContext.cs` ⇒ `--use-multitenancy true`, a Redis package
reference ⇒ `--use-redis true`, a Serilog one ⇒ `--use-serilog true`.

## 2. Read the changelog

The template's `CHANGELOG.md` and release notes live in the repository `template.repository` names
(a generated project's own `CHANGELOG.md` starts empty and is yours). Read every section after
`template.version` up to the target: behaviour changes and security fixes are called out there, and
some entries carry upgrade notes of their own (drain the outbox first, regenerate a migration).

## 3. Regenerate into a scratch directory and diff

Install the target version of `template.package`, then generate with the marker's short name, the
**same** name and **every** option from the marker, one `--<key> <value>` per entry:

```bash
dotnet new install <template.package>::<new-version>
dotnet new <template.shortName> -n <name> -o /tmp/next --include-ui true --use-redis false …
git diff --no-index . /tmp/next
```

Same name, same options: otherwise the diff drowns in renames and conditional-content noise.
`jq -r '.options | to_entries | map("--\(.key) \(.value)") | join(" ")'` over the marker prints the
option flags.

## 4. Apply: take, merge, keep

| Take the template's side | Merge by hand | Keep yours |
|---|---|---|
| `scripts/` (template-owned tooling, no project edits expected) | `.claude/`: the rules describe your code, the skills and agents are procedures you may have adapted, and `remove-feature` has you edit both | `Migrations/`: **never overwrite**, your migration history is project-specific (see below) |
| Files you never touched (`git diff --no-index` plus a clean working tree shows which) | `docs/adr/` and `docs/INDEX.md`: the template's ADRs are numbered 0001–0099 and arrive as new files or dated amendments; yours start at 0100 (the `new-adr` skill), so the two never collide | `CHANGELOG.md`, `README.md`: the project's own |
| The marker, last: replace it with the new tree's copy once the upgrade is merged and green, so it records the version you now match | `AGENTS.md`, `docs/*.md`, workflows: template prose and settings you may have edited | |
| | Your feature slices (`Features/*` beyond the shipped Invoices/Auth) and their `docs/features/*.md` | |
| | `.csproj` / `Directory.Packages.props` additions, `appsettings*.json` values, endpoints and DI you added | |

Commit the wholesale takes first, then work through the merge column file by file.

## 5. Schema-affecting changes

If the new version changes the persistence model, or you enable a schema-affecting option while
upgrading (`--use-multitenancy` adds `TenantId` columns to every aggregate table, `OutboxMessages`
and `AspNetUsers`):

- **No deployed database yet**: keep the pre-release convention: regenerate the initial
  migration (`./scripts/ef.sh migrations remove` + `migrations add InitialCreate`).
- **Deployed database**: add a new migration on top
  (`./scripts/ef.sh migrations add <Name> --project src/Infrastructure/Trellis.Persistence`)
  and never regenerate `InitialCreate`: the database's `__EFMigrationsHistory` already records
  the old one, and a rewritten history cannot be applied to it.

Either way, finish with `./scripts/dotnet.sh test Trellis.slnx`: the migration-drift guard
(`HasPendingModelChanges`) fails if the model and the migrations disagree.

## 6. Deploying the upgrade

Read the changelog's upgrade notes before the first deploy, not after: a change to how the outbox
credits its handlers or to a bookkeeping table's shape is safe on an empty outbox and an empty
idempotency table, and only there. Drain the outbox (no pending `OutboxMessages` rows) and let
in-flight idempotent requests finish before switching versions when a note asks for it.
