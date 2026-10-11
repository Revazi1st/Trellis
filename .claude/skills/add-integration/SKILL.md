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
- [ ] Called from an outbox event handler, a `Result` cannot travel back: the handler throws on
      `Unavailable` or `Failure` so the outbox retries it, and logs or records a domain refusal and
      returns (`.claude/rules/application.md`, *Event handlers report failure by throwing*).

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
  - **A call that is not idempotent** (a POST that charges, books or sends) must not be repeated
    blindly: send the idempotency key the provider honours, built from the business identity
    (and, from an event handler, the handler's `[DomainEventHandlerName]`), or configure the
    client's handler with `Retry.DisableForUnsafeHttpMethods()`, as ServiceDefaults' blanket
    handler does. The standard handler you add retries every method by default.

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

## 7. Receiving a webhook (when the provider calls you)

A callback ("the payment succeeded") is an inbound adapter: an endpoint the provider calls, which
authenticates the provider rather than a user and must cope with the same event arriving again.

- [ ] `.AllowAnonymous()` with its row in `EndpointMetadataTests.AnonymousAllowlist` and a row in
      `docs/threat-model.md`: the JWT does not apply. **Verify the signature first**, over the raw
      body, with the provider's scheme and a secret from configuration (validated options, user
      secrets in development), compared with `CryptographicOperations.FixedTimeEquals`, and its
      timestamp checked against a tolerance so an old delivery cannot be replayed; a missing or wrong
      signature is a 401 and nothing else runs.
- [ ] **Deduplicate on the provider's event id**, never on anything that changes per delivery. The
      command's handler hashes the provider name and the event id (SHA-256 hex fits the
      128-character `IdempotencyKeys.Key`), asks `IIdempotencyStore.FindAsync`, where any answer but
      `IdempotencyLookup.None` means already processed, and otherwise calls `Reserve(key, fingerprint)`.
      The reservation commits with the aggregate change and its outbox rows, or not at all; two
      concurrent deliveries resolve at the primary key, and the loser's commit returns
      `Idempotency.Duplicate`.
- [ ] **Answer 2xx to a duplicate**: `Idempotency.Duplicate`, an already-processed key, or the
      aggregate's "already in that state" `Conflict`. A 409 makes the provider keep retrying; keep
      5xx for what a retry can fix.
- [ ] Change one aggregate in the command; send anything non-transactional (a receipt, a call back
      out) from an event handler, after the commit.
- [ ] **Keep the key longer than the provider retries.** These rows share `IdempotencyKeys` with the
      HTTP keys and are swept after `Retention:IdempotencyKeyAge` (2 days by default): raise it above
      the provider's redelivery window, which can be three days.
- [ ] Tests: a signed delivery applies once; the same delivery again, and two at once, answer 2xx
      and change nothing more; a bad or stale signature is a 401.
