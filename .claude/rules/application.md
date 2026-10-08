---
description: CQRS use-case conventions for the Application layer
paths: ["src/Core/Trellis.Application/**/*.cs"]
---

# Application layer rules

Depends on Domain + SharedKernel only — never Infrastructure, Persistence, EF, or ASP.NET (arch-test enforced).

- **Vertical slices**: `Features/{Feature}/{UseCase}/` holds the command/query + its handler + its FluentValidation validator, co-located.
- **Commands/queries** implement `ICommand` / `ICommand<TResponse>` / `IQuery<TResponse>` (SharedKernel). **No mediator** — endpoints inject the handler interface directly.
- **Handlers** are `internal sealed`, implement `ICommandHandler<>` / `IQueryHandler<>`, and return `Result`/`Result<T>`. They orchestrate the domain + ports; they contain no business rules (those live in the aggregate).
- **Validators** are `internal sealed : AbstractValidator<T>` — shape checks only (required, length, range). Semantic rules belong in domain value objects. Run automatically via the validation decorator; never call them by hand.
- **Ports** (interfaces the outside world implements) live in `Abstractions/`: `ICurrentUser`, `IEmailSender`, `IIdentityService`, `ITokenProvider`, etc.
- **Domain-event handlers** implement `IDomainEventHandler<TEvent>` in `Features/{Feature}/EventHandlers/` and carry a unique `[DomainEventHandlerName("feature.event.reaction")]` — the key the outbox credits them under; never change it while messages are in flight. They run at-least-once: honour the `CancellationToken`, do unit-of-work writes **before** external side effects, and never call `SaveChanges` yourself — the outbox commits your writes.
- Registration is automatic (Scrutor scan + decorator pipeline in `DependencyInjection.cs`) — no manual DI lines per handler.

Decorator pipeline: Logging → Tracing → Validation → UnitOfWork → handler (queries skip UnitOfWork). Every invocation writes one outcome line in the `UseCase` scope: Information for success and for classified expected failures, Warning for `ErrorType.Failure`, Error only when the use case throws. Handlers do not log their own outcomes.
