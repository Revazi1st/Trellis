# ADR-0003: Hand-rolled CQRS handlers with a decorator pipeline (no mediator)

- **Status**: accepted
- **Date**: 2026-07-14

## Context

CQRS use cases need a dispatch mechanism. MediatR is the mainstream choice, but MediatR ≥13 is commercially licensed (ADR-0011), and even the free tier forces a license-key flow on every template consumer. The template also aims to be maximally legible to AI agents — an explicit call graph beats reflection-based dispatch.

## Decision

Hand-roll the CQRS contracts in `SharedKernel.Messaging`: `ICommand`, `ICommand<TResponse>`, `IQuery<TResponse>`, and `ICommandHandler<>`/`IQueryHandler<>` returning `Result`/`Result<T>`. **Endpoints inject the handler interface directly — there is no mediator/dispatcher.**

Cross-cutting concerns are DI-registered open-generic decorators (`SharedKernel.Decorators`): **Validation → Logging → UnitOfWork → handler**. Registration is automatic via **Scrutor** (`.Scan(...)` for handlers + `.Decorate(...)` for the pipeline) in `Application/DependencyInjection.cs` — one place, no per-handler DI lines.

## Consequences

- Zero mediator dependency and zero license exposure; the pipeline is ~4 small classes anyone can read.
- The one place DIY would be worse than a library — open-generic decoration — uses Scrutor (MIT, ~40KB), not hand-rolled.
- Adding a use case = a command/query + a sealed handler + a validator; it is picked up by the scan with no wiring (the `add-feature` skill scaffolds it).
- Direct handler injection makes the call graph explicit for humans and AI agents.
