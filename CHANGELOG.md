# Changelog

All notable changes to Trellis are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added
- **The UI shows what the API said.** `Api.Contracts` gains `ProblemResponse` and `FieldError`
  (a tolerant mirror of the RFC 9457 body); `ApiResult<T>` carries `StatusCode`, `Problem` and the
  field `Errors`; one `ApiResponses` describer turns problem codes, a 429's `Retry-After`,
  validation errors and an unreachable API into user-facing sentences (previously each client had
  a fixed sentence such as `Request failed (409).`). The seeded-admin login hint renders only in Development
  (`UiOptions.ShowDevelopmentHints`).
- **Generated CI**: `--use-redis` projects start Redis and pass `ConnectionStrings:cache` in the
  `api-contract` and `ui-tests` jobs (the generated API's `/health` answered 503 before), and the
  maintainer-only template toggle check no longer ships as a permanently skipped step (new
  `TemplateSource` constant symbol). `scripts/check-template-toggles.sh` now fails when a
  template.json exclude glob stops matching a tracked file and understands doubled-hash YAML
  conditionals.
- **Docs & AI assets**: ADR-0021 (API hardening defaults) and ADR-0022 (tenant on the principal,
  superseding the header parts of ADR-0019); `docs/threat-model.md` rewritten with a status per
  gap, and the docs-drift check requires it to move with auth, identity and hardening code; the
  template and package author is "Trellis contributors"; the README states which Docker engines
  this repository actually exercises.
- **Result combinators** (`Bind`/`Map`/`Tap`/`Ensure` + async overloads, `Result.Combine` that
  aggregates every value-object failure into one response) and a **`Currency` value object** —
  `CreateInvoiceCommandHandler` is the refactored showcase. `Money` now rejects sub-cent scale
  (the database silently truncated it before), `Zero` validates its code, subtraction can no
  longer mint negative money, and `Entity<TId>` gained `==`/`!=` consistent with `Equals`.
- **Use-case telemetry**: the logging decorator now logs failures *and throws* with elapsed time
  under a correlation scope (a SaveChanges crash previously produced no use-case log at all), and a
  new tracing decorator emits one span per use case (pipeline: Validation → Logging → Tracing →
  UnitOfWork).
- **OpenAPI as a contract**: bearer security scheme (Scalar can finally authorize),
  `Idempotency-Key` header + 409 documented on idempotent endpoints, 401/403 on secured ones, and
  the document is committed at `docs/api/openapi.v1.json` with a CI diff gate
  (`scripts/export-openapi.sh`).
- **Conditional GET**: `InvoiceDto.Version` exposes the xmin token; `GET /invoices/{id}` emits a
  weak ETag and honors `If-None-Match` with 304.
- **RFC 9457 everywhere**: 429s carry a problem body + `Retry-After`; JwtBearer 401/403 challenges
  carry problem bodies instead of empty responses.
- **Complete UI lifecycle**: add-line + issue controls on the invoice detail page (drafts are no
  longer dead ends) and a Register page.
- **Template UX**: `dotnet new trellis` prints next steps, an SDK-version constraint fails wrong
  hosts at instantiation instead of first build, maintainer-only workflows (template-ci, release)
  no longer ship into generated repos, and `scripts/check-template-toggles.sh` guards toggle
  wiring in CI.
- **Docs & AI assets**: production `docs/deployment.md` and `docs/upgrading.md`; `docs/features/auth.md`;
  layer rules for Infrastructure/SharedKernel/tools plus post-audit conventions in the existing
  rules; `remove-feature` and `add-toggle` skills; the `docs-not-needed` PR-label escape hatch the
  docs promised; Dependabot now watches the container images; SharedKernel primitives
  (dispatcher, entity equality, paged list, ID JSON) got direct unit tests.
- **Generated CI covers the API contract and the no-SDK path**: a generated project's `ci.yml`
  gains an `api-contract` job (starts postgres, migrates and seeds, exports the OpenAPI document and
  diffs it against `docs/api/openapi.v1.json`) and an `sdk-container` job (builds `scripts/sdk-image`
  on Linux and runs the build plus the Testcontainers integration tests through `scripts/dotnet.sh`).
  The `ui-tests` job ships only in projects created with `--include-ui-tests` (and the UI).
- **Template CI on every change, with a content gate**: `template-ci.yml` runs on every push and PR
  (no path filter) and gates `release.yml`. Every matrix leg now fails on template-named paths,
  unprocessed conditionals, template-prefixed environment variables and files that never belong in
  a repository (`.DS_Store`, local appsettings, `.pfx`), runs the generated project's own ADR link
  check and actionlint, and the default and no-ui legs run the generated tests against the shipped
  migration.
- **Prerequisites and the CLI-free start**: the README lists Docker, the .NET 10 SDK, the Aspire CLI
  (`dotnet tool install -g Aspire.Cli`) and pwsh (Playwright), and both README and
  `docs/development.md` show `dotnet run --project tools/<Name>.AppHost` for running without the
  CLI. `aspire.config.json` is committed, so `aspire` commands find the AppHost from any folder.
- **Dependabot grouping**: `Microsoft.Testing.*` and `Microsoft.Playwright` move together with
  xunit.v3; `dotnet-ef` (from `.config/dotnet-tools.json`) and the bare `Microsoft.EntityFrameworkCore`
  package join the Microsoft group; new `dotnet-sdk` (global.json, majors ignored) and grouped
  GitHub Actions updates.

### Reliability & testing
- **Blazor sessions survive transient refresh failures**: only a 401 from `POST /auth/refresh`
  ends the session; a 429, a 5xx or an unreachable API keeps the token pair and holds off further
  refresh attempts for 5 s (previously any failed rotation signed the circuit out).
- **New suites**: `EndpointMetadataTests` (every `/api/` endpoint is anonymous by allowlist or
  carries a catalogued permission policy, idempotency on writes, summary and description),
  `ForwardedHeadersTests`, `AuthRateLimitTests` (every test client now has its own simulated
  address, so fixtures no longer drain one shared anonymous partition), `RequestLimitTests` on a
  real Kestrel listener, `AuthFlowTests` (15 end-to-end auth facts: lockout, replay, revoke-all,
  expiry, timing), `TenantIsolationTests` plus four more `TenancyTests`, `ApiTests/Ui` (39 tests
  over the real `AddSharedUi` graph with a scripted handler and a manual clock), and unit tests
  for the auth validators and handlers. The in-process host disables Aspire's Redis health check
  and opens registration itself, so the generated tests pass in every toggle leg, Serilog and
  Redis included.
- **Outbox, production-shaped**: skip-locked batch claiming (replicas never double-dispatch),
  exponential backoff (30s→4m instead of five retries in 25 seconds), explicit
  Pending/Processed/DeadLettered status with a critical log + `trellis.outbox.dead_lettered`
  metric (exhausted and unresolvable messages were previously dropped in silence), per-handler
  completion tracking (retries never re-run a handler that succeeded — no double emails or
  double-counted metrics), stable versioned wire names via `[DomainEventName]` (refactoring never
  orphans in-flight rows), and `traceparent` + tenant propagation across the async boundary.
- **Data retention**: a janitor prunes processed outbox rows, stale idempotency keys and dead
  refresh tokens (previously all three tables grew forever); the outbox pending-scan index is now
  partial, so it stays small regardless of history.
- **In-process API tests** (`WebApplicationFactory` + Testcontainers): the real HTTP pipeline —
  JWT 401/403, permission policies, RFC 9457 shape, idempotency reserve/duplicate/release, the
  auth rate limit — now runs inside the SDK container on every `./scripts/dotnet.sh test`.
  Previously nothing covered the Presentation ring in the default loop.
- New integration tests: idempotency concurrent-reserve race, tenancy isolation + ambient-tenant
  restore (compiled into tenant-enabled generated solutions; the all-options template-CI leg runs
  them), migration-drift guard (`HasPendingModelChanges`), domain-event serialization fitness.
- **Conventions enforced, not asserted**: architecture tests now verify value objects (sealed
  records with `Create → Result<T>`), aggregates (no public constructors/setters), domain events
  (sealed records), endpoints (internal sealed), and — with the UI included — that the UI depends
  only on `Api.Contracts`. The ADR-0011 license ceilings became an MSBuild error instead of prose.
- **CI**: code coverage via out-of-process `dotnet-coverage` (the in-process collector crashed the
  xunit.v3 host), TRX results attached to failed runs, and a Playwright job that actually boots
  PostgreSQL + API + Web and runs the browser tests (they had never executed in any pipeline);
  the browser suite gained wrong-password and create-invoice flows.

### Security
- **Endpoints require authorization by default** (ADR-0021). The versioned API group calls
  `RequireAuthorization()`; the four auth endpoints are the explicit anonymous allowlist, `/health`
  and `/alive` stay on the host, and a test fails any endpoint that is neither anonymous nor
  carrying a permission from the catalogue. `RequirePermission` with an unknown name now throws at
  startup instead of minting a policy nobody can satisfy. An endpoint you add is authenticated
  unless you say otherwise.
- **Registration is closed by default.** `POST /auth/register` answers 403 `Auth.RegistrationClosed`
  unless `Registration:Mode` is `Open` (Development sets `Open`; production opts in explicitly).
  Login costs one password verification whether or not the e-mail exists (timing no longer reveals
  which addresses are registered), and failed sign-ins and lockouts are logged as events 1101-1104
  with the e-mail masked and never a password.
- **Forwarded headers: trust never widens.** With `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` and no
  trusted proxy configured (what Aspire's publishers emit), the framework trusted `X-Forwarded-For`
  from anyone, so a caller could pick its own rate-limit partition. The API restores loopback-only
  trust, applies the middleware once, and logs a startup warning naming
  `ForwardedHeaders__KnownNetworks__0` / `ForwardedHeaders__KnownProxies__0`.
- **Rate limits by credential type.** Only register and login carry the strict `auth` policy;
  refresh and logout fall under the global limiter (a web host relaying many users no longer locks
  them all out of refresh); `/health` and `/alive` are exempt, so an orchestrator probing past
  100/min never sees 429.
- **The tenant comes from the token, not a header** (ADR-0022, `--use-multitenancy`). `X-Tenant-Id`
  was trusted from any caller and is now ignored. Users carry a `TenantId`, the JWT carries a
  `tenant` claim, a token without it is rejected (401, `invalid_token`), and every aggregate root is
  tenant-filtered by convention (`IHasDomainEvents`) instead of per entity configuration.
  Self-registration provisions a fresh tenant; `Seed:TenantId` (default `default`) names the seeded
  admin's. Enabling the toggle regenerates the initial migration with `AspNetUsers.TenantId`.
- **Hardening defaults.** HS256 is the only accepted signing algorithm; 401s carry
  `WWW-Authenticate: Bearer` (`error="invalid_token"` on a failed token); every API response gets
  `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY` and
  `Content-Security-Policy: frame-ancestors 'none'`, and `/auth/*` responses add `Cache-Control:
  no-store`; Kestrel caps request bodies at 1 MB (413 above it) and request headers at 32 KB,
  overridable through `Kestrel:Limits:*`. The Blazor host sends its own CSP (`script-src 'self'`,
  `frame-ancestors 'none'`), `X-Frame-Options: DENY`, nosniff and `Referrer-Policy: same-origin`.
- **Auth input is validated.** Login, refresh and logout answer 400 problem details for missing or
  oversized fields instead of 500, and an oversized password costs no hash work.
- **Every generated project gets its own Development signing key** (`dev-only-` plus 64 hex
  characters, minted at `dotnet new` time), so no two projects share the template's placeholder.
- **Account lockout**: five failed sign-ins lock the account for 15 minutes (403
  `Auth.AccountLockedOut`); a successful login resets the counter. Previously the Identity lockout
  columns were dead schema and per-account brute force was unthrottled.
- **Fail-fast JWT signing key**: the API refuses to start when `Authentication:SigningKey` is
  missing or under 32 characters (previously an empty key surfaced as an opaque token-stack error,
  possibly only on first login). The API project now has a `UserSecretsId` for dev keys.
- **Seed guards**: demo invoices seed only in Development, and the MigrationService refuses to seed
  the well-known default admin password outside Development.
- **Session revocation**: new `POST /auth/logout` (revoke one refresh token, idempotent) and
  `POST /auth/revoke-all` (sign out everywhere). Presenting an already-consumed refresh token now
  revokes the user's whole session family (stolen-token replay detection, OAuth BCP) with a logged
  warning. `RefreshTokens` gained the missing FK to `AspNetUsers` (cascade); the initial migration
  was regenerated (pre-release convention).
- **Workflow hardening**: least-privilege `permissions:` and `timeout-minutes` on all jobs; actions
  pinned by commit SHA; releases are gated on build+tests and an instantiate-from-the-nupkg smoke,
  publish via NuGet trusted publishing (OIDC — set the `NUGET_USER` repo variable) instead of a
  long-lived API key, attach build provenance, and create a GitHub Release from the CHANGELOG
  section (tagging without notes fails the release).

### Fixed
- **`GET /invoices/{id}` under `--use-multitenancy` answered 404 for the caller's own tenant** right
  after creation: HybridCache runs its factories on a detached flow where the request's tenant was
  invisible, so the filtered query ran as the system scope. `TenantContext` now remembers the
  tenant per scope.
- **`--include-ui false` output shipped five empty `ApiTests/Ui/*.cs` files**; the folder is now
  excluded together with the UI.
- **`aspire run` (and the Aspire E2E) pointed every resource at the wrong database.** The IDE
  launch profiles pass a hardcoded dev connection string as a command-line argument (needed for
  the dev-container flow), and arguments outrank the connection string Aspire injects — so
  Aspire-launched resources silently targeted the compose database instead of the provisioned
  one. The AppHost now excludes launch profiles for its project resources and sets Development
  explicitly in run mode. Found by the first CI run in the project's history.
- **`--include-ui false` solutions did not compile in Release** — the AppHost's `api` variable
  became unused (S1481) once the UI block was stripped; the run-mode environment block now uses
  it unconditionally.
- **Project names containing spaces produce broken solutions** (the engine renames paths and
  namespaces differently); the unsupported matrix leg is gone and the README documents the
  naming rule.
- **`--use-multitenancy` generated an app that could never start.** `ITenantContext` was registered
  only in the API host; the MigrationService (and the integration-test host) could not resolve
  `AppDbContext`, the migration worker exited nonzero and the API waited on it forever. A
  `SystemTenantContext` (no tenant) is now registered in every non-HTTP host, and the template-CI
  all-options leg runs the generated tests so a DI-only failure can't hide behind a green build.
- **`--include-ui false --include-ui-tests true` generated a solution that could not restore** —
  the `.slnx` kept the UiTests project entry while the folder was excluded. The slnx conditional now
  matches template.json (`IncludeUiTests && IncludeUI`), and the combo is a template-CI matrix leg.
- **The packed template misplaced its three extension-less files** (`LICENSE`, `CODEOWNERS`,
  `scripts/sdk-image/Dockerfile` became directories nesting the file), breaking the generated dev
  container. `PackagePath` is now the bare `content` folder, which NuGet expands per-file correctly.
- **`dotnet new trellis` hung forever on non-TTY hosts** — the chmod post-action prompts, and
  with no TTY `dotnet new` loops on the unanswerable prompt. CI instantiation passes
  `--allow-scripts yes` (interactive use still gets the normal prompt).
- **Blazor sessions died after 15 minutes.** The refresh token was stored but never used and `exp`
  was never checked. The auth state provider now rotates expired access tokens through
  `POST /auth/refresh` (single-flight), and `BearerTokenHandler` attaches a currently-valid token.
- **UI retries could duplicate commands.** The resilience handler auto-retries POSTs but no
  `Idempotency-Key` was ever sent; the typed client now sends a fresh key per logical POST.
- **Rate limiting never partitioned per user and collapsed behind a proxy.** The limiter ran before
  `UseAuthentication` (anonymous principal ⇒ per-user branch dead) and no forwarded headers were
  honoured (all callers shared the proxy's bucket). The limiter now runs after authentication keyed
  by the user-id claim, and `ForwardedHeaders` config trusts explicitly-listed proxies/networks.
- **Idempotency over-reported 409 and burned keys.** Any `DbUpdateException` (over-length key,
  transient DB error) was reported as "duplicate"; keys were raw global strings shared across all
  callers; and a failed `Result` kept the reservation forever. Keys are now scoped
  `(user, route, key)` and hashed, only a Postgres unique violation means duplicate, over-length
  keys get 400, and failed outcomes release the reservation.
- **Refresh-token rotation was not atomic** — concurrent replays could both consume one token; the
  consume is now a single conditional `UPDATE`.
- **The invoice cache could leak across tenants and pin 404s.** Cache keys now carry the tenant
  when multi-tenancy is on (a cache hit bypasses the EF tenant filter), and a cached miss is
  removed instead of answering 404 for the whole TTL.
- **The Aspire E2E test read the overdue listing as a bare array** but the endpoint returns a
  `PagedResponse` envelope — it failed on first real run.
- **The decorator-pipeline test silently skipped the Auth slice.** It hand-listed seven Invoices
  handlers; it now discovers every command/query by reflection and asserts each resolves through
  the full Validation → Logging → UnitOfWork pipeline.
- The Blazor invoice list showed only the first page with no controls (pager added), and a failed
  action's error message was wiped by the reload that followed it.
- Pinned transitive `SSH.NET` to 2026.0.0 (GHSA-q939-rpr3-3284 flagged by NuGetAudit).
- **`--use-serilog` solutions could not build in Release** — `WriteTo.Console()` without a format
  provider is CA1305, and generated solutions treat warnings as errors in Release. Found by
  actually building a generated all-options solution end to end.
- **Template package was unusable.** `dotnet pack` flattened the whole repo into `content/`
  (293 entries, none nested), dropped colliding files (`NU5118`) and omitted
  `.template.config/`, so `dotnet new install Trellis.Templates` could not work at all.
  Packing now preserves the tree via `PackagePath="content\%(RecursiveDir)…"`.
- **Project names containing a dot produced a solution that did not compile.** `AddTrellisUi`
  became `AddAcme.ShopUi`, and Aspire's `Projects.Trellis_Api` became `Projects.Acme.Shop_Api`.
  The DI extension is renamed `AddSharedUi` (name-independent, like every sibling), and derived
  symbols now map the name to Aspire's `_`/`-` forms for project classes and resource names.
- `.template.config/` no longer ships into generated projects.
- `scripts/check-adr-links.sh` is now made executable by the post-action; CI runs it.
- **The generated UI was unstyled in every project not named after the template**: `App.razor`
  linked `trellis-ui.css`, which the rename turned into a 404. The stylesheet is now
  `_content/<Name>.UI.Common/app.css`.
- **Seeded admin is `admin@example.com`** (password `Admin123!` unchanged) everywhere: seed default,
  login hint, UI tests, Aspire E2E and docs. An existing dev database volume still holds the old
  user; the migration worker adds the new one on its next run.
- **`dotnet new trellis` no longer prompts for `--allow-scripts`**: the helper scripts are made
  executable by the engine's dedicated chmod action (the previous run-process action hung on a
  non-TTY stdin). All next-step messages now print, not just the first, with the real project paths,
  the Aspire CLI install line and the seeded credentials; `dotnet new trellis --help` shows a
  description.
- **Kebab-case and spaced names produce a consistent tree**: `-n acme-shop` wrote `acme-shop.*`
  folders but `acme_shop.*` into the solution file, so 18 project paths were missing and restore
  failed. Paths, the solution file and the Aspire project/resource names now agree. Such names still
  fail analyzer rule CA1707 (underscores in namespaces), so creation prints a warning that names the
  resulting identifier and asks for a dotted name such as `Acme.Shop`; both READMEs document the rule.
- **`--include-ui false` projects failed their own ADR link check**: `.claude/rules/ui.md` is now
  dropped together with the UI.
- **Environment variables follow the project name**: the SDK-image and design-time-database
  variables used by `scripts/dotnet.sh`, `scripts/ef.sh` and `AppDbContextFactory` are
  `<NAME>_SDK_IMAGE` / `<NAME>_DESIGNTIME_DB` in generated projects instead of the template's prefix.
- **Package content mirrors `.gitignore`**: `.DS_Store`, local appsettings, `.pfx`/`.key`,
  `TestResults`, `.aspire`, `nupkg`, coverage files, agent worktrees and a worktree's `.git` file
  never ship; template.json and the pack csproj exclude the same set. `--skip-restore` is hidden in
  IDE dialogs (the CLI flag is unchanged).
- **Shipped docs were truncated and maintainer-only files leaked**: prose in `.claude/rules/tools.md`,
  ADR-0010 and ADR-0019 quoted a conditional-directive token mid-line, which the engine reads as a
  directive, so generated copies lost their tails (half of `tools.md` with `--include-ui false`).
  The lines are reworded and `scripts/check-template-toggles.sh` now fails on such lines in `.md`,
  `.sh` and `.yml` files. That script and the `add-toggle` skill (both need `.template.config`) no
  longer ship, which also removes the 13 "Balanced nesting depth" messages every `dotnet new trellis`
  printed. `TenancyTests.cs` no longer ships as an empty file when multi-tenancy is off.
- **`scripts/dotnet.sh` works beyond Docker Desktop**: the SDK container gets
  `--add-host=host.docker.internal:host-gateway`, honours `DOCKER_HOST` (unix socket path or tcp
  endpoint) and `TESTCONTAINERS_DOCKER_SOCKET_OVERRIDE`, and forwards every `TESTCONTAINERS_*`
  variable. `scripts/ef.sh` gets the same host mapping and now forwards the design-time database
  variable it silently dropped. `docs/development.md` covers Colima, rootless Docker, Podman and TCP
  endpoints (only Docker Desktop is exercised here); the dev container maps the host gateway too.
- **`aspire publish` exposes the API**: the `api` resource publishes its HTTP endpoint externally
  (compose `ports:` instead of `expose:`); postgres and migrations stay internal.
- **The Aspire E2E no longer reuses your `aspire run` database**: it strips the postgres data volume
  and runs against a throwaway database, and asserts on the invoice it created rather than on seed
  data.

### Added
- Rich-DDD Clean Architecture .NET 10 solution: SharedKernel, Domain, Application,
  Infrastructure ∥ Persistence, Api (+ Common/Contracts), Blazor UI (+ Common),
  Aspire AppHost + MigrationService.
- Hand-rolled CQRS + Result, EF Core 10 with audit/soft-delete/domain-event
  interceptors, ASP.NET Identity + JWT + permission authorization, HybridCache.
- Full test pyramid (unit, architecture, integration via Testcontainers, E2E via
  Aspire.Hosting.Testing) and CI (format, warnings-as-errors, docs-drift).
- `dotnet new trellis` templatization with toggles: `IncludeUI`, `UseRedis`, `UseSerilog`,
  `UseMultitenancy`, `IncludeUiTests`, `skipRestore`.
- AI-friendly repo: AGENTS.md/CLAUDE.md, `.claude` rules/agents/skills/hooks,
  `.mcp.json`, dual human/AI docs, 19 ADRs.

[Unreleased]: https://github.com/Revazi1st/trellis/commits/main
