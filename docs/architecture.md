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

- **CQRS without a mediator** — hand-rolled `ICommandHandler`/`IQueryHandler` + a decorator pipeline (tracing → logging → validation → unit-of-work). Endpoints inject handlers directly. (ADR-0003)
- **Result over exceptions** — operations return `Result`/`Result<T>`; the API maps errors to RFC 9457 problem details at one seam, every problem carrying a stable `code`. (ADR-0004, ADR-0024)
- **Secure by default** — every API endpoint requires authorization unless it is on the anonymous allowlist; hardening (forwarded-header trust, security headers, rate limits, request limits) lives in one place. (ADR-0021)
- **OpenAPI as a contract** — one generated document per API version, committed at `docs/api/openapi.v1.json` and compared with the served one on every test run. (ADR-0025)
- **Rich domain** — aggregates with factories + private EF constructors, value objects as EF complex types, hand-rolled strongly-typed IDs. (ADR-0005, ADR-0006)
- **Transactional outbox** — a SaveChanges interceptor writes each domain event to `OutboxMessages` in the same transaction as the aggregate change; `OutboxProcessor` (a background service in the API) dispatches them after commit, at least once, crediting each handler under its `[DomainEventHandlerName]` so a retry never re-runs one that succeeded. Nothing dispatches during `SaveChanges`. (ADR-0018, ADR-0023)
- **Idempotent writes** — a write endpoint's `Idempotency-Key` reservation commits with the command's unit of work, and a retry replays the stored response. (ADR-0018)
- **Auth** — ASP.NET Core Identity + JWT with permission claims; permission-based authorization; single-use refresh tokens. (ADR-0007)
- **Shared UI library** — a render-mode-agnostic Razor Class Library (`UI.Common`) holds the pages/components/typed client, consumed by the Blazor web host. (ADR-0015)

## Scheduled use cases

The template's own timers are bookkeeping (`OutboxProcessor`, `DataRetentionJanitor`); a feature's will
be business ("flag the invoices that went overdue", "send a reminder"). Give one this shape:

- **A hosted service in the API host, one scope per unit of work.** A `BackgroundService` waits on a
  `PeriodicTimer` built from the injected `TimeProvider` (a test moves the clock instead of sleeping). On
  each tick it opens a DI scope, asks a query handler for the ids that are due (a specification, such as
  `InvoiceSpecs.Overdue(now)`), then gives each id a scope and a command of its own: the job is a driving
  adapter like an endpoint, every command goes through the whole pipeline (span, outcome line,
  validation, one commit), and one aggregate's failure stays its own. The rule lives in the command
  handler, never in the timer; the instant comes from `TimeProvider` and travels in the command. The
  interval is a validated options class (`OptionsConventionTests`), the catch filter keys on the host
  token (`when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)`), and a
  failed tick logs a Warning and waits for the next.
- **Idempotent through the aggregate, not a lock.** Every replica runs the same timer, and a tick can run
  again after a crash. The aggregate's state makes a repeat harmless: the query selects only aggregates
  still in the state the command changes, the transition refuses one that has moved on (a `Conflict`),
  and when two replicas reach the same aggregate together `xmin` lets one commit and answers the other
  `Concurrency.Conflict`. Both mean "already done": nothing to retry, no leader election, no lock table. A
  side effect per aggregate (an e-mail, a call out) goes in an event handler of the transition's event,
  so the outbox delivers it once the change has committed.
<!--#if (UseMultitenancy) -->
- **The tenant is not ambient in a job.** A job has no signed-in user, so no `tenant` claim:
  `ITenantContext.TenantId` is null, and the `"Tenant"` query filter then matches only rows whose
  `TenantId` is NULL, which no tenant's aggregate is. A job that serves tenants iterates them: read the
  tenant ids to serve (the distinct `TenantId` of the aggregate, with `IgnoreQueryFilters(["Tenant"])`),
  then run each tenant's work in its own scope with `AmbientTenant.CurrentTenantId` set to that tenant and
  reset in a `finally`, as `OutboxProcessor` restores a message's tenant. Rows the job creates are then
  stamped with that tenant too.
<!--#endif -->

Decisions and their rationale live in [`docs/adr/`](adr/). The canonical feature to copy is [Invoices](features/invoices.md).
