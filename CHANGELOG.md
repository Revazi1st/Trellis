# Changelog

All notable changes to the Trellis template are documented here, written for the people who
generate projects from it. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). A generated project gets its own empty
changelog; its `.trellis-template.json` records which version of this one it came from.

## [Unreleased]

## [0.1.0] - 2026-10-08

The first release. *Added* describes what a project generated from 0.1.0 contains. *Changed*,
*Security* and *Fixed* matter if you generated a project from a pre-release snapshot of `main`; adopt
this version with `docs/upgrading.md` and read these notes first:

- **Drain the outbox before deploying.** Completion credits are now keyed on each handler's
  `[DomainEventHandlerName]` (stable names such as `invoices.paid.send-receipt`), no longer on the CLR
  type name. Rows still pending when you switch would have their handlers run once more (a second
  receipt e-mail). Let `OutboxMessages` drain to no pending rows, then deploy.
- **Idempotency v2 changes a table.** `IdempotencyKeys` gains `RequestFingerprint`, `StatusCode`,
  `ContentType`, `Body`, `Location` and `CompletedAtUtc` and a named primary key, and the shipped
  `InitialCreate` was regenerated. A deployed database needs a new migration on top
  (`docs/upgrading.md`, "Schema-affecting changes"), never the regenerated one. The stored key now
  hashes the HTTP method too, so a key reserved before the upgrade never matches a request after it:
  let in-flight keyed requests finish before switching, and the old rows age out with
  `Retention:IdempotencyKeyAge` (or delete them).
- **The wire contract changed** for API clients: problem documents, validation errors, `InvoiceDto`,
  the paging parameters and the OpenAPI document (see *Changed*). Regenerate client code from the new
  `docs/api/openapi.v1.json`.
- **Dashboards need the renamed telemetry** (see *Changed*).
- **`--use-multitenancy` adds `AspNetUsers.TenantId`** (the tenant now lives on the user): a project
  generated with the toggle from a pre-release snapshot needs that column in a migration too, and its
  existing users a tenant.

### Added

**The solution**

- A rich-DDD, Clean Architecture .NET 10 solution grouped by ring: SharedKernel, Domain and
  Application (`src/Core`), Infrastructure and Persistence as peers that never reference each other
  (`src/Infrastructure`), the API with its plumbing and public contracts and the Blazor UI
  (`src/Presentation`), Aspire service defaults (`src/Shared`), the Aspire AppHost and a migration job
  (`tools`). Architecture tests enforce the dependency flow.
- Hand-rolled CQRS without a mediator: endpoints inject handlers, a decorator pipeline runs
  Logging → Tracing → Validation → UnitOfWork → handler (queries skip the unit of work). `Result` and
  `Result<T>` with `Bind`/`Map`/`Tap`/`Ensure` and `Result.Combine`; `IUnitOfWork.CommitAsync` returns
  a `Result`, so a stale concurrency token is a 409 `Concurrency.Conflict`, never an exception.
- Domain building blocks: aggregates with factories and private EF constructors, sealed-record value
  objects (`Money`, `Currency`, `EmailAddress`) mapped as EF complex types, `IEquatable` strongly-typed
  IDs that serialize as bare GUIDs (dictionary keys included), versioned domain events
  (`[DomainEventName]`), hand-rolled specifications whose orderings end with the id.
- **Invoices**, the reference slice: create, add lines, issue (a due date that is not in the past),
  pay, void, get with a weak ETag and conditional GET, list overdue (paged); a receipt e-mail and
  business metrics from outbox-dispatched handlers; a read cache.
