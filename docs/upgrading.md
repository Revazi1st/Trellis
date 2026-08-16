# Upgrading a generated project

`dotnet new xtemplate` takes a snapshot — nothing updates a generated project afterwards.
Adopting a later template version is a regenerate-and-diff exercise, not a package bump.

## 1. Read the changelog

The template's `CHANGELOG.md` maps versions to what changed. Read every section between the
version you generated from and the target — security fixes and behavior changes (auth, outbox,
idempotency) are called out there, and some entries carry migration notes of their own.

## 2. Regenerate into a scratch directory and diff

```bash
dotnet new install XTemplate.Templates::<new-version>
dotnet new xtemplate -n YourProject -o /tmp/yourproject-next \
    --include-ui true --use-redis false …   # the SAME name and options you generated with
git diff --no-index . /tmp/yourproject-next
```

Same name, same toggles — otherwise the diff drowns in renames and conditional-content noise.
If you don't remember the options, the generated tree tells you: `UI/` exists ⇒ `--include-ui`,
`SystemTenantContext.cs` exists ⇒ `--use-multitenancy`, and so on.

## 3. Apply: overwrite vs merge

| Safe to overwrite wholesale | Must be merged by hand |
|---|---|
| `scripts/` — template-owned tooling, no project edits expected | Your feature slices (`Features/*` beyond the shipped Invoices/Auth) and their `docs/features/*.md` |
| `.claude/` — rules/skills/agents/hooks | `Migrations/` — **never overwrite**: your migration history is project-specific (see below) |
| `docs/adr/` — template-numbered ADRs; ADRs you recorded are new files and stay untouched | `.csproj` / `Directory.Packages.props` additions — packages and settings you added |
| | `appsettings*.json` values you changed, endpoints you registered, DI you added |

Everything else is judgment: take the template side where you never touched the file, merge
where you did. `git diff --no-index` plus a clean working tree makes the triage cheap — commit
the wholesale overwrites first, then work through the merge column file by file.

## 4. Schema-affecting changes

If the new version changes the persistence model — or you enable a schema-affecting toggle
while upgrading (`--use-multitenancy` adds a `TenantId` column):

- **No deployed database yet**: keep the pre-release convention — regenerate the initial
  migration (`./scripts/ef.sh migrations remove` + `migrations add InitialCreate`).
- **Deployed database**: add a new migration on top
  (`./scripts/ef.sh migrations add <Name> --project src/Infrastructure/XTemplate.Persistence`)
  and never regenerate `InitialCreate` — the database's `__EFMigrationsHistory` already records
  the old one, and a rewritten history cannot be applied to it.

Either way, finish with `./scripts/dotnet.sh test XTemplate.slnx` — the migration-drift guard
(`HasPendingModelChanges`) fails if the model and the migrations disagree.
