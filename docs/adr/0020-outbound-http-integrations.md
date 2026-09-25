# ADR-0020: Outbound HTTP integrations — typed client, per-client resilience, validated options

- **Status**: accepted
- **Date**: 2026-09-25

## Context

Every non-trivial service eventually calls something it does not own, and the template said nothing about how. `Trellis.Infrastructure` held exactly two adapters (a JWT minter and a logging email stub), so the first third-party integration in any generated project was a blank page — and the defaults on that page are unhelpful in ways that only show up in production:

- `ServiceDefaults` applies `AddStandardResilienceHandler()` to **every** `HttpClient`. Its per-attempt timeout is **10 seconds**. For a fast JSON API that is fine; for anything slower it cancels every attempt regardless of what `HttpClient.Timeout` says, and the resulting `TaskCanceledException` looks like a caller cancellation rather than a misconfiguration.
- Leaving `HttpClient.Timeout` at its 100s default while a resilience pipeline is attached caps the **whole** pipeline, retries included, and races the per-attempt timeout.
- A third party's exceptions are transport concerns. Letting `HttpRequestException` escape into Application code contradicts ADR-0004 (Result, not exceptions) at exactly the boundary where failure is most expected.
- Configuration for an integration (a base URL, a timeout) is the kind of thing that is wrong in one environment only, and `Get<T>() ?? new T()` turns that into a silently-defaulted runtime failure far from the cause.
- A health check that calls a dependency on every probe hands anyone who can reach `/health` a free load lever against that dependency — and against whatever rate limit governs it.

## Decision

A reference outbound integration ships in `src/Infrastructure/Trellis.Infrastructure/ExternalApis/ExchangeRates/`, and it is the shape to copy:

- **A port in Application, an adapter in Infrastructure.** `IExchangeRateProvider` returns `Result<decimal>`; `ExchangeRateClient` is an `internal sealed` typed `HttpClient` that converts every transport outcome — error status, timeout, transport failure, unreadable body — into an `Error`. Nothing a third party does reaches Application code as an exception.
- **Resilience is configured per client, not inherited.** The client sets `HttpClient.Timeout = Timeout.InfiniteTimeSpan` (the pipeline owns timeouts) and overrides the attempt timeout, total timeout, breaker sampling window and retry count from its own options. `MaxRetries` has a floor of 1 because the standard handler validates `MaxRetryAttempts >= 1`.
- **Options are annotated and validated.** `ExchangeRateOptions` carries `[Required]`, `[Url]` and `[Range]`, bound with `ValidateDataAnnotations().ValidateOnStart()`. A malformed URL fails the host at boot, not on the first call.
- **The health check probes cheaply and caches.** `ExchangeRateHealthCheck` opens a bare TCP connection (no application request), caches the verdict for 30s, and distinguishes **Degraded** (third party unreachable — do not pull a healthy instance out of rotation over someone else's outage) from **Unhealthy** (our own configuration is wrong — scream). It must be registered as a singleton or the cache is meaningless.

The same options discipline now covers `AuthTokenOptions`. Binding and annotations live in `AddInfrastructure`; promoting them to a boot failure is a separate opt-in, `RequireValidTokenOptions()`, called only by the API. That split is deliberate: `ValidateOnStart` is host policy, and the migration service shares the Infrastructure registration but must not refuse to run EF migrations for want of a JWT secret it never uses.

## Consequences

- The first integration in a generated project is a copy-and-adapt, not a research task — including the two traps (blanket attempt timeout, `HttpClient.Timeout` capping the pipeline) that are invisible until load or latency finds them.
- `Trellis.Infrastructure` takes on `Microsoft.Extensions.Http`, `.Http.Resilience`, `.Diagnostics.HealthChecks` and `.Options.DataAnnotations` — all Microsoft, all within the ADR-0011 license ceilings.
- `/health` gains an `exchange-rates` entry tagged `external`. Readiness can now report Degraded, which orchestrators treat as still-serving; `/alive` is unaffected because nothing here is tagged `live`.
- The sample provider is part of the reference material, like the Invoices slice: delete it when the real integration replaces it.
