# ADR-0005: Hand-rolled strongly-typed IDs (not Vogen)

- **Status**: accepted
- **Date**: 2026-07-14

## Context

Primitive `Guid` IDs invite mixing (`invoiceId` passed where a `customerId` is expected). Strongly-typed IDs fix that. Vogen is the popular source-generator; but it adds a dependency, source-gen magic, and an EF-converter registration ritual, and its output is opaque to a reader/agent.

## Decision

Hand-roll IDs as `readonly record struct XId(Guid Value) : IStronglyTypedId<XId>, IParsable<XId>` with `New() => new(Guid.CreateVersion7())` and `From(Guid)` — ~12 visible lines each (`Domain/Invoices/InvoiceId.cs`, `InvoiceLineId.cs`). `SharedKernel.Domain` supplies the generic plumbing once: `IStronglyTypedId<TSelf>`, a `StronglyTypedIdJsonConverterFactory`, and (in Persistence) a generic `StronglyTypedIdValueConverter<TId>`. `IParsable` gives minimal-API route binding for free.

Adding an aggregate adds one line to `AppDbContext.ConfigureConventions`. Vogen is documented as the alternative for teams that prefer it.

## Consequences

- Zero dependency, zero source-gen; every line is visible and copyable by humans and AI agents.
- `Guid.CreateVersion7()` gives time-ordered, index-friendly keys.
- Slightly more boilerplate per ID than Vogen — accepted for legibility and no version churn.
