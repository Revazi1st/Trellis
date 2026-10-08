# Deployment

Production deployment of a generated solution: container images, the migration job, the
required configuration, probes, telemetry.

## Container images — no Dockerfiles, by design

Every host publishes its own image with the SDK's built-in containerization; there are no
Dockerfiles to drift out of sync with the projects:

```bash
dotnet publish src/Presentation/Api/Trellis.Api /t:PublishContainer
dotnet publish tools/Trellis.MigrationService /t:PublishContainer
dotnet publish src/Presentation/UI/Trellis.UI.Web /t:PublishContainer   # if the UI is included
```

Base image, exposed port and entry point derive from the project; registry and tag are MSBuild
properties (`-p:ContainerRegistry=… -p:ContainerRepository=… -p:ContainerImageTag=…`).

For Docker Compose targets, the AppHost declares `AddDockerComposeEnvironment("compose")` —
`aspire publish` (local SDK required, ADR-0008) emits compose artifacts from the same
orchestration model that `aspire run` uses, building the images via `PublishContainer` along
the way. The generated compose file already encodes the ordering below.

What the compose output publishes: the AppHost marks the public surfaces external
(`WithExternalHttpEndpoints()`), so `api` and, when the UI is included, `web` get a `ports:`
entry. `postgres` and `migrations` stay on the internal `aspire` network. The publisher also
adds the Aspire dashboard (`compose-dashboard`) with its UI port published; decide whether it
belongs in your production environment before you deploy.

## Migrations: a one-shot pre-deploy job

`Trellis.MigrationService` applies EF migrations, seeds roles/permissions and the admin user,
then exits. **Exit code 0 gates the API** — the AppHost expresses this as
`WaitForCompletion(migrations)`; in Kubernetes make it a Job (or init container) the API rollout
depends on. Nonzero exit means migration or seeding failed — do not start the new API version.

Seed guards outside Development:

- The worker **refuses to seed the well-known default admin password** — set
  `Seed__AdminPassword` (and consider `Seed__AdminEmail`) or the job exits 1 on a fresh database.
- The `Seed` section is validated at boot (`AdminEmail` an e-mail, `AdminPassword` ≥ 8 characters,
  with multi-tenancy `TenantId` ≤ 64): a bad value exits 1 naming the key. A password Identity
  rejects (it also wants a digit, an upper-case letter and a symbol) exits 1 with every Identity
  error in the log — never "seeding completed" without an admin.
- Demo invoices are seeded in Development only.

The job is idempotent: an already-migrated database and an existing admin user are both no-ops
(an existing admin's password is never changed; one left without its role by an interrupted run is
given it back), so run it on every deploy.

## Required configuration

Env-var form shown (`:` → `__`). The API validates hard requirements at startup — a bad deploy
fails the rollout, not the first request.

