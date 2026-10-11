# ADR-0003: Hand-rolled CQRS handlers with a decorator pipeline (no mediator)

- **Status**: accepted
- **Date**: 2026-07-14
- **Amended**: 2026-10-08 — pipeline order and outcome logging (see *Amendment* below)
- **Amended**: 2026-10-11 — Tracing wraps Logging (see the second *Amendment* below)
- **Amended**: 2026-10-11 — `ErrorType.PreconditionFailed` joins the Information set and `ErrorType.Unavailable` logs at Warning; ADR-0028's second amendment owns the levels (see the note under the first *Amendment*)

## Context

CQRS use cases need a dispatch mechanism. MediatR is the mainstream choice, but MediatR ≥13 is commercially licensed (ADR-0011), and even the free tier forces a license-key flow on every template consumer. The template also aims to be maximally legible to AI agents — an explicit call graph beats reflection-based dispatch.

## Decision

Hand-roll the CQRS contracts in `SharedKernel.Messaging`: `ICommand`, `ICommand<TResponse>`, `IQuery<TResponse>`, and `ICommandHandler<>`/`IQueryHandler<>` returning `Result`/`Result<T>`. **Endpoints inject the handler interface directly — there is no mediator/dispatcher.**

Cross-cutting concerns are DI-registered open-generic decorators (`SharedKernel.Decorators`) wrapping the handler; the order first recorded here ran validation outermost, then logging, then the unit of work. Registration is automatic via **Scrutor** (`.Scan(...)` for handlers + `.Decorate(...)` for the pipeline) in `Application/DependencyInjection.cs` — one place, no per-handler DI lines. *(Order superseded by the 2026-10-08 amendment below: **Logging → Tracing → Validation → UnitOfWork → handler**.)*

## Consequences

- Zero mediator dependency and zero license exposure; the pipeline is ~4 small classes anyone can read.
- The one place DIY would be worse than a library — open-generic decoration — uses Scrutor (MIT, ~40KB), not hand-rolled.
- Adding a use case = a command/query + a sealed handler + a validator; it is picked up by the scan with no wiring (the `add-feature` skill scaffolds it).
- Direct handler injection makes the call graph explicit for humans and AI agents.

## Amendment (2026-10-08): Logging → Tracing → Validation → UnitOfWork → handler

The original order above put Validation outermost, so a rejected request returned before Logging and Tracing ran: no log line, no span. The pipeline is now **Logging → Tracing → Validation → UnitOfWork → handler** (queries skip UnitOfWork), registered with `TryDecorate` so a project with no queries (or no commands) still boots. Every invocation writes exactly one outcome line and one span; the log level follows the outcome class — Information for success and for classified expected failures (Validation, NotFound, Conflict, Unauthorized, Forbidden), Warning for an unclassified `ErrorType.Failure`, Error only for an exception that ends the use case. `IUnitOfWork.CommitAsync` returns `Result`, so a stale concurrency token travels through the pipeline as a Conflict outcome instead of an exception (ADR-0004). The decision above — hand-rolled contracts, Scrutor decoration, direct handler injection — is unchanged.
*(Amended 2026-10-11: the two `ErrorType`s added since, `PreconditionFailed` and `Unavailable`, log at Information and Warning respectively; ADR-0028's second amendment says why.)*

## Amendment (2026-10-11): Tracing → Logging → Validation → UnitOfWork → handler

The 2026-10-08 order put Logging outside Tracing, so the use-case span had already stopped when the
outcome line was written: the line linked to the HTTP request's span instead of the use case's, and a
trace view could not show it under the use case. Tracing now wraps Logging: **Tracing → Logging →
Validation → UnitOfWork → handler** (queries skip UnitOfWork). Both stages still sit outside Validation
and the commit, so a rejected request and a failing commit keep their span and their one outcome line.
`DecoratorPipelineTests` asserts the sequence; `UseCaseOutcomeTests` asserts that the outcome line
carries the use-case span's id. What the line and the span record is ADR-0028's.
