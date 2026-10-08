# Feature: Exchange rates (reference outbound integration)

The template's worked example of depending on an HTTP API you do not own (ADR-0020). It exists to be
copied: the interesting part is not currency conversion, it is the wiring around the call.

Like the Invoices slice, this is reference material — delete it once a real integration replaces it.

## Port (`src/Core/Trellis.Application/Abstractions/`)

`IExchangeRateProvider.GetRateAsync(baseCurrency, quoteCurrency, ct)` → `Result<decimal>`.
Failures are `ExchangeRateErrors.Unavailable`, `.MalformedResponse` and `.UnknownCurrency(code)`.
Nothing the third party does reaches Application code as an exception (ADR-0004) — except the
caller's own cancellation, which propagates as `OperationCanceledException`.

## Adapter (`src/Infrastructure/Trellis.Infrastructure/ExternalApis/ExchangeRates/`)

| File | Role |
|---|---|
| `ExchangeRateOptions` | `BaseUrl` / `TimeoutSeconds` / `MaxRetries`, annotated and validated at boot |
| `ExchangeRateClient` | `internal sealed` typed `HttpClient`; maps every transport and pipeline outcome to a `Result` |
| `ExchangeRateHealthCheck` | Cached TCP probe; Degraded when unreachable, Unhealthy when misconfigured |

Registered by `AddOutboundIntegrations(configuration)` — **the API only**, not `AddInfrastructure`.
The migration service never calls a third party, so nothing in an `ExternalApis` section can stop it
from running migrations. Registration alone would: the standard resilience handler validates its own
options at boot, and this client's are computed from `ExchangeRateOptions`.

## Behavior

- **Resilience is replaced per client, not added.** `ServiceDefaults` attaches a standard resilience
  handler to every `HttpClient` (10 s per attempt, 30 s total, 3 retries). A second
  `AddStandardResilienceHandler(...)` on its own nests *inside* that one: the blanket 10 s still cancels
  every attempt and each outer retry reruns the client's whole retry loop. So the client first calls
  `RemoveAllResilienceHandlers()` (experimental, `EXTEXP0001` suppressed with the reason), then adds its
  own handler. Its chain holds exactly one `ResilienceHandler`.
- **The pipeline reads the options lazily.** `AddStandardResilienceHandler().Configure((o, sp) => …)`
  resolves the validated `ExchangeRateOptions` when the pipeline is built — no snapshot bound at
  registration time.
- **The total budget includes back-off.** Retries wait exponentially with jitter from 2 s, each wait
  capped at 5 s; the total timeout is `TimeoutSeconds × (MaxRetries + 1) + 5 s × MaxRetries` (40 s on the
  defaults), so the total timeout never cuts off a retry the policy granted. A provider's `Retry-After`
  is honoured; one longer than the remaining budget ends the call as `Unavailable`.
- **Timeouts belong to the pipeline, not the client.** `HttpClient.Timeout` is set to
  `Timeout.InfiniteTimeSpan` deliberately — leaving the 100s default would cap the whole pipeline
  including retries and race the per-attempt timeout. `MaxRetries` has a floor of 1: the handler
  validates `MaxRetryAttempts >= 1` and throws at first use on 0.
- **What the adapter catches** — the pipeline's exceptions, not a bare `HttpClient`'s:
  `HttpRequestException` (transport), Polly's `TimeoutRejectedException` (attempt timeout or total
  budget — *not* `TaskCanceledException`), `BrokenCircuitException` (open circuit, nothing sent) and any
  other `ExecutionRejectedException` (the concurrency limiter) → `Unavailable`; `JsonException` →
  `MalformedResponse`; an `OperationCanceledException` the caller did not cause → `Unavailable`. The
  caller's own cancellation is not caught.
- **A 200 can still be a failure.** An object body with `"success": false` (providers that wrap their
  errors in a 200) → `Unavailable`; Frankfurter's array is read normally; any other shape — v1's object
  among them, or a quote without a rate — → `MalformedResponse`. A quote the answer does not contain
  (an empty array included) → `UnknownCurrency`.
- **Configuration fails at boot, not on first call.** A malformed `BaseUrl` stops the API with a
  precise message instead of surfacing later as a timeout or a `NullReferenceException`.
- **The health check cannot be used as a load lever.** It opens a bare TCP connection (no application
  request) and caches the verdict for 30 seconds, so however often `/health` is polled the provider
  sees at most one connect per window. It must stay a singleton or the cache does nothing.
- **Degraded ≠ Unhealthy.** A provider outage reports Degraded so an orchestrator does not pull a
  perfectly serving instance out of rotation over someone else's problem; a malformed `BaseUrl`
  reports Unhealthy because that is our deployment being wrong.

## Configuration

```json
"ExternalApis": {
  "ExchangeRates": {
    "BaseUrl": "https://api.frankfurter.dev/v2/",
    "TimeoutSeconds": 10,
    "MaxRetries": 2
  }
}
```

The default provider is [Frankfurter](https://frankfurter.dev)'s v2 API (ECB reference rates, no access
key), read through `rates?base=EUR&quotes=USD` → `[{"date":"…","base":"EUR","quote":"USD","rate":…}]`:
one element per quote, no date meaning the latest rates. Frankfurter's `/v1` (`latest?base=&symbols=` →
an object of `rates`) is deprecated — it answers with a `Deprecation` header dated 2026-05-18 and a
`successor-version` link to `/v2/rates` — so the adapter moved to v2, which serves the same use case. The
previous default before that, `api.exchangerate.host`, now requires an access key and answers every
keyless call with `success:false` at HTTP 200. How v2 answers a code it does not publish was not probed:
an error status maps to `Unavailable`, an answer without the quote to `UnknownCurrency`. The live smoke
test below is what notices when the provider changes again.

The check appears in `/health` as `exchange-rates`, tagged `external`. It is not tagged `live`, so
`/alive` is unaffected by a provider outage.

## Tests

- `tests/Trellis.ApiTests/ExchangeRateProviderTests.cs` drives the real registration — options binding,
  typed client and resilience pipeline included — with only the socket replaced, and asserts that each
  third-party outcome comes back as a `Result` — the v2 request shape (`rates?base=&quotes=`) is pinned
  on the way: error status, unreadable body, a body of the wrong shape, unknown currency,
  transport failure, `success:false`, a provider slower than the attempt timeout, an open circuit (a
  burst of failures opens the real breaker; the next call sends nothing), 429/503 retried to success.
  The caller's cancellation propagates as `OperationCanceledException`, unretried. The probe is pinned
  twice: Degraded-not-Unhealthy, and the 30 s cache (a moved clock, a listener that disappears).
- `tests/Trellis.ApiTests/ExchangeRateResilienceTests.cs` boots the API's real graph —
  `AddServiceDefaults()` + `AddInfrastructure` + `AddOutboundIntegrations` — and asserts exactly one
  `ResilienceHandler` in the client's chain, a hanging provider answered `Unavailable` within the
  client's own budget (two calls, under the blanket 10 s), a fail-once-then-succeed retry that waits past
  an attempts-only budget, and the boot split: the API rejects a malformed section, starts without one,
  and a migration-shaped host starts with a malformed one.
- `tests/Trellis.ApiTests/ExchangeRateLiveSmokeTests.cs` makes one real call to the default provider —
  opt-in, `RUN_LIVE_SMOKE_TESTS=1`.
