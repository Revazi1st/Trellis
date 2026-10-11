---
name: add-integration
description: Add a call to a third-party HTTP API (a payment provider, a geocoder, a CRM) the Trellis way by copying the ExchangeRates reference integration. Use when the application must depend on a service it does not own.
disable-model-invocation: false
---

# Add an outbound integration

`ExchangeRates` is the reference (ADR-0020): read `docs/features/exchange-rates.md`, then copy
`src/Core/Trellis.Application/Abstractions/{IExchangeRateProvider,ExchangeRateErrors}.cs`,
`src/Infrastructure/Trellis.Infrastructure/ExternalApis/ExchangeRates/` and the registration in
`Infrastructure/DependencyInjection.cs`. The interesting part is not the call, it is the wiring
around it: the defaults that look harmless are the ones that fail in production. `{Provider}` below
is the third party (`Stripe`), `{Capability}` what the application needs from it (`Payments`).

## 1. The port (Application)

- [ ] `Abstractions/I{Capability}Provider.cs`: what the application needs, in its own words and types,
      returning `Result`/`Result<T>`. No `HttpClient`, no provider DTO, no provider name leaks in.
- [ ] `Abstractions/{Capability}Errors.cs`: at least `Unavailable` (`Error.Unavailable`, a 503: transport,
      timeout, open circuit, the provider's 5xx/429 after the retries), `Rejected` (`Error.Failure`: it
      refused our credentials, plan or URL, which no wait cures) and `MalformedResponse` (an answer the
      adapter cannot read), plus the domain failures the provider can report. Codes are `Area.Problem` (ADR-0024).

## 2. Options (Infrastructure)

- [ ] `ExternalApis/{Provider}/{Provider}Options.cs`: `SectionName = "ExternalApis:{Provider}"`,
      `[Required]`/`[Url]` on `BaseUrl`, `[Range]` on `TimeoutSeconds` and `MaxRetries` (floor 1: the
      standard handler rejects 0; "no retries" means no handler) [`OptionsConventionTests`].
- [ ] Secrets (an API key) come from configuration, never a default in code; development values
      go in user secrets. Add the section to the API's `appsettings.json` without the secret.

## 3. The adapter (Infrastructure)

- [ ] `ExternalApis/{Provider}/{Provider}Client.cs`: an `internal sealed` typed client taking
      `HttpClient` (and a logger), implementing the port. Every outcome becomes a `Result`:
  - **probe the provider's error statuses** (an unknown input, a wrong path, no key) before mapping them;
  - **map each status by the endpoint's contract**, not its class: 400/422 on input is the domain error,
    a 404 on a collection route is our wrong URL and 401/403 our key (`Rejected`, logged at Error),
    408/429/5xx after the retries `Unavailable`. Never forward an upstream 401/403/404 as our own;
  - read a provider's error code from a 4xx body through a private DTO if you branch on it, and never
    copy the provider's text into an `Error`;
  - a 200 whose body reports an error, a body of the wrong shape, a missing field → `MalformedResponse`
    or `Unavailable`, never an exception;
  - `catch` what the **pipeline** throws, not what a bare `HttpClient` throws: `HttpRequestException`,
    Polly's `TimeoutRejectedException` (a timeout here is not a `TaskCanceledException`),
    `BrokenCircuitException` and any other `ExecutionRejectedException` → `Unavailable`;
    `JsonException` → `MalformedResponse`; `OperationCanceledException when
    (!cancellationToken.IsCancellationRequested)` → `Unavailable`. The caller's own cancellation
    propagates.
  - Log at Information or Warning with the provider and the status, never a credential or a
    personal value.

## 4. Registration: `AddOutboundIntegrations`, never `AddInfrastructure`

- [ ] A private `Add{Provider}(...)` called from `AddOutboundIntegrations(configuration)`, which only
      hosts that call out invoke (the API). The standard resilience handler validates its options at
      boot, so registering a client makes its section a boot requirement of the host: the migration
      job must never see it [`ExchangeRateResilienceTests.Boot_MigrationShapedHost_…` is the model].
- [ ] `AddOptions<{Provider}Options>().Bind(...).ValidateDataAnnotations().ValidateOnStart()`.
- [ ] `AddHttpClient<I{Capability}Provider, {Provider}Client>((sp, client) => …)`: the base address
      from the validated options (trailing slash added), and `client.Timeout = Timeout.InfiniteTimeSpan`
      (the pipeline owns every timeout; the 100 s default would cap the whole pipeline, retries included).
- [ ] **Replace, don't add**: `client.RemoveAllResilienceHandlers()` (inside
      `#pragma warning disable EXTEXP0001`, with the reason) **before** `AddStandardResilienceHandler()`.
      Added alone, the client's handler nests inside ServiceDefaults' blanket one, whose 10 s attempt
      timeout still wins and whose retries multiply the client's.
- [ ] `.Configure((options, sp) => …)` reading the validated options when the pipeline is built: the
      attempt timeout, `Retry.MaxRetryAttempts`, `Retry.Delay` and a capped `Retry.MaxDelay`, a total
      timeout of `attempt × (retries + 1) + maxDelay × retries` (smaller cuts off a retry the policy
      granted), and `CircuitBreaker.SamplingDuration` at least twice the attempt timeout.
- [ ] A health check that probes cheaply (a TCP connect, not an API call), caches its verdict
      (30 s), is registered as a **singleton**, tagged `external`, and reports `Degraded` when the
      provider is unreachable and `Unhealthy` only when our own configuration is wrong.

## 5. Tests (ApiTests; copy the ExchangeRate suites)

- [ ] `{Provider}ProviderTests` over a scripted `HttpMessageHandler`: the happy path, an error status,
      unreadable JSON, a wrong shape, a 200 that reports an error, a transport failure, a provider
      slower than the attempt timeout, an open circuit (nothing sent), a transient status retried to
      success, the caller's cancellation propagating, the health check's cache and Degraded verdict.
- [ ] `{Provider}ResilienceTests` under `AddServiceDefaults()`: exactly one `ResilienceHandler` in the
      chain; a hanging provider is `Unavailable` within the client's own budget; a retry is not cut off
      by the total timeout; an API-shaped host refuses a malformed section at boot and a
      migration-shaped host ignores it.
- [ ] A row per bounded key in `OptionsConventionTests`' boot theory.
- [ ] Optional: a live smoke test behind its own opt-in variable (`RUN_LIVE_SMOKE_TESTS=1` is the
      model), never part of the default run.

## 6. Docs

- [ ] `docs/features/{provider}.md` (kebab-case), shaped like `exchange-rates.md`: port, adapter,
      behaviour, configuration, tests; a line in `docs/INDEX.md` [`check-docs-drift.sh` requires the
      doc whenever `ExternalApis/{Provider}/` changes].
- [ ] A row in `docs/deployment.md`'s required-configuration table (the section, which values are
      secrets, what an invalid value does at boot).
- [ ] `docs/threat-model.md` when the integration sends personal or payment data to the third party,
      or accepts callbacks (webhooks need their own authentication: a signature check, not the JWT).
- [ ] A load-bearing deviation from this checklist (a provider SDK instead of a typed client, a
      queue instead of a synchronous call) gets an ADR (the `new-adr` skill).
- [ ] `dotnet build Trellis.slnx -c Release && dotnet test Trellis.slnx`.
