# Changelog

All notable changes to XTemplate are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); the project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Reliability & testing
- **Outbox, production-shaped**: skip-locked batch claiming (replicas never double-dispatch),
  exponential backoff (30s→4m instead of five retries in 25 seconds), explicit
  Pending/Processed/DeadLettered status with a critical log + `xtemplate.outbox.dead_lettered`
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
- **`dotnet new xtemplate` hung forever on non-TTY hosts** — the chmod post-action prompts, and
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
  `.template.config/`, so `dotnet new install XTemplate.Templates` could not work at all.
  Packing now preserves the tree via `PackagePath="content\%(RecursiveDir)…"`.
- **Project names containing a dot produced a solution that did not compile.** `AddXTemplateUi`
  became `AddAcme.ShopUi`, and Aspire's `Projects.XTemplate_Api` became `Projects.Acme.Shop_Api`.
  The DI extension is renamed `AddSharedUi` (name-independent, like every sibling), and derived
  symbols now map the name to Aspire's `_`/`-` forms for project classes and resource names.
- `.template.config/` no longer ships into generated projects.
- `scripts/check-adr-links.sh` is now made executable by the post-action; CI runs it.

### Added
- Rich-DDD Clean Architecture .NET 10 solution: SharedKernel, Domain, Application,
  Infrastructure ∥ Persistence, Api (+ Common/Contracts), Blazor UI (+ Common),
  Aspire AppHost + MigrationService.
- Hand-rolled CQRS + Result, EF Core 10 with audit/soft-delete/domain-event
  interceptors, ASP.NET Identity + JWT + permission authorization, HybridCache.
- Full test pyramid (unit, architecture, integration via Testcontainers, E2E via
  Aspire.Hosting.Testing) and CI (format, warnings-as-errors, docs-drift).
- `dotnet new xtemplate` templatization with toggles: `IncludeUI`, `UseRedis`,
  `UseSerilog`, `skipRestore`.
- AI-friendly repo: AGENTS.md/CLAUDE.md, `.claude` rules/agents/skills/hooks,
  `.mcp.json`, dual human/AI docs, 17 ADRs.

[Unreleased]: https://github.com/your-org/xtemplate/commits/main
