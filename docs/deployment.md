# Deployment

Production deployment of a generated solution: container images, what `aspire publish` produces,
edge TLS, the migration job, the required configuration, probes, telemetry, and the outbox and
idempotency runbooks.

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

CI's `container-smoke` job (`.github/workflows/ci.yml`) publishes these images on every run, runs the
migration image against PostgreSQL, then boots the API image (and the web image, with the UI) in
Production with only the settings [Required configuration](#required-configuration) marks as
required. It checks readiness, a problem document from an anonymous call, and a sign-in as the
seeded admin, so a setting Production needs and this page does not name fails CI, not a deploy.

### Docker Compose: what `aspire publish` produces

The AppHost declares `AddDockerComposeEnvironment("compose")`, so `aspire publish` (a local SDK and
the Aspire CLI, ADR-0008) writes a `docker-compose.yaml` and an `.env` from the same orchestration
model `aspire run` uses. What the publish step of Aspire 13.6 writes, and what it leaves to you:

| Service | As published | Before production |
|---|---|---|
| `postgres` | `postgres:18`, a named data volume, port 5432 on the internal `aspire` network only (`expose:`), password from `POSTGRES_PASSWORD` in `.env` | Set a strong `POSTGRES_PASSWORD`; plan backups of the volume. Keep the major version pinned: a data volume does not survive a major upgrade |
| `migrations` | `${MIGRATIONS_IMAGE}`, no ports, starts once PostgreSQL has *started* (not once it is ready; the job's retrying execution strategy rides out a normal start-up) | Add `Seed__AdminPassword` (the job refuses the default outside Development). No restart policy: it is a one-shot job, and rerunning `docker compose up` reruns it safely |
| `api` | `${API_IMAGE}`, published as `"${API_PORT}"` (the container port on an **ephemeral host port**), starts only after `migrations` exited 0, `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` | Add `Authentication__SigningKey` (the API refuses to boot without it), `Email__Provider`, `Registration__Mode` if wanted, the `ForwardedHeaders__*` lists (below) and `restart: unless-stopped`; publish a fixed host port or none (behind the proxy) |
| `web` (with the UI) | `${WEB_IMAGE}`, `"${WEB_PORT}"` on an ephemeral host port, service discovery pointed at `http://api:${API_PORT}`, `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` | `restart: unless-stopped`; reach it only through the proxy (below) |
| `compose-dashboard` | The Aspire dashboard (`mcr.microsoft.com/dotnet/nightly/aspire-dashboard`), UI port 18888 published, OTLP on 18889/18890 internal, `restart: always`; every .NET service exports its logs, metrics and traces to it | **Decide** (below) |

`.env` holds the parameters: the three image names, the two container ports and the PostgreSQL
password, empty until you fill them (or a deploy step does). Build the images with
`PublishContainer` (above) and put their names in `.env`. Nothing in the file is TLS, a health check
or a resource limit; nothing sets `ASPNETCORE_ENVIRONMENT`, so every host runs as Production.

**The dashboard.** It is a development tool that also works in production, but it keeps telemetry in
memory only, it shows every log line and trace (masked, but still operational detail), and its UI is
published on a host port. Pick one before the first deploy: keep it and remove its `ports:` entry
(reach it over an SSH tunnel; it asks for the browser token it prints at start-up), or delete the
service and point `OTEL_EXPORTER_OTLP_ENDPOINT` of every service at your own collector.

### Edge TLS and forwarded headers

The hosts speak plain HTTP; TLS terminates in front of them, at a reverse proxy (Caddy, nginx,
Traefik), a cloud load balancer or a Kubernetes ingress, which forwards to `api` and `web` over the
internal network. Then:

- **Tell the API which proxy to trust.** `ForwardedHeaders__KnownProxies__0` (the proxy's fixed IP)
  or `ForwardedHeaders__KnownNetworks__0` (a CIDR range, e.g. the compose network's subnet: pin it
  with an `ipam` block, read it with `docker network inspect <project>_aspire`). With neither, the
  API trusts loopback only (ADR-0021): it logs a startup warning, ignores `X-Forwarded-For` and
  `X-Forwarded-Proto`, every caller shares the proxy's rate-limit bucket, and logs show the proxy's
  address. The publisher's `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` cannot widen that trust.
- **Keep the hosts off the public network.** Only the proxy publishes ports. The web host takes
  `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` as the framework does (forwarded headers from any
  address), so it must be reachable only through the proxy; the probes stay internal too (below).
- **Redirect to HTTPS at the proxy.** Neither host can: the API has no HTTPS redirection at all, and
  the web host's finds no HTTPS port to redirect to behind a proxy and stays inert. Forward
  `X-Forwarded-Proto` so both see the original scheme; the web host then sends
  `Strict-Transport-Security` on HTTPS requests outside Development.

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
| `Email__Provider` / `Email__Smtp__*` | yes, in effect | `Smtp`: each message goes to the relay in `Email__Smtp__Host`, `__Port` (587), `__FromAddress` and, both or neither, `__UserName` / `__Password` (a secret), upgraded with STARTTLS — a relay that does not offer it fails the send. Checked at API boot only while `Smtp` is selected. A failed send is retried by the outbox, whose error records the status code and masked recipient, never the relay's reply; delivery is at least once, so a crash after the relay accepted can send a message twice. Not supported: implicit TLS (port 465 stops the API at boot) and OAuth2 (XOAUTH2) — either is a new adapter (`Infrastructure/Email`). `__UseStartTls=false` sends in clear, for a local mail catcher: refused with credentials, a startup warning outside Development. `Logging` (shipped): e-mail is **not sent** — each message becomes one log line (masked recipient, subject, length) and the API logs a startup warning outside Development. `None`: discard on purpose. Any other value stops the API at boot. |
| `ExternalApis__ExchangeRates__BaseUrl` / `__TimeoutSeconds` / `__MaxRetries` | no | The reference integration: Frankfurter v2 (`https://api.frankfurter.dev/v2/`), 10 s per attempt, 2 retries. Validated at API boot only — the migration job does not register outbound integrations. |
| `ForwardedHeaders__KnownProxies__0…` / `ForwardedHeaders__KnownNetworks__0…` | behind ingress | IPs / CIDR ranges of trusted proxies. Without them `X-Forwarded-For` is ignored (loopback-only default — spoof-safe) and every caller shares the ingress IP's rate-limit bucket. `aspire publish` output sets `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`; that switch cannot widen trust — with no list configured the API keeps the loopback-only default and logs a startup warning naming these settings (ADR-0021). A Blazor Server web host signs everyone in from one address: 10 logins/min per web host, by design. |
| `Kestrel__Limits__MaxRequestBodySize` / `Kestrel__Limits__MaxRequestHeadersTotalSize` | no | Request caps in bytes: 1 MB body (larger → 413) and 32 KB of headers by default. Raise per endpoint for a real upload, not globally. |
| `Cors__AllowedOrigins__0…` | if browsers call cross-origin | Allowed origins; **none by default** (same-origin only). |
| `Telemetry__IncludeErrorDetail` | no — Development only | `true` appends `Error.Description` to the use-case outcome line of a failure (event 1004 instead of 1002). Only `appsettings.Development.json` sets it; leave it unset elsewhere, because a description can repeat what a user typed or what a third party wrote, and the line is written at Information for every request. The error type and code identify the failure without it, and the response carries the text. |
| `Retention__*` | no | Janitor windows for the bookkeeping tables: `SweepInterval` (1h; 1 s – 1 day), `InitialDelay` before the first sweep (1m; 0 – 1h), `ProcessedOutboxAge` (3d; ≥ 1h), `DeadLetteredOutboxAge` (30d; ≥ 1 day), `IdempotencyKeyAge` (2d; ≥ 1h), `DeadRefreshTokenAge` (30d; ≥ 1 day — keep it ≥ `Authentication__RefreshTokenDays`, a consumed token is what makes a replay detectable). Every window is capped at 365 days. TimeSpan format, e.g. `7.00:00:00`. A value out of range stops the API at boot, naming the key. |
| `Outbox__*` | no | The outbox processor (ADR-0023): `PollInterval` (5 s; 100 ms – 10 min) between polls once a batch came back short, `BatchSize` (20; 1 – 1000) rows per claim, `MaxAttempts` (8; 1 – 50) failures before a message is dead-lettered, `BaseRetryDelay` (30 s; 1 s – 1 h) doubling per failure up to `MaxRetryDelay` (15 min; 1 s – 1 day, and ≥ `BaseRetryDelay`), `DispatchTimeout` (30 s; 1 s – 10 min) per message's handlers. Defaults retry after 30 s, 1 m, 2 m, 4 m, 8 m, 15 m, 15 m and dead-letter about 45 minutes after the first failure. A claimed row is leased for `DispatchTimeout × (BatchSize + 1)` (10.5 min): after a **crash** its in-flight rows reappear within that long (a graceful stop releases them at once) — shorten `BatchSize` or `DispatchTimeout` to shorten it. The lease is also the fence: a processor still working on a row after its lease was claimed again writes nothing more to it (the attempt is booked `lease_lost`), so a stale replica cannot undo the new owner's bookkeeping. TimeSpan format. A value out of range stops the API at boot, naming the key. |

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
- `trellis.outbox.retries` rate (`error.type` names the exception; `AggregateException` means several
  handlers of one message failed) — a handler's dependency is struggling; the per-attempt line is
  logged at Warning (event 1301) and each failed handler by name (event 1304). Its sibling handlers
  are not delayed: they ran and were credited, and only the failed ones are retried.
  `trellis.outbox.dispatch.duration` (by `trellis.outbox.outcome`) shows whether handlers approach
  `Outbox__DispatchTimeout`.
- `trellis.outbox.dispatch.duration` with `trellis.outbox.outcome=lease_lost` (event 1303, Warning) —
  a processor outlived its lease: a handler ignored its token past `Outbox__DispatchTimeout`, or the
  database was slower than the budget (event 1306 then says a batch was cut short for that reason).
  Another replica finished the row and the stale one wrote nothing; the handler that was in flight may
  have run twice. Occasional during a deploy or a database hiccup; steady means the lease is too short
  for the handlers or the database — raise `Outbox__DispatchTimeout`.
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
  by hand: they expire by themselves within `Outbox__DispatchTimeout × (Outbox__BatchSize + 1)`. The
  lease value is also what every bookkeeping write of the row is conditional on (`NextAttemptAtUtc` is
  its concurrency token), so an `UPDATE` of that column on a leased row revokes the lease: the processor
  holding it books `lease_lost` and discards its attempt, and the handlers it had checkpointed stay
  credited. Harmless, and pointless — the statements above touch dead letters only, which nobody holds.
- The single-row re-drive above is executed as written by `OutboxRetryTests` against PostgreSQL: the
  row gets a fresh budget, the handlers it had credited stay skipped, and the receipt is not re-sent.
- Re-driving a `poison` row without a code fix dead-letters it again on the next poll.
- `unknown_type` rows mean an event type was removed or renamed while rows were in flight: redeploy a
  type carrying that `[DomainEventName]` and re-drive them.
- Every outbox log line carries an event id (`OutboxLog`), so an alert matches the id, not the text:
  1301 retry scheduled (Warning), 1302 dead-lettered (Critical), 1303 lease lost (Warning), 1304 one
  handler failed while its siblings ran (Warning), 1305 a bookkeeping save failed and the row keeps its
  lease (Warning), 1306 a batch stopped short of its lease (Warning), 1307 leases released on stop
  (Information), 1308 a lease release failed (Warning), 1309 a poll failed and will be retried (Warning).

### Idempotency keys

| Idempotency | What to know |
|---|---|
| Table | `IdempotencyKeys` (ADR-0018): one row per `(user, method, route, Idempotency-Key)` hash. A row with a `StatusCode` is a completed request whose status, content type, `Body` and `Location` are replayed to a retry; a row without one is in flight. Rows are inserted by the command's own commit, so a failed command never leaves one. |
| Replay cap | Response bodies are stored up to 64 KB (per endpoint in code, `RequireIdempotency(maxReplayBodyBytes:)`; not a configuration setting). A larger response completes normally; its retry is a 409 `Idempotency.Duplicate` telling the client to read the resource. |
| Stuck in flight | A crash between a command's commit and the completion write leaves its row in flight: retries answer 409 `Idempotency.InFlight` with `Retry-After` until `Retention__IdempotencyKeyAge` (2 days) sweeps it. The command *did* apply; the client reads the resource. To free the key sooner: `DELETE FROM "IdempotencyKeys" WHERE "StatusCode" IS NULL AND "CreatedAtUtc" < now() - interval '1 hour';` — only after confirming no request that old can still be running (`Outbox__DispatchTimeout` bounds nothing here; the API's 30 s request timeout does). |
| Size | Bounded by `Retention__IdempotencyKeyAge` × write rate × (≤ 64 KB + a few hundred bytes) per keyed request; the janitor sweeps by creation time whatever the state. |
