# ADR-0020: Outbound HTTP integrations — typed client, per-client resilience, validated options

- **Status**: accepted
- **Date**: 2026-09-25
- **Amended**: 2026-10-08 — replace (not add) the blanket pipeline, the real exception set, API-only registration, back-off-aware budget, Frankfurter (see *Amendment* below)
- **Amended**: 2026-10-11 — an outage is `Unavailable` (503), a refusal stays `Failure` (see *Amendment (2026-10-11)* below)

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

## Amendment (2026-10-08): replace the blanket pipeline, catch what it throws, register only where it is used

The first version taught three things that were wrong in exactly the situations it was written for.

- **"Override per client" stacked instead of replacing.** `AddStandardResilienceHandler(...)` on a client that `ServiceDefaults` already gave one adds a *second* `ResilienceHandler`, nested inside the first: the blanket 10 s attempt timeout still cancels every attempt, and each outer retry reruns the client's whole retry loop. The client now calls `RemoveAllResilienceHandlers()` first (experimental, `EXTEXP0001` suppressed with the reason; the non-experimental equivalent removes every `ResilienceHandler` in `ConfigureAdditionalHttpMessageHandlers`) and then adds its own. **Replace, don't add** — a test boots `AddServiceDefaults()` with the integration and asserts exactly one `ResilienceHandler` in the chain.
- **The catch set was a bare `HttpClient`'s, not the pipeline's.** Behind the standard handler a timed-out attempt or budget surfaces as Polly's `TimeoutRejectedException` (not `TaskCanceledException`) and an open circuit as `BrokenCircuitException`; both escaped the Result boundary as 500s. The adapter now maps `HttpRequestException`, `TimeoutRejectedException`, `BrokenCircuitException` and any other `ExecutionRejectedException` to `Unavailable`, `JsonException` to `MalformedResponse`, and an `OperationCanceledException` the caller did not cause to `Unavailable`; the caller's own cancellation propagates.
- **`ValidateOnStart` in `AddInfrastructure` reached the migration host — and moving it was not enough.** The standard resilience handler validates its own options at boot, and the client's are computed from the integration's options, so merely *registering* the client makes its section a boot requirement of the host. Outbound integrations are therefore registered by `AddOutboundIntegrations(configuration)`, which only hosts that call out invoke (the API); it validates their options at boot. `AddInfrastructure` keeps what every host needs. The same call validates `Email:Provider` at boot and warns when the logging stand-in is active outside Development; the e-mail sender itself stays in `AddInfrastructure`, because Application's event handlers must be constructible in every host.

Two smaller corrections ride along. The pipeline reads the validated options through `AddStandardResilienceHandler().Configure((o, sp) => …)` when it is built, instead of a `Get<T>() ?? new T()` snapshot bound at registration. Its total timeout now counts the back-off — `attempt × (retries + 1) + maxDelay × retries`, with waits capped at `Retry.MaxDelay` — because a budget of attempts alone (`attempt × (retries + 2)`) cut off a retry the policy had granted whenever the jittered wait ran long. The default provider moved from `api.exchangerate.host`, which now answers `success:false` at HTTP 200 to every keyless call while its TCP probe stays green, to Frankfurter (`https://api.frankfurter.dev/v1/`, same `latest?base=&symbols=` shape); an explicit `success:false` is still rejected, and an opt-in live smoke test (`RUN_LIVE_SMOKE_TESTS=1`) watches the provider itself.

The default provider then moved once more, to Frankfurter's v2 API (`https://api.frankfurter.dev/v2/`,
`rates?base=&quotes=`, an array per quote): v1 answers with a `Deprecation` header and a successor
link. The adapter reads the array, an object with `success:false` stays `Unavailable`, any other shape
is `MalformedResponse` (`docs/features/exchange-rates.md`).

## Amendment (2026-10-11): an outage is `Unavailable`, a refusal stays a `Failure`

`ExchangeRateErrors.Unavailable` was a deliberate `Failure` (500), and it covered two different
situations: the provider being down, and the provider refusing us. ADR-0004 now has
`ErrorType.Unavailable` (503), so the reference port splits them by whether waiting helps:

- **`ExchangeRate.Unavailable`** is an `Unavailable`: a transport failure, an attempt or total timeout,
  an open circuit, a concurrency-limiter rejection, and a 5xx, 408 or 429 still failing after the
  pipeline's retries. A client may retry later.
- **`ExchangeRate.Rejected`** is a `Failure`: the provider answered and refused our credentials or plan
  (401/403), a route it does not serve, meaning a wrong `BaseUrl` or a retired API version (404), or
  said `success:false` in a 200. No wait cures our configuration, so a 503 would mislead.
- `ExchangeRate.MalformedResponse` stays a `Failure`, and `ExchangeRate.UnknownCurrency` a `Validation`.

An endpoint that calls an integration declares the 503 explicitly with a bare
`.Produces(StatusCodes.Status503ServiceUnavailable)`, which the OpenAPI transformer fills with the
`ProblemResponse` body like any other declared status (ADR-0025): the shape of an operation cannot show
that it depends on a third party. No shipped endpoint calls the port yet, so the committed document has
no 503. `Retry-After` is deferred for the reason ADR-0004's amendment gives.

