# ADR-0006: EF Core 10 value-object mapping + SaveChanges interceptors

- **Status**: accepted
- **Date**: 2026-07-14

## Context

The persistence layer must map rich-DDD building blocks — value objects, domain events, auditing, soft-delete — without leaking EF into the domain.

## Decision

- **Value objects as EF complex types** (`ComplexProperty`), the EF 10-recommended mapping (value semantics; not owned-entity identity). `Money`/`EmailAddress` map this way in `Configurations/`.
- **Aggregates load whole**: child navigations use `AutoInclude()` + field access; there are no partial-aggregate loads.
- **Three `ISaveChangesInterceptor`s**, order fixed in `Persistence/DependencyInjection.cs`:
  1. `DomainEventDispatchInterceptor` — dispatches aggregate events (drains to quiescence).
  2. `SoftDeleteInterceptor` — turns `Remove` of `ISoftDeletable` into a `DeletedAtUtc` update, rescues cascade-deleted children; paired with the EF 10 named `"SoftDeleted"` query filter.
  3. `AuditingInterceptor` — stamps `IAuditable` from `TimeProvider` + `ICurrentUser`, aggregate-root-aware (a child change stamps the root), via the entry API (post-DetectChanges).

## Consequences

- The domain stays EF-free; mapping lives entirely in Persistence configurations + interceptors.
- Soft-delete + auditing are automatic and centralized.
- Domain-event dispatch is in-transaction pre-commit; a **transactional outbox** is the production evolution for side-effecting handlers, built on top of this interceptor.
