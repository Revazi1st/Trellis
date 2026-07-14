---
description: Rich-DDD conventions for the Domain layer
paths: ["src/Core/XTemplate.Domain/**/*.cs"]
---

# Domain layer rules

The domain is pure: **BCL + SharedKernel only** — never reference EF Core, ASP.NET, Application, Infrastructure, or Persistence (arch-test enforced).

- **Aggregates** derive from `AggregateRoot<TId>`; entities from `Entity<TId>`. One aggregate per consistency boundary.
- **Construction**: a `static Create(...)` factory returning `Result<T>` (validates invariants) + a `private` parameterless constructor for EF. No public constructors, no public setters — mutate only through intention-revealing methods.
- **State changes** return `Result`/`Result<T>` and `Raise(...)` a domain event when something noteworthy happened. Never throw for expected rule violations — return an `Error` from the aggregate's `*Errors` class.
- **Value objects** are `sealed record`s with a `static Create(...) → Result<T>`; no `ValueObject` base class (records give value equality). Map them as EF complex types in Persistence.
- **Strongly-typed IDs**: `readonly record struct XId(Guid Value) : IStronglyTypedId<XId>, IParsable<XId>` with `New() => Guid.CreateVersion7()`. Register the EF converter in `AppDbContext.ConfigureConventions`.
- **Specifications** live here: one `static` factory per query shape on a `{Aggregate}Specs` class, returning `Specification<T>`.

Reference: `Invoice` (`src/Core/XTemplate.Domain/Invoices/`). Copy its shape. See `docs/features/invoices.md`.
