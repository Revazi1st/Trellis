# Architecture

Trellis is a rich-DDD, Clean Architecture .NET 10 solution. Projects are grouped by architectural ring; features are vertical slices inside the layers.

## Dependency flow (enforced by `Trellis.ArchitectureTests`)

```
                 ┌─────────────── Core ───────────────┐
 SharedKernel ◄── Domain ◄── Application ◄──┐
                                            │
        ┌──────── Infrastructure ───────────┤   (Infrastructure ∥ Persistence:
        │  Infrastructure   Persistence ─────┘    peers, never reference each other — ADR-0002)
        │
        └──────── Presentation ────────────────
             Api ──► Api.Common ──► Api.Contracts
             Api ──► Api.Contracts ◄── UI.Common ◄── UI.Web
             Shared: ServiceDefaults (Aspire) — referenced by hosts
```

Rule of thumb: **dependencies point inward.** The Domain knows nothing about EF, HTTP, or DI. Application defines *ports* (interfaces); Infrastructure and Persistence implement them.

## The rings

| Ring | Projects | Responsibility |
|---|---|---|
| **Core** | SharedKernel, Domain, Application | Business rules + use cases. Pure C#. |
| **Infrastructure** | Infrastructure, Persistence | Driven adapters (email/JWT; EF Core/Identity). Peers. |
| **Presentation** | Api(.Common/.Contracts), UI(.Common/.Web) | Driving adapters: HTTP API + Blazor UI. |
| **Shared** | ServiceDefaults | Aspire cross-cutting (OTel, health, discovery, resilience). |
| **tools** | AppHost, MigrationService | Aspire orchestration + DB migrate/seed worker. |

## Key patterns

- **CQRS without a mediator** — hand-rolled `ICommandHandler`/`IQueryHandler` + a decorator pipeline (logging → tracing → validation → unit-of-work). Endpoints inject handlers directly. (ADR-0003)
- **Result over exceptions** — operations return `Result`/`Result<T>`; the API maps errors to RFC 9457 problem details at one seam, every problem carrying a stable `code`. (ADR-0004, ADR-0024)
- **Secure by default** — every API endpoint requires authorization unless it is on the anonymous allowlist; hardening (forwarded-header trust, security headers, rate limits, request limits) lives in one place. (ADR-0021)
- **OpenAPI as a contract** — one generated document per API version, committed at `docs/api/openapi.v1.json` and compared with the served one on every test run. (ADR-0025)
- **Rich domain** — aggregates with factories + private EF constructors, value objects as EF complex types, hand-rolled strongly-typed IDs. (ADR-0005, ADR-0006)
- **Transactional outbox** — a SaveChanges interceptor writes each domain event to `OutboxMessages` in the same transaction as the aggregate change; `OutboxProcessor` (a background service in the API) dispatches them after commit, at least once, crediting each handler under its `[DomainEventHandlerName]` so a retry never re-runs one that succeeded. Nothing dispatches during `SaveChanges`. (ADR-0018, ADR-0023)
- **Idempotent writes** — a write endpoint's `Idempotency-Key` reservation commits with the command's unit of work, and a retry replays the stored response. (ADR-0018)
- **Auth** — ASP.NET Core Identity + JWT with permission claims; permission-based authorization; single-use refresh tokens. (ADR-0007)
- **Shared UI library** — a render-mode-agnostic Razor Class Library (`UI.Common`) holds the pages/components/typed client, consumed by the Blazor web host. (ADR-0015)

Decisions and their rationale live in [`docs/adr/`](adr/). The canonical feature to copy is [Invoices](features/invoices.md).
