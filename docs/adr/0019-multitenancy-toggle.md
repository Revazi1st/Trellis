# ADR-0019: Multi-tenancy as an opt-in template toggle

- **Status**: accepted — tenant resolution and per-aggregate wiring superseded by ADR-0022
- **Date**: 2026-07-16
- **Amended**: 2026-11-25 — the multi-tenant initial migration ships (see *Amendment (2026-11-25)* below)

## Context

Some solutions built from this template are multi-tenant, most are not. Baking a `TenantId` column and a tenant filter into every generated project would tax the majority; omitting the seam entirely would force the minority into an invasive retrofit (schema + every query + every insert).

## Decision

Ship multi-tenancy as the **`--use-multitenancy` template toggle (default off)**, structured so enabling it is a scaffold-time choice rather than a refactor:

- **`ITenantContext`** (Application port) — the current tenant, or null.
- **`TenantContext`** (Api.Common) — resolves the tenant from the principal's `tenant` **claim first**, falling back to the `X-Tenant-Id` header. Claim-first is deliberate: a header alone must never let a caller read another tenant's data. *(Superseded by ADR-0022: claim-only, the header is not read, and the claim is minted from the user's persisted membership.)*
- **`TenantInterceptor`** (Persistence) — stamps the `TenantId` **shadow property** on newly added rows, keeping the domain model free of infrastructure concerns.
- **Named `"Tenant"` query filter** on `Invoice` in `AppDbContext.OnModelCreating`, reusing the same named-filter pattern as `"SoftDeleted"` so callers can opt out explicitly with `IgnoreQueryFilters(["Tenant"])`. *(Superseded by ADR-0022: the column, index and filter are a convention over every aggregate root, not per-aggregate wiring.)*

Wiring uses the proven `#if` / XML-comment conditional mechanics on `UseMultitenancy`; the three tenant-only files are additionally excluded via `sources.modifiers` when the toggle is off.

## Consequences

- Reads are isolated by the query filter and writes are stamped automatically — feature code needs no per-query tenant plumbing.
- **Enabling the toggle changes the schema** (a `TenantId` column + index). The shipped `InitialCreate` migration is generated for the default (off) shape, so a project generated with multi-tenancy on must **regenerate its initial migration** (`./scripts/ef.sh migrations remove` then `add InitialCreate`) — trivial on a fresh project, and documented in `docs/development.md`.
- The tenant-only types compile in the template repo even with the toggle off (they are self-contained), and the on-branch is compile-verified via `-p:DefineConstants=UseMultitenancy` — which is how the missing design-time/​fixture `ITenantContext` arguments were caught.
- Tenant isolation is enforced at the data layer only; cross-tenant admin paths must opt out deliberately.

## Amendment (2026-11-25): the multi-tenant initial migration ships

*Consequences* said a project generated with multi-tenancy on must regenerate its initial migration,
because the shipped one was generated for the model without the tenant columns. That step is gone. A
project created with `--use-multitenancy` gets an initial migration generated for its own model:
`TenantId` on every aggregate table, on `AspNetUsers` and on `OutboxMessages`, with an index on the
first two. A unique index an aggregate root declares would be led by `TenantId` (ADR-0022); the
reference model declares none. `dotnet new` prints no reminder and the first run needs no command
before it.

- **A project created before this, with the option**, regenerated its own initial migration then. It
  keeps it: the tables are the same, the migration's id is its own, and its database's history records
  that id. Nothing is applied and nothing changes (`docs/upgrading.md`, steps 4 and 5).
- **Turning the option on later**, in a project created without it, is still that project's own schema
  change: regenerate the initial migration while no database is deployed, add a migration on top once
  one is (`docs/upgrading.md`, step 5).
<!--#if (TemplateSource) -->

In the template's repository this means two initial migrations and one rule: **a model change or an EF
Core bump regenerates both, in one run** (`scripts/regenerate-initial-migrations.sh`).

- The default set stays in `src/Infrastructure/Trellis.Persistence/Migrations/`. The multi-tenant set
  sits in `.template.config/tenancy/Migrations/`, outside every project: nothing here compiles it,
  dotnet-ef never finds it and `DatabaseEngineTests` does not scan it. Inside the Persistence project it
  would be a second snapshot of the same name, and `migrations add` writes to whichever it finds.
- `template.json` ships one of them: a source conditional on `UseMultitenancy` lays the second set over
  the first one's folder, and a modifier under the same condition keeps the first out. Without the
  modifier `dotnet new` succeeds and ships both; a source folder that is missing or empty makes the
  whole template invalid. The engine renames the source's target with the project name as typed,
  while every other path gets the identifier-safe form (`projectPathName`). A dotted name has one
  form for both; for a name that needs that form (`acme-shop`, which `dotnet new` warns will not
  build and should be re-created) the set lands in `acme-shop.Persistence/Migrations/`, beside the
  `acme_shop.Persistence` project, which then has no migration. A set for another option is wired
  the same way and lands the same way.
- The script generates each set where its model exists: in a project created from the working tree
  under the template's own name, with the option on for the second set, so no name is rewritten. It
  deletes the old files there and adds the migration; it never runs `migrations remove`, which on a
  host can reach a database someone keeps. The tree's files are replaced only when every set that was
  asked for can ship (an id that holds a port literal cannot). Its `--check` mode writes nothing and
  fails unless each committed file is what the script generates, the id aside.
- Held by `MigrationSnapshotTests` (both sets on the pinned EF Core, no port literal in either id, the
  same migrations in the Persistence namespace, the second snapshot equal to the first once the tenant
  seam is taken out, the wiring in `template.json`) and by Template CI: its content gate counts one set
  per leg, the right one, its first-migration command and `MigrationDriftTests` run on the two
  multi-tenancy legs, which no longer regenerate, and its pack job runs the script's `--check`.
- Considered: regenerating in every generated project, as before (a step every multi-tenant project
  had to remember before its first run, and one the template's own CI ran instead of testing what
  ships); a subfolder of the Persistence project (the snapshot hazard above); building this repository
  with the option's symbol defined to generate the set in place (a global `DefineConstants` replaces
  every project's own constants).
<!--#endif -->
