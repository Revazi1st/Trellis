---
description: Adapter conventions for the Infrastructure layer (non-persistence)
paths: ["src/Infrastructure/Trellis.Infrastructure/**/*.cs"]
---

# Infrastructure layer rules

Peer of Persistence — **never reference `Trellis.Persistence`** (ADR-0002, arch-test enforced). Anything touching EF/the database belongs in Persistence, not here.

- This project is **adapters for Application ports only** (`Abstractions/`): `JwtTokenProvider : ITokenProvider`, `LoggingEmailSender : IEmailSender`, `TimeProvider.System`. No business rules, no orchestration — those live in Application/Domain.
- Adapters are `internal sealed`; the only public surface is `DependencyInjection.AddInfrastructure`.
- **Options are bound from configuration in `AddInfrastructure`** (e.g. `AuthTokenOptions` from its `SectionName`) and registered as singletons — adapters take the options type, never `IConfiguration`.
- **Every options class carries DataAnnotations** (`[Required]`, `[Url]`, `[Range]`) and is bound with `AddOptions<T>().Bind(...).ValidateDataAnnotations()`. Never `Get<T>() ?? new T()` — that turns a missing section into silently-defaulted behaviour. Add `.Validate(predicate, message)` when the annotation's own message wouldn't tell you what to do about it.
- **`ValidateOnStart` is host policy, not adapter wiring.** `AddInfrastructure` binds and annotates; a host opts into boot-time failure (`RequireValidTokenOptions()`). The migration service shares this registration and must not need a JWT secret to run migrations.
- **Outbound HTTP** goes in `ExternalApis/<Provider>/` as a typed client behind an Application port returning `Result` (ADR-0020). Two rules that are not optional: set `HttpClient.Timeout = Timeout.InfiniteTimeSpan` (the resilience pipeline owns timeouts), and override `AddStandardResilienceHandler` **per client** — ServiceDefaults' blanket handler cancels every attempt at 10s. Catch `HttpRequestException`, timeout-`TaskCanceledException` (only when the caller did not cancel) and `JsonException` at the boundary.
- **External health checks cache their verdict and probe cheaply** (a TCP connect, not an application call), are registered as singletons, and report **Degraded** when the third party is down vs **Unhealthy** when our own config is wrong.
- `LoggingEmailSender` is the dev stand-in — a real mail provider is a new adapter in `Email/` behind the same `IEmailSender` port, nothing else changes.
- Keep it thin: one folder per concern (`Authentication/`, `Email/`), one adapter per port. If a class grows its own domain logic, it is in the wrong ring.
