# ADR-0022: Tenant on the principal

- **Status**: accepted
- **Date**: 2026-10-07
- **Supersedes**: the tenant-resolution and per-aggregate wiring bullets of ADR-0019 (the toggle,
  the interceptor and the named-filter pattern stand)

## Context

ADR-0019 shipped the multi-tenancy toggle with a resolver that read the principal's `tenant` claim
first and fell back to an `X-Tenant-Id` request header. Nothing ever minted the claim:
`ApplicationUser` had no tenant, `AuthenticatedUser` carried none and `JwtTokenProvider` emitted
`sub`, `email`, `jti` and `permission` only. The claim branch was dead code and the header was the
selector, so any authenticated caller chose its tenant per request, and the `"Tenant"` query
filter, the `TenantInterceptor` stamp and the tenant-prefixed cache keys all faithfully scoped to
*that* choice. The only tests drove a hand-rolled `ITenantContext`, so the HTTP resolver was never
exercised.

Two smaller problems rode along. The shadow column, its index and the filter were hand-wired per
aggregate (`AppDbContext.OnModelCreating` for the filter, `InvoiceConfiguration` for the column),
so the second aggregate a consumer added shipped unisolated unless they remembered both places, and
no fitness test would have noticed.

Constraints: Result over exceptions; Infrastructure and Persistence stay peers (the tenant flows
Persistence → `AuthenticatedUser` (Application) → Infrastructure's token provider, exactly as the
permissions already do); no new package or project; the toggle-off output is unchanged in
behaviour; the shipped `InitialCreate` migration is generated for the toggle-off shape and stays so.

## Decision

1. **Tenant membership lives on the user.** `ApplicationUser.TenantId` is a required, indexed
   string (max 64; opaque — the template never parses it). One user, one tenant. A membership table
   with a tenant switcher is an additive retrofit later; the claim, resolver and filter would not
   change. Identity tables stay **global** (no tenant filter): login, lockout and refresh run before
   a tenant is known, and `RequireUniqueEmail` already means one e-mail → one user → one tenant.
2. **The tenant reaches the token through the existing principal record.** `AuthenticatedUser`
   gains `TenantId`; `IdentityService` fills it from the user row; `JwtTokenProvider` mints it as
   the claim named by `ITenantContext.ClaimType` (`"tenant"`), the single constant for the name.
3. **Resolution is claim-only; the header is gone.** `TenantContext` resolves `AmbientTenant`
   (outbox dispatch restoring the originating tenant) → the `tenant` claim → `null`. No anonymous
   endpoint touches tenant-scoped data, so a header branch has no legitimate consumer and is a trap
   for the next public endpoint someone adds.
4. **Fail closed.** `JwtBearerEvents.OnTokenValidated` rejects a token that validates but carries no
   tenant claim (a token minted before the toggle was enabled, or an external IdP that does not emit
   it); the existing challenge writes the RFC 9457 401. Letting such a principal fall through to
   `null` would be a silent read of system-scoped rows — the class of bug being fixed.
5. **Scoping is a convention over every aggregate root.** `AppDbContext.OnModelCreating` loops over
   the entity types whose CLR type implements `IHasDomainEvents` (what `AggregateRoot<>` implements
   and what `OutboxWriteInterceptor` already keys on) and gives each the `TenantId` shadow column,
   its index and the named `"Tenant"` filter, built as an expression whose constant is the context
   instance so EF parameterises the current tenant per query. `InvoiceConfiguration` contains no
   tenant code. An opt-in marker was rejected: forgetting it recreates "second aggregate ships
   unisolated". A deliberately shared aggregate is a future, explicit opt-out made in one place.
6. **Self-registration provisions a new tenant** (`Guid.CreateVersion7()`), inside
   `IdentityService.RegisterAsync`, behind the `Registration:Mode` check in `RegisterCommandHandler`
   (closed outside Development by default). A caller-supplied tenant at registration would be the
   header hole again; invitation flows are out of template scope.
7. **The seeded admin and the demo data belong to `Seed:TenantId`** (default `default`). The
   migration worker seeds the demo invoices under `AmbientTenant` = that tenant, so the admin sees
   them and the idempotent "already seeded" probe sees them too. Roles and permissions stay global.
8. **The migration story is unchanged.** The repository ships `InitialCreate` for the toggle-off
   model, which this decision does not touch; a project generated with `--use-multitenancy`
   regenerates it (`migrations remove --force` + `add InitialCreate`), as the template
   repository's all-options CI leg and `docs/development.md` already do. The regenerated migration additionally
   carries `AspNetUsers.TenantId` + index next to the aggregate and outbox columns.

Proof lives in `TenantIsolationTests` (ApiTests, all-options leg: cross-tenant read and write 404,
overdue listing scoped, `X-Tenant-Id` ignored on reads and writes, a cache warmed by one tenant
never serving another, the token carrying the claim, a token without it rejected 401 while its
twin with the claim is accepted) and `TenancyTests` (IntegrationTests: model fitness for every
aggregate root and for the tables that must stay unfiltered, registration provisioning distinct
tenants, outbox rows carrying the tenant and handlers running under it).

## Consequences

- A new aggregate is tenant-isolated the moment it derives from `AggregateRoot<>`; the fitness
  test fails if that convention ever regresses. Never add tenant columns or filters in a
  configuration (`.claude/rules/persistence.md`).
- Moving a user between tenants takes effect at the next token refresh (≤ `AccessTokenMinutes`).
- `IHttpContextAccessor` is an AsyncLocal and HybridCache runs `GetOrCreateAsync` factories without
  the caller's ExecutionContext, so a handler asking for the tenant inside a cache factory sees no
  request. `TenantContext` therefore reads the principal live whenever the request is visible and
  remembers it for the scope; a detached flow gets what the request flow last resolved (the cache
  key, computed before the factory). Any other request-scoped service that reads the accessor
  inside a cache factory (`CurrentUser`) has the same blind spot; keep writes, and anything keyed
  on the principal, out of cache factories.
- Replacing `JwtTokenProvider` with an external identity provider requires that provider to emit
  the `tenant` claim, or every call is 401 — by design.
- A cross-tenant administrative view must opt out per query with `IgnoreQueryFilters(["Tenant"])`;
  the bare `IgnoreQueryFilters()` drops the tenant filter together with `"SoftDeleted"` silently (a
  source-scan guard is a separate decision).
- A deliberately shared aggregate needs an explicit opt-out that does not exist yet; adding one is a
  deliberate change to the convention loop, not a per-aggregate flag.
- Enabling the toggle on a project that already has tenant-less users requires a data migration
  that assigns each user a `TenantId` before the regenerated schema's `NOT NULL` applies; the
  template's regenerate-`InitialCreate` story assumes a fresh database.
- The outbox processor restores the originating tenant around each message's handlers (as before);
  a handler that *writes* under the tenant still depends on the per-message commit fix tracked with
  the outbox work.
