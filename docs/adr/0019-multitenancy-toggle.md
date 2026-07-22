# ADR-0019: Multi-tenancy as an opt-in template toggle

- **Status**: accepted
- **Date**: 2026-07-16

## Context

Some solutions built from this template are multi-tenant, most are not. Baking a `TenantId` column and a tenant filter into every generated project would tax the majority; omitting the seam entirely would force the minority into an invasive retrofit (schema + every query + every insert).

## Decision

Ship multi-tenancy as the **`--use-multitenancy` template toggle (default off)**, structured so enabling it is a scaffold-time choice rather than a refactor:

- **`ITenantContext`** (Application port) — the current tenant, or null.
- **`TenantContext`** (Api.Common) — resolves the tenant from the principal's `tenant` **claim first**, falling back to the `X-Tenant-Id` header. Claim-first is deliberate: a header alone must never let a caller read another tenant's data.
- **`TenantInterceptor`** (Persistence) — stamps the `TenantId` **shadow property** on newly added rows, keeping the domain model free of infrastructure concerns.
- **Named `"Tenant"` query filter** on `Invoice` in `AppDbContext.OnModelCreating`, reusing the same named-filter pattern as `"SoftDeleted"` so callers can opt out explicitly with `IgnoreQueryFilters(["Tenant"])`.

Wiring uses the proven `#if (UseMultitenancy)` / `<!--#if-->` mechanics; the three tenant-only files are additionally excluded via `sources.modifiers` when the toggle is off.

## Consequences

- Reads are isolated by the query filter and writes are stamped automatically — feature code needs no per-query tenant plumbing.
- **Enabling the toggle changes the schema** (a `TenantId` column + index). The shipped `InitialCreate` migration is generated for the default (off) shape, so a project generated with multi-tenancy on must **regenerate its initial migration** (`./scripts/ef.sh migrations remove` then `add InitialCreate`) — trivial on a fresh project, and documented in `docs/development.md`.
- The tenant-only types compile in the template repo even with the toggle off (they are self-contained), and the on-branch is compile-verified via `-p:DefineConstants=UseMultitenancy` — which is how the missing design-time/​fixture `ITenantContext` arguments were caught.
- Tenant isolation is enforced at the data layer only; cross-tenant admin paths must opt out deliberately.
