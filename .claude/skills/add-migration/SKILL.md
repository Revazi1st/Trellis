---
name: add-migration
description: Create an EF Core migration the correct way for XTemplate (containerized dotnet-ef). Use when the persistence model changed.
allowed-tools: Bash(./scripts/ef.sh:*), Bash(./scripts/dotnet.sh:*)
---

# Add a migration

Migrations are generated, **never hand-edited** (see `.claude/rules/persistence.md`).

```bash
./scripts/ef.sh migrations add <DescriptiveName> --project src/Infrastructure/XTemplate.Persistence
```

This runs `dotnet-ef` inside the SDK container (the tool is pinned in `.config/dotnet-tools.json`). The design-time factory (`AppDbContextFactory`) supplies a throwaway connection, so no database is needed to scaffold.

After scaffolding:
1. **Review** the generated `Up`/`Down` in `src/Infrastructure/XTemplate.Persistence/Migrations/` — confirm the columns/constraints match your model change (e.g. value-object complex-type columns, strongly-typed-ID `uuid` columns, required FKs).
2. Build + test: `./scripts/dotnet.sh test XTemplate.slnx` — the IntegrationTests apply the migration against a real PostgreSQL container.

To undo a not-yet-applied migration: `./scripts/ef.sh migrations remove --project src/Infrastructure/XTemplate.Persistence`.

Pre-release, prefer regenerating the single `InitialCreate` over stacking churn migrations.
