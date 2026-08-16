---
description: Adapter conventions for the Infrastructure layer (non-persistence)
paths: ["src/Infrastructure/Trellis.Infrastructure/**/*.cs"]
---

# Infrastructure layer rules

Peer of Persistence — **never reference `Trellis.Persistence`** (ADR-0002, arch-test enforced). Anything touching EF/the database belongs in Persistence, not here.

- This project is **adapters for Application ports only** (`Abstractions/`): `JwtTokenProvider : ITokenProvider`, `LoggingEmailSender : IEmailSender`, `TimeProvider.System`. No business rules, no orchestration — those live in Application/Domain.
- Adapters are `internal sealed`; the only public surface is `DependencyInjection.AddInfrastructure`.
- **Options are bound from configuration in `AddInfrastructure`** (e.g. `AuthTokenOptions` from its `SectionName`) and registered as singletons — adapters take the options type, never `IConfiguration`.
- `LoggingEmailSender` is the dev stand-in — a real mail provider is a new adapter in `Email/` behind the same `IEmailSender` port, nothing else changes.
- Keep it thin: one folder per concern (`Authentication/`, `Email/`), one adapter per port. If a class grows its own domain logic, it is in the wrong ring.
