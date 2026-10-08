---
description: Building-block conventions for the SharedKernel
paths: ["src/Core/Trellis.SharedKernel/**/*.cs"]
---

# SharedKernel rules

Every layer depends on this project — **changes here ripple everywhere**. Keep the surface minimal, generic, and dependency-free (BCL + FluentValidation abstractions only). Prefer extending a consuming layer over growing the kernel.

- **Results**: `Result`/`Result<T>` + `Error(Code, Description, Type)` with static factories (`Error.Validation/NotFound/Conflict/Unauthorized/Forbidden/Failure`). `ErrorType` drives the RFC 9457 mapping in the API — pick the factory that matches the HTTP semantics. Implicit conversions exist (`value → Success`, `error → Failure`).
- **CQRS contracts** (`Messaging/`): `ICommand`, `ICommand<TResponse>`, `IQuery<TResponse>` + their handler interfaces. No mediator — handlers are resolved directly.
- **Decorator pipeline** (`Decorators/`): `LoggingDecorators` → `TracingDecorators` → `ValidationDecorators` → `UnitOfWorkDecorators` → handler, each a static class with nested `CommandHandler`/`QueryHandler` wrappers. UnitOfWork wraps **commands only** (queries never save). Logging and Tracing are outermost so a rejected request and a failing commit still get their one outcome line and span. Order is fixed by the Scrutor `TryDecorate` registration in Application's `DependencyInjection` (first call = innermost) — a new decorator slots in there, and `DecoratorPipelineTests` walks the chain and asserts the exact sequence.
- **Domain building blocks** (`Domain/`): `Entity<TId>` (identity equality), `AggregateRoot<TId>` (`Raise(...)` events), `IDomainEvent`, `IUnitOfWork`, `ISoftDeletable`, `IAuditable`.
- **Catch filters key on the token, not only the exception type**: `when (e is not OperationCanceledException || !token.IsCancellationRequested)` with the host/request token (never a linked one) — a downstream timeout is an `OperationCanceledException` too, and it is a fault. `ConventionTests` scans `src/` and `tools/` for the type-only form.
- **The unit of work never throws for an expected outcome.** `IUnitOfWork.CommitAsync` returns `Result`; a stale concurrency token is `UnitOfWorkErrors.ConcurrencyConflict` (409 via `CustomResults`), propagated by the UnitOfWork decorator like any other failure. Nothing in Presentation catches EF exceptions.
- **`DomainEventNameAttribute` is required on every domain event** — the stable, versioned wire name (`"invoices.paid.v1"`) stored with outbox rows so renames never orphan in-flight messages.
- **Strongly-typed IDs**: `IStronglyTypedId<TSelf>` + `StronglyTypedIdJsonConverterFactory` (serializes as the bare Guid). New ID types need no kernel change.
- **`Specification<T>`** (ADR-0017): Where (AND-composed), OrderBy/ThenBy, paging — deliberately no Includes (aggregates load whole). Needs beyond this shape mean switching to Ardalis.Specification, not growing this class.