| Setting | Required | Effect |
|---|---|---|
| `ConnectionStrings__trellis-db` | yes (API + migration job) | PostgreSQL connection string. The name contains a dash — settable in compose/k8s env maps, but not via POSIX `export`; use a `.env` file or manifest. |
| `Authentication__SigningKey` | yes | JWT HMAC-SHA256 key, **≥ 32 characters or the API refuses to boot**. Store in a secret store; rotate by redeploying (outstanding access tokens die within `AccessTokenMinutes`). |
| `Seed__AdminPassword` | yes (migration job, fresh DB) | Bootstrap admin credential; seeding refuses the default outside Development. |
| `Seed__TenantId` | no (multi-tenancy only) | Tenant of the bootstrap admin and the Development demo data; default `default`. |
| `Registration__Mode` | no | `Closed` (default): `POST /auth/register` answers 403 and accounts come from an administrator or the seeder. `Open`: anyone can create a Member account, which can read business data — set it only when that is intended. Any other value stops the API at boot. |
| `Email__Provider` | yes, in effect | `Logging` (shipped): e-mail is **not sent** — each message becomes one log line (masked recipient, subject, length) and the API logs a startup warning outside Development. `None`: discard on purpose. Any other value stops the API at boot. A real provider is a new adapter (`Infrastructure/Email`). |
| `ExternalApis__ExchangeRates__BaseUrl` / `__TimeoutSeconds` / `__MaxRetries` | no | The reference integration: Frankfurter v2 (`https://api.frankfurter.dev/v2/`), 10 s per attempt, 2 retries. Validated at API boot only — the migration job does not register outbound integrations. |
| `ForwardedHeaders__KnownProxies__0…` / `ForwardedHeaders__KnownNetworks__0…` | behind ingress | IPs / CIDR ranges of trusted proxies. Without them `X-Forwarded-For` is ignored (loopback-only default — spoof-safe) and every caller shares the ingress IP's rate-limit bucket. `aspire publish` output sets `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`; that switch cannot widen trust — with no list configured the API keeps the loopback-only default and logs a startup warning naming these settings (ADR-0021). A Blazor Server web host signs everyone in from one address: 10 logins/min per web host, by design. |
| `Kestrel__Limits__MaxRequestBodySize` / `Kestrel__Limits__MaxRequestHeadersTotalSize` | no | Request caps in bytes: 1 MB body (larger → 413) and 32 KB of headers by default. Raise per endpoint for a real upload, not globally. |
| `Cors__AllowedOrigins__0…` | if browsers call cross-origin | Allowed origins; **none by default** (same-origin only). |
| `Retention__*` | no | Janitor windows for the bookkeeping tables: `SweepInterval` (1h; 1 s – 1 day), `InitialDelay` before the first sweep (1m; 0 – 1h), `ProcessedOutboxAge` (3d; ≥ 1h), `DeadLetteredOutboxAge` (30d; ≥ 1 day), `IdempotencyKeyAge` (2d; ≥ 1h), `DeadRefreshTokenAge` (30d; ≥ 1 day — keep it ≥ `Authentication__RefreshTokenDays`, a consumed token is what makes a replay detectable). Every window is capped at 365 days. TimeSpan format, e.g. `7.00:00:00`. A value out of range stops the API at boot, naming the key. |
| `Outbox__*` | no | The outbox processor (ADR-0023): `PollInterval` (5 s; 100 ms – 10 min) between polls once a batch came back short, `BatchSize` (20; 1 – 1000) rows per claim, `MaxAttempts` (8; 1 – 50) failures before a message is dead-lettered, `BaseRetryDelay` (30 s; 1 s – 1 h) doubling per failure up to `MaxRetryDelay` (15 min; 1 s – 1 day, and ≥ `BaseRetryDelay`), `DispatchTimeout` (30 s; 1 s – 10 min) per message's handlers. Defaults retry after 30 s, 1 m, 2 m, 4 m, 8 m, 15 m, 15 m and dead-letter about 45 minutes after the first failure. A claimed row is leased for `DispatchTimeout × (BatchSize + 1)` (10.5 min): after a **crash** its in-flight rows reappear within that long (a graceful stop releases them at once) — shorten `BatchSize` or `DispatchTimeout` to shorten it. TimeSpan format. A value out of range stops the API at boot, naming the key. |

## Health probes

- **`/health`** — readiness: all checks, including the database (`AddDbContextCheck`).
- **`/alive`** — liveness: only checks tagged `"live"` (the self check) — stays green during a
  DB outage, so the orchestrator doesn't restart-loop a healthy process.

Both are **anonymous by design**: orchestrator probes (the AppHost's `WithHttpHealthCheck`,
compose/k8s probes) must reach them without credentials, and both are **exempt from rate limiting** —
a probe answered 429 would read as an unhealthy instance. **Keep them off the public ingress** —
route probes over the internal network only. The response is just the status text, but an
open readiness endpoint is still a free DB-status oracle.

## Observability

Set `OTEL_EXPORTER_OTLP_ENDPOINT` and every host exports logs, metrics and traces via OTLP
(the exporter is registered only when the variable is present).

- **What is exported**: every meter and trace source named `Trellis.*` — ServiceDefaults subscribes
  by prefix, so a new meter needs no host registration (`TelemetryNamingTests` keeps every name on
  the prefix) — plus ASP.NET Core, HttpClient and runtime instrumentation, the `Polly` meter
  (retries, timeouts and open circuits of every HttpClient pipeline) and the
  `Microsoft.EntityFrameworkCore` meter (SaveChanges, optimistic-concurrency failures,
  execution-strategy retries).
- **Who emits it**: `service.name` is `OTEL_SERVICE_NAME` (Aspire sets it), else the host's
  application name; `service.version` is the host's informational version;
  `deployment.environment.name` is the ASP.NET Core environment. A key you set in
  `OTEL_RESOURCE_ATTRIBUTES` wins over the computed one.
- **Correlation**: every API response carries `X-Correlation-ID` — the caller's own value when it
  is short and safe, else the request's W3C trace id, the same trace a problem body's `traceId`
  names. The request span carries it as `trellis.correlation_id`.
<!--#if (UseSerilog) -->
- **Serilog** (the `UseSerilog` toggle): Serilog writes the console and hands every event on to the
  OpenTelemetry log provider, so OTLP log export is unchanged. Its levels come from the `Serilog`
  section (`MinimumLevel:Default` and `MinimumLevel:Override:<category>`), which mirrors
  `Logging:LogLevel` but is not read from it: change both, e.g.
  `Serilog__MinimumLevel__Override__Microsoft.AspNetCore=Information`.
<!--#endif -->

Worth alerting on:

- `trellis.outbox.dead_lettered` — a domain event exhausted its retries (`trellis.outbox.reason` =
  `exhausted`), its type is unknown to this deploy (`unknown_type`) or its payload does not parse
  (`poison`); dead-lettered rows are the alert queue and are kept 30 days by default. Re-drive them with
  the runbook below.
