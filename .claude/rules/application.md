---
description: CQRS use-case conventions for the Application layer
paths: ["src/Core/XTemplate.Application/**/*.cs"]
---

# Application layer rules

Depends on Domain + SharedKernel only — never Infrastructure, Persistence, EF, or ASP.NET (arch-test enforced).

- **Vertical slices**: `Features/{Feature}/{UseCase}/` holds the command/query + its handler + its FluentValidation validator, co-located.
- **Commands/queries** implement `ICommand` / `ICommand<TResponse>` / `IQuery<TResponse>` (SharedKernel). **No mediator** — endpoints inject the handler interface directly.
- **Handlers** are `internal sealed`, implement `ICommandHandler<>` / `IQueryHandler<>`, and return `Result`/`Result<T>`. They orchestrate the domain + ports; they contain no business rules (those live in the aggregate).
- **Validators** are `internal sealed : AbstractValidator<T>` — shape checks only (required, length, range). Semantic rules belong in domain value objects. Run automatically via the validation decorator; never call them by hand.
- **Ports** (interfaces the outside world implements) live in `Abstractions/`: `ICurrentUser`, `IEmailSender`, `IIdentityService`, `ITokenProvider`, etc.
- **Domain-event handlers** implement `IDomainEventHandler<TEvent>` in `Features/{Feature}/EventHandlers/`.
- Registration is automatic (Scrutor scan + decorator pipeline in `DependencyInjection.cs`) — no manual DI lines per handler.

Decorator pipeline order: Validation → Logging → UnitOfWork → handler.