- **Auth**: ASP.NET Core Identity in Persistence, HS256 JWTs carrying permission claims, a permission
  catalogue (`Permissions`, `Roles.Permissions`) seeded by the migration job, single-use refresh
  tokens with replay detection (a replayed token revokes the user's whole session family), logout and
  revoke-all, account lockout (5 failures, 15 minutes, a visible 403 `Auth.AccountLockedOut`),
  `Registration:Mode` (`Closed` by default, `Open` in Development), validated auth input.
- **Exchange rates**, the reference outbound integration: a typed client behind a port returning
  `Result`, its own resilience pipeline replacing ServiceDefaults' blanket one, options validated at
  boot, a cached TCP health check reporting Degraded, Frankfurter v2 as the default provider, and an
  opt-in live smoke test (`RUN_LIVE_SMOKE_TESTS=1`).
- **Transactional outbox**: domain events are written to `OutboxMessages` in the command's
  transaction and dispatched after commit by `OutboxProcessor` (ADR-0018, ADR-0023): a
  `FOR UPDATE SKIP LOCKED` claim leases a batch, each message runs in its own scope with a dispatch
  timeout, each handler is credited under its `[DomainEventHandlerName]` with its own commit, failures
  back off 30 s → 15 min and dead-letter on the eighth (unknown types and unreadable payloads at once),
  `traceparent` and the tenant travel with the row. Validated `Outbox` options, backlog gauges, and an
  SQL re-drive runbook in `docs/deployment.md`.
- **Idempotent writes**: `Idempotency-Key` on every write endpoint; the reservation commits with the
  command, a retry with the same body is answered with the stored response (`Idempotency-Replayed:
  true`), a different body is a 422 `Idempotency.FingerprintMismatch`, a retry while the first is
  running a 409 `Idempotency.InFlight`; bodies are stored up to 64 KB per endpoint.
- **Problem details** (ADR-0024): every 4xx and 5xx is RFC 9457 with a stable `code`,
  `type` = `urn:problem-type:{code}`, a human `title`, `detail`, `instance`, `traceId` and
  `correlationId` (= `X-Correlation-ID`); a validation 400 lists `errors[]` of `{field, code,
  description}`. Unknown routes, 405, 415, malformed JSON, unparsable ids and oversized bodies are
  problem documents too, never a bodyless status or a 500.
- **OpenAPI** (ADR-0025): one document per API version with concrete paths, an `operationId` per
  endpoint class, problem responses by shape referencing a generated `ProblemResponse` schema,
  `Location`, conditional-GET members, typed status, money and version, the bearer scheme (Scalar can
  authorize). The document is committed at `docs/api/openapi.v1.json`, compared with the served one on
  every test run (`UPDATE_OPENAPI_SNAPSHOT=1` regenerates it) and diffed against the running app in CI.
- **Secure-by-default API** (ADR-0021): the versioned group requires authorization and a test fails
  any endpoint that is neither on the anonymous allowlist nor carrying a catalogued permission;
  forwarded-header trust never widens beyond loopback unless `ForwardedHeaders:*` names the proxy;
  rate limits by credential type; security headers and `no-store` on `/auth/`; 1 MB request bodies,
  32 KB headers.
- **Caching** (ADR-0026): HybridCache with an in-memory L1 (30 s) and, with `--use-redis`, a Redis L2
  (5 min); keys from the scoped `CacheKeys` service, tenant-scoped under multi-tenancy.
- **Data retention** (ADR-0027): a janitor sweeps processed and dead-lettered outbox rows, idempotency
  keys and dead refresh tokens on validated `Retention` windows.
- **Options validated at boot** everywhere (`Authentication`, `Registration`, `Email`, `ExternalApis`,
  `Outbox`, `Retention`, `Seed`): a bad value stops the host naming the key.
- **Telemetry** (ADR-0028): OpenTelemetry logs, metrics and traces over OTLP; every `Trellis.*` meter
  and source (renamed with the project) plus the Polly and EF Core meters; `service.version` and
  `deployment.environment.name` on the resource; one outcome line and one span per use case;
  `X-Correlation-ID` on every response.
- **Blazor UI** (`--include-ui`): a render-mode-agnostic component library and a Blazor Server host;
  the invoice lifecycle and registration; sessions that rotate their access token and survive
  transient refresh failures; every API call through one helper that turns failures into results;
  server validation errors under their inputs; success notifications; sign-out that revokes the
  refresh token (and a closing circuit too); a `returnUrl` that only ever goes back into the app;
  a production `/Error` page; a CSP; an accessibility pass.
- **Toggles**: `--include-ui`, `--use-redis`, `--use-serilog`, `--use-multitenancy` (the tenant comes
  from the signed `tenant` claim, every aggregate root is filtered and stamped by convention),
  `--include-ui-tests`, `--skip-restore`.

**Tooling, tests and documentation**

- Build and test without a local SDK through the SDK container (`scripts/dotnet.sh`, a digest-pinned
  base image) or the dev container; `aspire run` with a local SDK; `aspire publish` to Docker Compose
  (what it writes and what to add before production is in `docs/deployment.md`). A one-shot migration
  job that refuses the default admin password outside Development and fails loudly on a rejected seed.
- Tests: domain and application unit tests, architecture and convention tests (layers, naming,
  options, telemetry names, catch filters, query filters), Testcontainers integration tests, in-process
  API tests over the real HTTP pipeline, UI client tests, the opt-in Aspire E2E and Playwright tests.
- Generated CI: a digest-pinned gitleaks scan, format check, Release build with warnings as errors,
  tests with coverage filtered to first-party code and a gate on the Core ring, the OpenAPI contract
  diff, the SDK-container build, CodeQL (public repositories, or private ones that set
  `CODEQL_ENABLED`), grouped Dependabot updates, the docs-drift check, and with `--include-ui-tests` a
  Playwright job.
- Central package management with transitive pinning, NuGetAudit on the whole graph, and license
  ceilings enforced over the resolved package graph (MediatR, AutoMapper, FluentAssertions,
  MassTransit, Moq) (ADR-0011, ADR-0029).
- Documentation for people and agents alike: `AGENTS.md`, path-scoped `.claude/rules`, the skills
  `add-feature`, `remove-feature`, `add-integration`, `add-migration`, `new-adr` and `run-app`, two
  review agents, a docs-drift edit hook, architecture, development, deployment, upgrading and threat
  model guides, feature docs, and 29 ADRs. Project ADRs are numbered from 0100 so template upgrades
  never collide with them.
- A generated project gets its own README, an empty CHANGELOG and `.trellis-template.json` (template
  version, name and options), and none of the template's own governance files. `LICENSE` keeps the
  template's MIT notice; the README asks you to add terms for your product.

### Changed

- **Decorator pipeline**: Logging → Tracing → Validation → UnitOfWork → handler (validation used to
  run first, outside the log and span). One outcome line per use case: Information for success and
  classified expected failures, Warning for an unclassified failure, Error only for an exception.
- **Problem documents**: `type` is `urn:problem-type:{code}`, `title` is a human sentence (never
  parse it), `code`, `instance` and `correlationId` are on every problem, and each `errors[]` entry is
  `{field, code, description}` with `field` the camelCase JSON path (or `null`) and `code` the rule
  (`Validation.NotEmpty`), no longer the property name.
- **Idempotency**: the reservation is written by the command's own commit instead of before the
  handler; a retry of a completed request is answered with the stored response instead of a bare 409;
  the key's scope includes the HTTP method.
- **Contracts**: `InvoiceDto.Status` is the string enum `InvoiceStatus`, `Version` is a `long`; the
  overdue list binds `page` (1–10000) and `pageSize` (1–100) by those camelCase names and
  `PageRequest` is gone; every OpenAPI `operationId` is the endpoint's class name (`IEndpoint.Map`
  returns the `RouteHandlerBuilder`, and endpoints no longer call `.WithName`).
