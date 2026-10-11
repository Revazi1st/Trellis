---
description: Rich-DDD conventions for the Domain layer
paths: ["src/Core/Trellis.Domain/**/*.cs"]
---

# Domain layer rules

The domain is pure: **BCL + SharedKernel only** — never reference EF Core, ASP.NET, Application, Infrastructure, or Persistence (arch-test enforced).

- **Aggregates** derive from `AggregateRoot<TId>`; entities from `Entity<TId>`. One aggregate per consistency boundary.
- **Construction**: a `static Create(...)` factory returning `Result<T>` (validates invariants) + a `private` parameterless constructor for EF. No public constructors, no public setters — mutate only through intention-revealing methods.
- **State changes** return `Result`/`Result<T>` and `Raise(...)` a domain event when something noteworthy happened. Never throw for expected rule violations — return an `Error` from the aggregate's `*Errors` class.
- **Value objects** are `sealed record`s with a `static Create(...) → Result<T>`; no `ValueObject` base class (records give value equality). `Create` is the only way in: no public constructor (so no positional record) and no public `set` or `init` accessor, which `with` would use to skip its checks (`ConventionTests`). Map them as EF complex types in Persistence. A value object that holds another (`Money` holds `Currency`) types the member as that value object, not as its raw string, and keeps a `private` parameterless constructor: EF cannot bind a nested complex property through a constructor. A single-value value object implements `IScalarValueObject<TSelf, TValue>` (`EmailAddress`, `Currency`), with `Restore` implemented explicitly so it is never a public way around `Create`; that is what lets Persistence map it as an indexable column when one is needed.
- **Limits are Domain constants** (`Invoice.CustomerNameMaxLength`, `InvoiceLine.DescriptionMaxLength`, `EmailAddress.MaxLength`, `Currency.CodeLength`): the factory or transition checks them and returns a `*TooLong` error, and the validator, the EF configuration and the UI form use the same constant instead of a second literal. A limit that only a validator knows is bypassed by every caller without one (the seeder, an event handler).
- **Strongly-typed IDs**: `readonly record struct XId(Guid Value) : IStronglyTypedId<XId>, IParsable<XId>` with `New() => Guid.CreateVersion7()`. Register the EF converter in `AppDbContext.ConfigureConventions`.
- **Domain events** are `sealed record`s with **`[DomainEventName("feature.event.vN")]`** — the stable wire name stored with outbox rows (renames must never orphan in-flight messages). Members must be primitives, `Guid`, `DateTimeOffset`, or strongly-typed IDs: the serialization fitness test (`DomainEventSerializationTests`) round-trips every event through the outbox serializer and fails on anything exotic.
- **Specifications** live here: one `static` factory per query shape on a `{Aggregate}Specs` class, returning `Specification<T>`. End every ordering with `OrderBy(x => x.Id)` — the unique tiebreaker that keeps pages stable (`ListPagedAsync` refuses a specification without it).

Reference: `Invoice` (`src/Core/Trellis.Domain/Invoices/`). Copy its shape. See `docs/features/invoices.md`.
