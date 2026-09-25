# Feature: Exchange rates (reference outbound integration)

The template's worked example of depending on an HTTP API you do not own (ADR-0020). It exists to be
copied: the interesting part is not currency conversion, it is the wiring around the call.

Like the Invoices slice, this is reference material — delete it once a real integration replaces it.

## Port (`src/Core/Trellis.Application/Abstractions/`)

`IExchangeRateProvider.GetRateAsync(baseCurrency, quoteCurrency, ct)` → `Result<decimal>`.
Failures are `ExchangeRateErrors.Unavailable`, `.MalformedResponse` and `.UnknownCurrency(code)`.
Nothing the third party does reaches Application code as an exception (ADR-0004).

## Adapter (`src/Infrastructure/Trellis.Infrastructure/ExternalApis/ExchangeRates/`)

| File | Role |
|---|---|
| `ExchangeRateOptions` | `BaseUrl` / `TimeoutSeconds` / `MaxRetries`, annotated and validated at boot |
| `ExchangeRateClient` | `internal sealed` typed `HttpClient`; maps every transport outcome to a `Result` |
| `ExchangeRateHealthCheck` | Cached TCP probe; Degraded when unreachable, Unhealthy when misconfigured |

## Behavior

- **Timeouts belong to the pipeline, not the client.** `HttpClient.Timeout` is set to
  `Timeout.InfiniteTimeSpan` deliberately — leaving the 100s default would cap the whole pipeline
  including retries and race the per-attempt timeout.
- **Resilience is overridden per client.** `ServiceDefaults` attaches the standard resilience handler
  to every `HttpClient`, and its per-attempt timeout is **10 seconds**. Anything slower needs its own
  `AddStandardResilienceHandler(...)` block, which is what `AddExchangeRateProvider` does. `MaxRetries`
  has a floor of 1: the handler validates `MaxRetryAttempts >= 1` and throws at first use on 0.
- **Configuration fails at boot, not on first call.** A malformed `BaseUrl` stops the host with a
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
    "BaseUrl": "https://api.exchangerate.host/",
    "TimeoutSeconds": 10,
    "MaxRetries": 2
  }
}
```

The check appears in `/health` as `exchange-rates`, tagged `external`. It is not tagged `live`, so
`/alive` is unaffected by a provider outage.

## Tests

`tests/Trellis.ApiTests/ExchangeRateProviderTests.cs` drives the real registration — options binding,
typed client and resilience pipeline included — with only the socket replaced, and asserts that each
third-party outcome (error status, unreadable body, unknown currency, transport failure) comes back
as a `Result`. One test pins the Degraded-not-Unhealthy semantics of the probe.