- `trellis.outbox.oldest_pending_age` above `2 × Outbox__PollInterval + Outbox__DispatchTimeout` for
  five minutes, or `trellis.outbox.pending` growing steadily — the drain is stalled or cannot keep up
  (a handler's dependency is slow, the database claim fails, no API replica runs the processor). Both
  gauges are sampled by the processor on each poll and are the same global number from every replica:
  take `max`, not `sum`.
- `trellis.outbox.retries` rate (`error.type` names the exception) — a handler's dependency is
  struggling; the per-attempt line is logged at Warning. `trellis.outbox.dispatch.duration` (by
  `trellis.outbox.outcome`) shows whether handlers approach `Outbox__DispatchTimeout`.
- `resilience.polly.strategy.events` with `event.name=OnCircuitOpened` — an outbound dependency's
  circuit opened; calls fail fast until it closes.
- `microsoft.entityframeworkcore.optimistic_concurrency_failures` — a rising rate means clients
  keep writing stale versions (each one is a 409).
- Outbox dispatch spans rejoin the originating request's trace (`traceparent` is persisted with
  the message), so a failed handler is visible under the request that caused it.

Health-probe requests are filtered out of tracing — probes don't pollute the trace stream.

The invoice read cache keeps an entry 30 seconds in each API instance's memory (L1) and, with the
`UseRedis` toggle, five minutes in Redis (L2). A write evicts the entry from its own instance and
from Redis only, so with several replicas another replica can serve the previous state, ETag
included, for up to 30 seconds; without Redis every replica caches on its own for the same 30 seconds.

### Outbox operations

The outbox's console is its table; there is no admin endpoint (ADR-0023). `Status` is 0 pending,
1 processed, 2 dead-lettered; `Error` holds the last failure; `CompletedHandlers` the handlers already
credited for the row.

```sql
-- Inspect the alert queue.
SELECT "Id", "Type", "Attempts", "Error", "OccurredAtUtc"
FROM "OutboxMessages" WHERE "Status" = 2 ORDER BY "OccurredAtUtc";

-- Re-drive one dead letter with a fresh retry budget. CompletedHandlers is kept on purpose:
-- handlers that already succeeded (the receipt e-mail, say) must not run again.
UPDATE "OutboxMessages" SET "Status" = 0, "Attempts" = 0, "Error" = NULL, "NextAttemptAtUtc" = now()
WHERE "Status" = 2 AND "Id" = '<id>';

-- Re-drive every dead letter of one event type after fixing its handler.
UPDATE "OutboxMessages" SET "Status" = 0, "Attempts" = 0, "Error" = NULL, "NextAttemptAtUtc" = now()
WHERE "Status" = 2 AND "Type" = 'invoices.paid.v1';

-- Give up on one for good (the janitor would otherwise keep it for Retention__DeadLetteredOutboxAge).
DELETE FROM "OutboxMessages" WHERE "Status" = 2 AND "Id" = '<id>';
```

- A pending row whose `NextAttemptAtUtc` lies minutes ahead is either in back-off (`Attempts` > 0, see
  `Error`) or leased by a processor that is working on it or died holding it. Leases are never released
  by hand: they expire by themselves within `Outbox__DispatchTimeout × (Outbox__BatchSize + 1)`.
- Re-driving a `poison` row without a code fix dead-letters it again on the next poll.
- `unknown_type` rows mean an event type was removed or renamed while rows were in flight: redeploy a
  type carrying that `[DomainEventName]` and re-drive them.

### Idempotency keys

| Idempotency | What to know |
|---|---|
| Table | `IdempotencyKeys` (ADR-0018): one row per `(user, method, route, Idempotency-Key)` hash. A row with a `StatusCode` is a completed request whose status, content type, `Body` and `Location` are replayed to a retry; a row without one is in flight. Rows are inserted by the command's own commit, so a failed command never leaves one. |
| Replay cap | Response bodies are stored up to 64 KB (per endpoint in code, `RequireIdempotency(maxReplayBodyBytes:)`; not a configuration setting). A larger response completes normally; its retry is a 409 `Idempotency.Duplicate` telling the client to read the resource. |
| Stuck in flight | A crash between a command's commit and the completion write leaves its row in flight: retries answer 409 `Idempotency.InFlight` with `Retry-After` until `Retention__IdempotencyKeyAge` (2 days) sweeps it. The command *did* apply; the client reads the resource. To free the key sooner: `DELETE FROM "IdempotencyKeys" WHERE "StatusCode" IS NULL AND "CreatedAtUtc" < now() - interval '1 hour';` — only after confirming no request that old can still be running (`Outbox__DispatchTimeout` bounds nothing here; the API's 30 s request timeout does). |
| Size | Bounded by `Retention__IdempotencyKeyAge` × write rate × (≤ 64 KB + a few hundred bytes) per keyed request; the janitor sweeps by creation time whatever the state. |
