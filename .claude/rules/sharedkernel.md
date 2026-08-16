---
description: Building-block conventions for the SharedKernel
paths: ["src/Core/XTemplate.SharedKernel/**/*.cs"]
---

# SharedKernel rules

Every layer depends on this project — **changes here ripple everywhere**. Keep the surface minimal, generic, and dependency-free (BCL + FluentValidation abstractions only). Prefer extending a consuming layer over growing the kernel.

- **Results**: `Result`/`Result<T>` + `Error(Code, Description, Type)` with static factories (`Error.Validation/NotFound/Conflict/Unauthorized/Forbidden/Failure`). `ErrorType` drives the RFC 9457 mapping in the API — pick the factory that matches the HTTP semantics. Implicit conversions exist (`value → Success`, `error → Failure`).
- **CQRS contracts** (`Messaging/`): `ICommand`, `ICommand<TResponse>`, `IQuery<TResponse>` + their handler interfaces. No mediator — handlers are resolved directly.
- **Decorator pipeline** (`Decorators/`): `ValidationDecorators` → `LoggingDecorators` → `UnitOfWorkDecorators`, each a static class with nested `CommandHandler`/`QueryHandler` wrappers. UnitOfWork wraps **commands only** (queries never save). Order is fixed by the Scrutor registration in Application's `DependencyInjection` — a new decorator slots in there, tested by the pipeline-resolution test.
- **Domain building blocks** (`Domain/`): `Entity<TId>` (identity equality), `AggregateRoot<TId>` (`Raise(...)` events), `IDomainEvent`, `IUnitOfWork`, `ISoftDeletable`, `IAuditable`.
- **`DomainEventNameAttribute` is required on every domain event** — the stable, versioned wire name (`"invoices.paid.v1"`) stored with outbox rows so renames never orphan in-flight messages.
- **Strongly-typed IDs**: `IStronglyTypedId<TSelf>` + `StronglyTypedIdJsonConverterFactory` (serializes as the bare Guid). New ID types need no kernel change.
- **`Specification<T>`** (ADR-0017): Where (AND-composed), OrderBy/ThenBy, paging — deliberately no Includes (aggregates load whole). Needs beyond this shape mean switching to Ardalis.Specification, not growing this class.
