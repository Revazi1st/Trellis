# Deployment

Production deployment of a generated solution: a quick start for one host, then the reference behind
it: container images, what `aspire publish` produces, edge TLS, the migration job, the required
configuration, probes, telemetry, and the outbox and idempotency runbooks.

## Deploy to one host

The shortest path to a running app on one Linux server with Docker. Steps 1 and 2 also need the .NET
SDK and the Aspire CLI ([development.md](development.md#prerequisites)), on the server or on the
machine you deploy from. Each step names the section further down that explains it.

1. **Build the images**, tagged with the commit
   ([Container images](#container-images--no-dockerfiles-by-design)):

   ```bash
   tag=$(git rev-parse --short HEAD)
   dotnet publish src/Presentation/Api/Trellis.Api /t:PublishContainer -p:ContainerImageTag=$tag
   dotnet publish tools/Trellis.MigrationService /t:PublishContainer -p:ContainerImageTag=$tag
   dotnet publish src/Presentation/UI/Trellis.UI.Web /t:PublishContainer -p:ContainerImageTag=$tag   # if the UI is included
   ```

   On the server they land in its Docker as `trellis-api`, `trellis-migrationservice` and
   `trellis-ui-web`. Built elsewhere, push them to a registry (`-p:ContainerRegistry=…`, plus
   `--os linux --arch x64` on an Arm machine building for an x64 server) and let the server pull them.
2. **Write the compose files**: `aspire publish -o deploy` writes `deploy/docker-compose.yaml` and
   `deploy/.env` ([Docker Compose](#docker-compose-what-aspire-publish-produces)). It asks for the two
   secret parameters, `signing-key` and `seed-admin-password`, unless the AppHost's configuration
   holds them (`Parameters:signing-key`, `Parameters:seed-admin-password`), and remembers the answers
   on that machine; either way, the files it writes leave every value for you to fill in.
3. **Fill `deploy/.env`** ([Required configuration](#required-configuration)): the image names with
   your tag (`API_IMAGE=trellis-api:<tag>`, `MIGRATIONS_IMAGE`, and `WEB_IMAGE` with the UI), the
   container ports (`API_PORT=8080`, and `WEB_PORT=8080` with the UI), a strong `POSTGRES_PASSWORD`,
   `SIGNING_KEY` (32 characters or more: `openssl rand -hex 32`) and `SEED_ADMIN_PASSWORD`, the first
   administrator's password, which must pass Identity's rules (a digit, an upper-case letter and a
   symbol). The file holds secrets: keep it out of version control.
4. **Start it**: `docker compose -f deploy/docker-compose.yaml up -d`. The migration job runs once and
   the API starts after it exits 0 ([Migrations](#migrations-a-one-shot-pre-deploy-job)); PostgreSQL, the
   API and the web host restart with Docker. Run on the server instead, `aspire deploy` does steps 1 to
   4 in one command: it builds the images, asks for the values it does not hold and runs
   `docker compose up` itself.
5. **Check it** ([Health probes](#health-probes)): `docker compose -f deploy/docker-compose.yaml port api 8080`
   prints the API's host port; `curl http://localhost:<port>/health` answers `Healthy`, and
   `POST /api/v1/auth/login` with `admin@example.com` and your `SEED_ADMIN_PASSWORD` answers with a
   token.
<!--#if (UseRedis) -->

With `--use-redis`, `.env` also lists `CACHE_PASSWORD`, the password of the `cache` service, which
stays on the internal network and restarts with Docker like the others.
<!--#endif -->

Before real traffic: TLS and the trusted-proxy lists at a reverse proxy in front of the API and the web
host ([Edge TLS](#edge-tls-and-forwarded-headers)), real e-mail (`Email__Provider`), and backups of the
PostgreSQL volume. The Aspire dashboard listens on the server's loopback only: open it through an SSH
tunnel ([below](#docker-compose-what-aspire-publish-produces)). Another target, Azure Container Apps or
Kubernetes, is an extension of your own: the Aspire hosting package for it in the AppHost, and the
migration job as a step the rollout waits on ([Migrations](#migrations-a-one-shot-pre-deploy-job)).

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
Tag each image with the commit it was built from (`-p:ContainerImageTag=$(git rev-parse --short HEAD)`),
or with the version of a release build, rather than only the default `latest`: `service.version` in the
telemetry already carries the commit (the informational version, `1.0.0+<sha>`), so a trace leads to
the image that produced it, and a rollback names one.

**A software bill of materials.** GitHub's dependency graph exports an SPDX SBOM of every package the
solution references (*Insights → Dependency graph → Export SBOM*, or
`gh api repos/{owner}/{repo}/dependency-graph/sbom`). An SBOM of what an image actually contains comes
from the pipeline that publishes it: a scanner over the pushed image or over the `dotnet publish`
output, optionally attested with `actions/attest-sbom`, which, like provenance attestation, needs a
public repository or GitHub Enterprise Cloud. CI here builds the images only to smoke-test them, so it
produces none.

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
| `postgres` | `postgres:18`, a named data volume, port 5432 on the internal `aspire` network only (`expose:`), password from `POSTGRES_PASSWORD` in `.env`, `restart: unless-stopped` | Set a strong `POSTGRES_PASSWORD`; plan backups of the volume. Keep the major version pinned: a data volume does not survive a major upgrade |
| `migrations` | `${MIGRATIONS_IMAGE}`, no ports, starts once PostgreSQL has *started* (not once it is ready; the job's retrying execution strategy rides out a normal start-up), `Seed__AdminPassword` from `SEED_ADMIN_PASSWORD` in `.env` | Fill `SEED_ADMIN_PASSWORD` (the job refuses the default outside Development); consider `Seed__AdminEmail`. No restart policy: it is a one-shot job, and rerunning `docker compose up` reruns it safely |
| `api` | `${API_IMAGE}`, published as `"${API_PORT}"` (the container port on an **ephemeral host port**), starts only after `migrations` exited 0, `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, `Authentication__SigningKey` from `SIGNING_KEY` in `.env`, `restart: unless-stopped` | Fill `SIGNING_KEY` (the API refuses to boot without it); add `Email__Provider`, `Registration__Mode` if wanted and the `ForwardedHeaders__*` lists (below); publish a fixed host port or none (behind the proxy) |
| `web` (with the UI) | `${WEB_IMAGE}`, `"${WEB_PORT}"` on an ephemeral host port, service discovery pointed at `http://api:${API_PORT}`, `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`, `restart: unless-stopped` | Reach it only through the proxy (below) |
| `compose-dashboard` | The Aspire dashboard (`mcr.microsoft.com/dotnet/nightly/aspire-dashboard`), its UI published on the host's loopback only (`127.0.0.1:18888`), OTLP on 18889/18890 internal, `restart: always`; every .NET service exports its logs, metrics and traces to it | Reach it over an SSH tunnel, or replace it with your own collector (below) |

`.env` holds the parameters: the three image names, the two container ports, the PostgreSQL
password, the signing key (`SIGNING_KEY`) and the first administrator's password
(`SEED_ADMIN_PASSWORD`), empty until you fill them (`aspire deploy` writes a filled copy of its own
beside it). The two secrets are
parameters the AppHost declares only for publishing, so `aspire publish` asks for them unless the
AppHost's configuration holds them; nothing it writes carries a value. Build the images with
`PublishContainer` (above) and put their names in `.env`. Nothing in the file is TLS, a health check
or a resource limit; nothing sets `ASPNETCORE_ENVIRONMENT`, so every host runs as Production.

**The dashboard.** It is a development tool that also works in production, but it keeps telemetry in
memory only and shows every log line and trace (masked, but still operational detail), so the AppHost
publishes its UI on the server's loopback only. Reach it over an SSH tunnel,
`ssh -L 18888:localhost:18888 <server>`, then <http://localhost:18888>; it asks for the browser token
it prints at start-up (`docker compose logs compose-dashboard`). To keep telemetry beyond a restart,
or across several servers, turn it off instead (`.WithDashboard(false)` on the compose environment in
the AppHost) and point `OTEL_EXPORTER_OTLP_ENDPOINT` of every service at your own collector.

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
| `Authentication__SigningKey` | yes | JWT HMAC-SHA256 key, **≥ 32 characters or the API refuses to boot**. Store in a secret store; rotate by redeploying (outstanding access tokens die within `AccessTokenMinutes`). The published compose file reads it from `SIGNING_KEY` in `.env`. |
| `Seed__AdminPassword` | yes (migration job, fresh DB) | Bootstrap admin credential; seeding refuses the default outside Development. The published compose file reads it from `SEED_ADMIN_PASSWORD` in `.env`. |
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

- **`/health`** — readiness: all checks, including the database, queried once per probe by the
  `database` check (`AddDbContextCheck`).
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
  the prefix, and in the [Telemetry reference](#telemetry-reference) below) — plus ASP.NET Core, HttpClient and runtime instrumentation, the `Polly` meter
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

**Sampling.** Every host records every trace by default (the SDK's parent-based always-on sampler):
right for development and a small deployment, expensive at production volume. To sample, set
`OTEL_TRACES_SAMPLER=parentbased_traceidratio` and `OTEL_TRACES_SAMPLER_ARG` to the share to keep
(`0.1` keeps one trace in ten) on every host. Parent-based matters: a host that receives a sampled
call records its part of the trace, so a request that crosses hosts stays one whole trace, which a bare
`traceidratio` sampler, deciding afresh on each host, breaks apart. To keep every failing trace, leave the
SDK at 100 % and tail-sample at an OpenTelemetry collector instead. Sampling thins spans only: counts come
from the metrics (ASP.NET Core's `http.server.request.duration`, the instruments below) and from the
use-case outcome lines, which are written whatever the decision. An outbox dispatch span follows the
decision of the request that raised the event, so a message from an unsampled request has no span, but
its failures still reach `trellis.outbox.retries`, `trellis.outbox.dead_lettered` and the Warning lines.
Leave Development at the default: the Aspire dashboard is more useful with every trace.
<!--#if (IncludeUI) -->

The web host runs the same ServiceDefaults: set the two variables on it as well as on the API, so a page
that calls the API is sampled as one trace.
<!--#endif -->

The invoice read cache keeps an entry 30 seconds in each API instance's memory (L1) and, with the
`UseRedis` toggle, five minutes in Redis (L2). A write evicts the entry from its own instance and
from Redis only, so with several replicas another replica can serve the previous state, ETag
included, for up to 30 seconds; without Redis every replica caches on its own for the same 30 seconds.

### Telemetry reference

Every source, meter, instrument, attribute and log event id the code emits, as dashboards and alerts
should name them. `TelemetryNamingTests` reads this section and the code and fails both ways: a name,
unit or event id listed here that the code does not emit, and one the code emits that is not listed.
Change the two together.

**Spans**

| Source | Span | Attributes |
|---|---|---|
| `Trellis.UseCases` | one per use case, named after its message type (`CreateInvoiceCommand`), inside the request span | `trellis.usecase.outcome`: `success`, `failure`, `exception` or `cancelled`. A failed `Result` adds `trellis.usecase.error_code` (its code) and `trellis.usecase.error_type` (its `ErrorType`), not `error.type`, so a 404 does not count as an error. Only an exception sets `error.type` (its full type name), status Error with its message, and the exception event |
| `Trellis.Outbox` | `outbox <event type>` (`outbox invoices.paid.v1`), one per dispatch attempt, under the trace of the request that raised the event | `trellis.outbox.message_id`, `trellis.outbox.event_type`, `trellis.outbox.attempt` (from 1), `trellis.outbox.outcome`: `processed`, `retry_scheduled`, `dead_lettered` or `lease_lost`; `trellis.outbox.reason` on a dead letter: `exhausted`, `unknown_type` or `poison`; `error.type`: the failing exception's full type name, or the `Outbox.*` code of an unreadable row |
| `Trellis.MigrationService` | `Migrate and seed database`, once per run | — |

The ASP.NET Core request span carries `trellis.correlation_id`, the response's `X-Correlation-ID`. Every
signal's resource carries `service.version` and `deployment.environment.name` (*Who emits it*, above).

**Metrics**

| Instrument | Meter | Kind | Unit | Attributes |
|---|---|---|---|---|
| `trellis.validation.failures` | `Trellis.UseCases` | counter | `{failure}` | `trellis.usecase.name` (the message type), `trellis.validation.code` (the rule's code, never the field or the value); counted at the outcome, so a domain rule's code counts beside the validators' |
| `trellis.invoices.issued` | `Trellis.Invoices` | counter | `{invoice}` | `trellis.invoice.currency` |
| `trellis.invoices.paid` | `Trellis.Invoices` | counter | `{invoice}` | — |
| `trellis.outbox.dispatched` | `Trellis.Outbox` | counter | `{message}` | `trellis.outbox.event_type` |
| `trellis.outbox.retries` | `Trellis.Outbox` | counter | `{message}` | `trellis.outbox.event_type`, `error.type` (the exception's short type name) |
| `trellis.outbox.dead_lettered` | `Trellis.Outbox` | counter | `{message}` | `trellis.outbox.event_type`, `trellis.outbox.reason` |
| `trellis.outbox.dispatch.duration` | `Trellis.Outbox` | histogram | `s` | `trellis.outbox.event_type`, `trellis.outbox.outcome` |
| `trellis.outbox.pending` | `Trellis.Outbox` | gauge | `{message}` | — (every replica reports the same number: take `max`) |
| `trellis.outbox.oldest_pending_age` | `Trellis.Outbox` | gauge | `s` | — (take `max`) |

**Log event ids** — match an alert on the id, never on the text.

| Range | Written by | Events |
|---|---|---|
| 10xx | the use-case outcome line (`LoggingDecorators`), one per invocation inside the `UseCase {UseCase}` scope, with `ElapsedMilliseconds` | 1001 succeeded (Information); 1002 failed, with `ErrorType` and `ErrorCode`; 1003 failed validation, adding the `ValidationFailures` field:code pairs; 1004 failed with `ErrorDescription`, only while `Telemetry__IncludeErrorDetail` is on; 1005 cancelled by the caller (Information); 1006 threw (Error, with the exception). 1002–1004 are Information for a classified 4xx outcome and Warning otherwise |
| 11xx | the security events (`SecurityEventLog`) | 1101–1106, listed in [features/auth.md](features/auth.md) under *Security events* |
| 12xx | e-mail (`Infrastructure/Email`) | 1201 not sent, the `Logging` stand-in (Information); 1202 discarded, `None` (Debug); 1203 the stand-in runs outside Development (Warning); 1204 e-mail switched off (Information); 1205 sent through the relay (Information, masked recipient); 1206 SMTP in clear outside Development (Warning) |
| 13xx | the outbox processor (`OutboxLog`); a message's lines inside the `OutboxMessage {MessageId} {EventType}` scope | 1301–1309, listed under [Outbox operations](#outbox-operations) |

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