- **Conditional GET** parses `If-None-Match`: the strong form, a list and `*` answer 304 too.
- **Outbox**: dead letter after about 45 minutes (eight failures) instead of 7.5; a retry logs Warning
  (was Error). Metrics gain units, `trellis.outbox.event_type` replaces `event.type`, and
  `trellis.outbox.retries`, `.dispatch.duration`, `.pending` and `.oldest_pending_age` are new.
- **Telemetry names**: the invoice counters' attribute is `trellis.invoice.currency`, the request
  span's is `trellis.correlation_id`; hosts no longer list meters and sources (ServiceDefaults
  subscribes `Trellis.*`).
- **Multi-tenancy**: the tenant is read only from the signed `tenant` claim, minted from
  `AspNetUsers.TenantId`; the `X-Tenant-Id` header is ignored and a token without the claim is a 401.
  Self-registration provisions a fresh tenant; `Seed:TenantId` names the seeded admin's.
- **Registration** is closed unless `Registration:Mode` is `Open` (Development sets it).
- **E-mail**: `Email:Provider` is `Logging` (masked recipient, subject and length only, with a boot
  warning outside Development) or `None`.
- **Exchange rates** default to Frankfurter v2 (`https://api.frankfurter.dev/v2/`).
- **Domain**: `Invoice.Issue(dueDate, now)` rejects a missing or past due date
  (`Invoice.DueDateInPast`); `Void` raises `InvoiceVoidedDomainEvent`; `Money` rejects sub-cent
  amounts and can no longer go negative through subtraction.
