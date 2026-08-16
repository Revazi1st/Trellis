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
             Api ──► Api.Common
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

- **CQRS without a mediator** — hand-rolled `ICommandHandler`/`IQueryHandler` + a decorator pipeline (validation → logging → unit-of-work). Endpoints inject handlers directly. (ADR-0003)
- **Result over exceptions** — operations return `Result`/`Result<T>`; the API maps errors to RFC 9457 problem details at one seam. (ADR-0004)
- **Rich domain** — aggregates with factories + private EF constructors, value objects as EF complex types, hand-rolled strongly-typed IDs, domain events dispatched by an EF SaveChanges interceptor. (ADR-0005, ADR-0006)
- **Auth** — ASP.NET Core Identity + JWT with permission claims; permission-based authorization; single-use refresh tokens. (ADR-0007)
- **Shared UI library** — a render-mode-agnostic Razor Class Library (`UI.Common`) holds the pages/components/typed client, consumed by the Blazor web host. (ADR-0015)

Decisions and their rationale live in [`docs/adr/`](adr/). The canonical feature to copy is [Invoices](features/invoices.md).
