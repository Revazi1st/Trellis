---
name: add-migration
description: Create an EF Core migration the correct way for Trellis (containerized dotnet-ef). Use when the persistence model changed.
---

# Add a migration

Migrations are generated, **never hand-edited** (see `.claude/rules/persistence.md`).

```bash
./scripts/ef.sh migrations add <DescriptiveName> --project src/Infrastructure/Trellis.Persistence
```

This runs `dotnet-ef` inside the SDK container (the tool is pinned in `.config/dotnet-tools.json`). The design-time factory (`AppDbContextFactory`) supplies a throwaway connection, so no database is needed to scaffold.

After scaffolding:
1. **Review** the generated `Up`/`Down` in `src/Infrastructure/Trellis.Persistence/Migrations/` — confirm the columns/constraints match your model change (e.g. value-object complex-type columns, strongly-typed-ID `uuid` columns, required FKs). Review, never edit: when something is wrong, remove the migration (below), correct the model or its configuration, and generate it again. The edit hook asks before any edit under `Migrations/`; a data migration (SQL the model cannot express) is the one reason to allow it.
   In a project whose name sorts before `Microsoft` (`Acme.Shop`, say), the first migration also reorders `AppDbContextModelSnapshot.cs`: EF writes entity types in the order of their CLR names, and the shipped snapshot was generated under a name that sorts after `Microsoft`, so the Identity entities and yours swap places. Moved blocks are not model changes: review `Up`/`Down` and the contents of the moved entities, not their order.
2. Build + test: `./scripts/dotnet.sh test Trellis.slnx` — the IntegrationTests apply the migration against a real PostgreSQL container.

To undo a not-yet-applied migration: `./scripts/ef.sh migrations remove --force --project src/Infrastructure/Trellis.Persistence`. Without `--force` the command asks a database whether the migration was applied and stops when none answers, as in the SDK container. With it, the command reverts the migration on the database the design-time connection reaches, when that database applied it (`InitialCreate`'s `Down` drops every table), then removes the files; when no database answers, it warns and removes the files. `scripts/ef.sh` forwards `TRELLIS_DESIGNTIME_DB` into the container, so run it with that variable unset (`env -u TRELLIS_DESIGNTIME_DB ./scripts/ef.sh migrations remove --force …`) to make sure none answers, and use it only on a migration no database you keep has applied.

Pre-release, prefer regenerating the single `InitialCreate` over stacking churn migrations.