- **Blazor sessions** end only on a 401 from the refresh endpoint; a 429, a 5xx or an unreachable API
  keeps the session and retries after 5 seconds.
- **HybridCache lifetimes** are stated: 30 s in memory, 5 min in Redis.
- The seeded admin is `admin@example.com` (password unchanged), and every generated project gets its
  own Development JWT signing key.

### Security

- Endpoints require authorization by default, `RequirePermission` rejects an unknown permission at
  startup, and an anonymous endpoint must be on a tested allowlist.
- Forwarded headers are trusted only from loopback or the configured proxies, even when
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` (a startup warning names the settings).
- HS256 is the only accepted signing algorithm; 401s carry `WWW-Authenticate`; the API refuses to
  start without a signing key of at least 32 characters.
- Login spends the same hashing work for an unknown e-mail as for a wrong password, logs security
  events with masked e-mails, and lockout throttles per account.
- Security headers on every API response, a CSP and frame protection on the web host, `no-store` on
  auth responses, request size limits.
- Problem documents never carry exception text; a production 500 is opaque.
- The tenant can no longer be chosen by a request header (`--use-multitenancy`), and the read cache is
  tenant-scoped.
- A blank `Idempotency-Key` is a 400; keys are scoped per user, method and route and hashed.
- Signing out of the web UI revokes the refresh token at the API, and the sign-in page's `returnUrl`
  cannot redirect outside the app.
- The migration job refuses to seed the default admin password outside Development, and demo data
  seeds in Development only.

### Fixed

- An idempotent retry after a client disconnect could apply the command twice (a cancelled cache
  eviction released the key after the commit).
- `--use-serilog` projects exported no application logs to OpenTelemetry.
- `GET /invoices/{id}` answered 404 for the caller's own invoice under `--use-multitenancy` (the
  cache factory ran without the request's tenant).
- `aspire run` pointed every resource at the Compose database instead of the provisioned one.
- `--use-multitenancy` projects could not start (no `ITenantContext` outside the API host).
- Blazor sessions died after 15 minutes (the refresh token was never used), and the UI's POSTs could
  be duplicated by retries (no idempotency key was sent).
- Rate limiting never partitioned per user and collapsed behind a proxy.
- Refresh-token rotation was not atomic; registration could leave an account without its role.
- Outbound calls to the reference integration escaped the `Result` boundary on a timeout or an open
  circuit, and the client's retries multiplied with ServiceDefaults' blanket handler.
- A failed bookkeeping save in the outbox processor stranded the leases of the rest of its batch.
- Paging could repeat or skip rows with equal sort keys; non-UTC `DateTimeOffset` values were
  rejected by PostgreSQL.
- A bad `Seed` option crashed the migration job (exit 134) instead of failing it with the key named.
- The generated UI was unstyled in every project not named after the template, and the Blazor
  invoice list showed only its first page.
- Projects named in kebab case produced a solution that did not load; such names now get a warning
  and a consistent tree.

[Unreleased]: https://github.com/Revazi1st/Trellis/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Revazi1st/Trellis/releases/tag/v0.1.0
