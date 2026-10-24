---
description: Rich-DDD conventions for the Domain layer
paths: ["src/Core/Trellis.Domain/**/*.cs"]
---

# Domain layer rules

The domain is pure: **BCL + SharedKernel only** — never reference EF Core, ASP.NET, Application, Infrastructure, or Persistence, nor use the packages the SharedKernel carries (FluentValidation, the Microsoft.Extensions DI and logging abstractions), which the Domain compiles against through its SharedKernel reference. `LayerDependencyTests` enforces it as an allow-list: `System.*`, the SharedKernel, the Domain itself.

- **Aggregates** derive from `AggregateRoot<TId>`; entities from `Entity<TId>`. One aggregate per consistency boundary, and one aggregate changed per command: a rule that must hold across two aggregates at once means the boundary is drawn wrong, and a change that may follow in another aggregate is a domain-event reaction, which loads that aggregate by id, calls one of its `Result` methods and never calls `SaveChanges` (the outbox commits it; `application.md`).
- **Construction**: a `static Create(...)` factory returning `Result<T>` (validates invariants) + a `private` parameterless constructor for EF. No public constructors, no public setters — mutate only through intention-revealing methods.
- **State changes** return `Result`/`Result<T>` and `Raise(...)` a domain event when something noteworthy happened. Never throw for expected rule violations — return an `Error` from the aggregate's `*Errors` class.
- **A transition checks before it changes.** Every guard runs before the first assignment, so a refused call returns its `Error`, leaves every field as it was and raises no event. A terminal state refuses every further action with a `Conflict` that names the state (`Invoice.AlreadyFinalized`), never a silent success. `InvoiceTests` pins both for every transition (`Failed_transitions_raise_no_domain_events`, `Every_transition_on_a_voided_invoice_is_rejected`); a new aggregate gets the same two theories.
- **Value objects** are `sealed record`s with a `static Create(...) → Result<T>`; no `ValueObject` base class (records give value equality). `Create` is the only way in: no public constructor (so no positional record) and no public `set` or `init` accessor, which `with` would use to skip its checks (`ConventionTests`). Map them as EF complex types in Persistence. A value object that holds another (`Money` holds `Currency`) types the member as that value object, not as its raw string, and keeps a `private` parameterless constructor: EF cannot bind a nested complex property through a constructor. A single-value value object implements `IScalarValueObject<TSelf, TValue>` (`EmailAddress`, `Currency`), with `Restore` implemented explicitly so it is never a public way around `Create`; that is what lets Persistence map it as an indexable column when one is needed.
- **What a value object guarantees is its shape, not a policy.** `Currency` accepts any three ASCII letters (ISO 4217's form, not its list: an allow-list a provider imposes is checked at that boundary), `EmailAddress` refuses what mail systems refuse and never checks that an address is deliverable, `Money` enforces the currency's minor units and `Money.MaxAmount`. Make a value a value object when it has rules of its own or travels between aggregates (an amount, a currency, an e-mail address); a value with no rule stays a primitive.
- **Limits are Domain constants** (`Invoice.CustomerNameMaxLength`, `InvoiceLine.DescriptionMaxLength`, `EmailAddress.MaxLength`, `Currency.CodeLength`): the factory or transition checks them and returns a `*TooLong` error, and the validator, the EF configuration and the UI form use the same constant instead of a second literal. A limit that only a validator knows is bypassed by every caller without one (the seeder, an event handler).
- **Strongly-typed IDs**: `readonly record struct XId(Guid Value) : IStronglyTypedId<XId>, IParsable<XId>` with `New() => Guid.CreateVersion7()`. Register the EF converter in `AppDbContext.ConfigureConventions`.
- **Domain events** are `sealed record`s with **`[DomainEventName("feature.event.vN")]`** — the stable wire name stored with outbox rows (renames must never orphan in-flight messages). Members must be primitives, `Guid`, `DateTimeOffset`, or strongly-typed IDs: the serialization fitness test (`DomainEventSerializationTests`) round-trips every event through the outbox serializer and fails on anything exotic. An event carries what its handlers need, a business instant included when one does (`MarkPaid(paidAtUtc)` already receives it); `IDomainEvent` has no timestamp of its own. A member added once rows of the old shape may be in flight is a new wire name (`invoices.paid.v2`).
- **Specifications** live here: one `static` factory per query shape on a `{Aggregate}Specs` class, returning `Specification<T>`. End every ordering with `OrderBy(x => x.Id)` — the unique tiebreaker that keeps pages stable (`ListPagedAsync` refuses a specification without it).

**Picking an `ErrorType`**, by what the caller can do about it (the outcome line's level follows the type, `application.md`):

| `ErrorType` | When | HTTP |
|---|---|---|
| `Validation` | the request cannot succeed as sent: a value's shape or range, or an invariant the change would break (`Invoice.DueDateInPast`, `Invoice.NoLines`, `Money.TooManyDecimalPlaces`) | 400 |
| `NotFound` | nothing this caller may see has that id: missing, soft-deleted, or filtered out for this caller | 404 |
| `Conflict` | a valid request the aggregate's current state forbids (`Invoice.NotDraft`, `Invoice.AlreadyFinalized`), or a duplicate | 409 |
| `PreconditionFailed` | the caller named a version the aggregate has moved on from (`Invoice.VersionMismatch`) | 412 |
| `Unauthorized` / `Forbidden` | the caller is not who it claims, or is known and may not do this; Auth's own (endpoints refuse a missing permission themselves) | 401 / 403 |
| `Unavailable` | something it depends on is down for now and the same request may succeed later; a port's, never an aggregate's | 503 |
| `Failure` | none of the above: our fault, or a dependency that refused us; no retry by the caller helps | 500 |

Reference: `Invoice` (`src/Core/Trellis.Domain/Invoices/`). Copy its shape. See `docs/features/invoices.md`.
