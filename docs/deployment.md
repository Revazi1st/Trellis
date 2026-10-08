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
| `ExternalApis__ExchangeRates__BaseUrl` / `__TimeoutSeconds` / `__MaxRetries` | no | The reference integration: Frankfurter (`https://api.frankfurter.dev/v1/`), 10 s per attempt, 2 retries. Validated at API boot only — the migration job does not register outbound integrations. |
| `ForwardedHeaders__KnownProxies__0…` / `ForwardedHeaders__KnownNetworks__0…` | behind ingress | IPs / CIDR ranges of trusted proxies. Without them `X-Forwarded-For` is ignored (loopback-only default — spoof-safe) and every caller shares the ingress IP's rate-limit bucket. `aspire publish` output sets `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`; that switch cannot widen trust — with no list configured the API keeps the loopback-only default and logs a startup warning naming these settings (ADR-0021). A Blazor Server web host signs everyone in from one address: 10 logins/min per web host, by design. |
| `Kestrel__Limits__MaxRequestBodySize` / `Kestrel__Limits__MaxRequestHeadersTotalSize` | no | Request caps in bytes: 1 MB body (larger → 413) and 32 KB of headers by default. Raise per endpoint for a real upload, not globally. |
| `Cors__AllowedOrigins__0…` | if browsers call cross-origin | Allowed origins; **none by default** (same-origin only). |
| `Retention__*` | no | Janitor windows for the bookkeeping tables: `SweepInterval` (1h; 1 s – 1 day), `InitialDelay` before the first sweep (1m; 0 – 1h), `ProcessedOutboxAge` (3d; ≥ 1h), `DeadLetteredOutboxAge` (30d; ≥ 1 day), `IdempotencyKeyAge` (2d; ≥ 1h), `DeadRefreshTokenAge` (30d; ≥ 1 day — keep it ≥ `Authentication__RefreshTokenDays`, a consumed token is what makes a replay detectable). Every window is capped at 365 days. TimeSpan format, e.g. `7.00:00:00`. A value out of range stops the API at boot, naming the key. |

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
(the exporter is registered only when the variable is present). Worth alerting on:

- `trellis.outbox.dead_lettered` — a domain event exhausted its retries or lost its handler;
  dead-lettered rows are the alert queue and are kept 30 days by default.
- Outbox dispatch spans rejoin the originating request's trace (`traceparent` is persisted with
  the message), so a failed handler is visible under the request that caused it.

Health-probe requests are filtered out of tracing — probes don't pollute the trace stream.
