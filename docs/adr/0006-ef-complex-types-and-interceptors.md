# ADR-0006: EF Core 10 value-object mapping + SaveChanges interceptors

- **Status**: accepted — the in-transaction domain-event dispatch (interceptor 1 below) is superseded by ADR-0018 (transactional outbox); the value-object mapping, soft-delete and auditing stand
- **Date**: 2026-07-14
- **Amended**: 2026-10-11 — concurrency at the aggregate boundary by convention, and value objects nesting value objects (see *Amendment (2026-10-11)* below)

## Context

The persistence layer must map rich-DDD building blocks — value objects, domain events, auditing, soft-delete — without leaking EF into the domain.

## Decision

- **Value objects as EF complex types** (`ComplexProperty`), the EF 10-recommended mapping (value semantics; not owned-entity identity). `Money`/`EmailAddress` map this way in `Configurations/`.
- **Aggregates load whole**: child navigations use `AutoInclude()` + field access; there are no partial-aggregate loads.
- **Three `ISaveChangesInterceptor`s**, order fixed in `Persistence/DependencyInjection.cs` *(amended 2026-10-11: a fourth, `AggregateConcurrencyInterceptor`, is attached by `AppDbContext.OnConfiguring` after these)*:
  1. A domain-event dispatch interceptor — dispatched aggregate events inside `SaveChanges` (draining to quiescence). *(Superseded by ADR-0018: `OutboxWriteInterceptor` now takes this slot and writes each event to `OutboxMessages` in the same transaction; handlers run after commit, never during `SaveChanges`.)*
  2. `SoftDeleteInterceptor` — turns `Remove` of `ISoftDeletable` into a `DeletedAtUtc` update, rescues cascade-deleted children; paired with the EF 10 named `"SoftDeleted"` query filter.
  3. `AuditingInterceptor` — stamps `IAuditable` from `TimeProvider` + `ICurrentUser`, aggregate-root-aware (a child change stamps the root), via the entry API (post-DetectChanges).

## Consequences

- The domain stays EF-free; mapping lives entirely in Persistence configurations + interceptors.
- Soft-delete + auditing are automatic and centralized.
- Domain-event dispatch was in-transaction pre-commit; a **transactional outbox** was named as the production evolution for side-effecting handlers. ADR-0018 delivered it and replaced the dispatch interceptor; ADR-0023 refines the processor.

## Amendment (2026-10-11): the aggregate boundary, enforced

### Why

Each aggregate maps Postgres `xmin` as its concurrency token, but the token sits on the root's row, and
EF writes that row only when a root column changes. A change to the children alone (`AddLine` on a
loaded invoice) reached the root's UPDATE only because `AuditingInterceptor` stamps `IAuditable` roots;
the first aggregate without `IAuditable` — or without a token at all — would have let two concurrent
edits of one aggregate both commit, silently.

### Decision

- **Every aggregate root has a concurrency token.** `AggregateConcurrencyTests` walks the model and
  fails on an `IHasDomainEvents` entity type without one, or without a plain column the promotion
  below can write back.
- **A child-only change promotes the root.** `AggregateConcurrencyInterceptor` marks one real, non-key,
  non-generated column of every `Unchanged` root with an added, modified or deleted child as modified
  (writing its current value back), so the root's UPDATE checks and bumps the token. A child is an
  entity the root reaches through a navigation, followed up through grandchildren; a foreign key to
  another aggregate without such a navigation promotes nothing. It is the fourth interceptor and the
  only one outside `DependencyInjection.cs`: stateless, it is attached by `AppDbContext.OnConfiguring`
  after the host's interceptors, so every context instance runs it.
- **A value object holding another value object types it as one** (`Money.Currency` and
  `Invoice.Currency` are `Currency`, not strings) and maps as a complex type nested in a complex type;
  such a value object keeps a private parameterless constructor, because EF cannot bind a nested
  complex property through a constructor. Columns do not change: the nested property keeps its column
  name.

### Consequences

- A stale write to any aggregate is `Concurrency.Conflict` (409) from `CommitAsync`, whatever the root
  implements; the auditing stamp is no longer load-bearing for concurrency.
- One extra UPDATE of the root per save that changes only children of a non-auditable aggregate — the
  write that carries the token.

