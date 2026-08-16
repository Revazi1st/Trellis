---
description: EF Core conventions for the Persistence layer
paths: ["src/Infrastructure/XTemplate.Persistence/**/*.cs"]
---

# Persistence layer rules

Peer of Infrastructure — **never reference `XTemplate.Infrastructure`** (arch-test enforced). Owns EF Core, migrations, and Identity.

- **Configurations**: one `IEntityTypeConfiguration<T>` per aggregate in `Configurations/`. Value objects → `ComplexProperty` (EF 10 complex types). Aggregates load whole: `AutoInclude()` child navigations + `UsePropertyAccessMode(Field)`.
- **Strongly-typed IDs**: add one line to `AppDbContext.ConfigureConventions` per new ID.
- **Interceptors** (`Interceptors/`) do cross-cutting persistence work — auditing, soft-delete, domain-event dispatch. Order is fixed in `DependencyInjection.cs`.
- **Soft delete**: implement `ISoftDeletable`; the interceptor + the `"SoftDeleted"` named query filter handle it. Use `IgnoreQueryFilters(["SoftDeleted"])` for admin views.
- **Outbox**: rows store the event's `[DomainEventName]` wire name (`DomainEventSerialization.TypeNameOf`), plus `TenantId` and `TraceParent`; the processor restores `AmbientTenant` and attaches its span to the originating trace. Never persist a CLR type name.
- **Race-sensitive single-row writes are one conditional `ExecuteUpdate`**, not read-check-write — `RefreshTokenStore.ConsumeAsync` is the reference (exactly one of two concurrent replays wins).
- **Bookkeeping tables** (processed outbox, idempotency keys, dead refresh tokens) are pruned by `DataRetentionJanitor`; windows live in `RetentionOptions` (`Retention` section) — a new grows-forever table gets a sweep there.
- **Migrations are generated, never hand-edited**. Create with `./scripts/ef.sh migrations add <Name> --project src/Infrastructure/XTemplate.Persistence` (see the `add-migration` skill). Regenerate the initial migration on the current EF version rather than accumulating churn pre-release.
- **Identity** lives in `Identity/` (ADR-0007): `ApplicationUser`, `IdentityService`, refresh-token store.
