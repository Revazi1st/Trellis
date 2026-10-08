# ADR-0006: EF Core 10 value-object mapping + SaveChanges interceptors

- **Status**: accepted — the in-transaction domain-event dispatch (interceptor 1 below) is superseded by ADR-0018 (transactional outbox); the value-object mapping, soft-delete and auditing stand
- **Date**: 2026-07-14

## Context

The persistence layer must map rich-DDD building blocks — value objects, domain events, auditing, soft-delete — without leaking EF into the domain.

## Decision

- **Value objects as EF complex types** (`ComplexProperty`), the EF 10-recommended mapping (value semantics; not owned-entity identity). `Money`/`EmailAddress` map this way in `Configurations/`.
- **Aggregates load whole**: child navigations use `AutoInclude()` + field access; there are no partial-aggregate loads.
- **Three `ISaveChangesInterceptor`s**, order fixed in `Persistence/DependencyInjection.cs`:
  1. A domain-event dispatch interceptor — dispatched aggregate events inside `SaveChanges` (draining to quiescence). *(Superseded by ADR-0018: `OutboxWriteInterceptor` now takes this slot and writes each event to `OutboxMessages` in the same transaction; handlers run after commit, never during `SaveChanges`.)*
  2. `SoftDeleteInterceptor` — turns `Remove` of `ISoftDeletable` into a `DeletedAtUtc` update, rescues cascade-deleted children; paired with the EF 10 named `"SoftDeleted"` query filter.
  3. `AuditingInterceptor` — stamps `IAuditable` from `TimeProvider` + `ICurrentUser`, aggregate-root-aware (a child change stamps the root), via the entry API (post-DetectChanges).

## Consequences

- The domain stays EF-free; mapping lives entirely in Persistence configurations + interceptors.
- Soft-delete + auditing are automatic and centralized.
- Domain-event dispatch was in-transaction pre-commit; a **transactional outbox** was named as the production evolution for side-effecting handlers. ADR-0018 delivered it and replaced the dispatch interceptor; ADR-0023 refines the processor.
