# ADR-0022: Tenant on the principal

- **Status**: accepted
- **Date**: 2026-10-07
- **Supersedes**: the tenant-resolution and per-aggregate wiring bullets of ADR-0019 (the toggle,
  the interceptor and the named-filter pattern stand)
- **Amended**: 2026-11-12 — a unique index on an aggregate root is unique per tenant, by the same convention (see *Amendment (2026-11-12)* below)

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

## Amendment (2026-11-12): a unique key is unique per tenant

### Why

Decision 5 gave every aggregate root the `TenantId` column, its index and the filter, and left the
root's own unique indexes global. A feature's unique business key (a customer's e-mail, a code) then
disagreed with its own lookup: the tenant-filtered "is it taken?" query answered free, the global
index answered taken, and the create failed on a row the caller could not see, its 409 telling the
caller that another tenant holds the value.

### Decision

- **The convention loop also rewrites every unique index an aggregate root declares so that
  `TenantId` leads it**, under the database name the index had (the default one included), with its
  annotations kept and any per-column setting (sort order, collation, operator class, null order)
  moved along with its column. A configuration still names no tenant, and `UniqueConstraintErrors`
  and `UniqueConstraintTests.FaultOnPurpose` key on the same name whichever way the toggle is set.
- **Identity's tables are not aggregates and keep their global unique indexes**: one e-mail is one
  user in one tenant (decision 1).
- `TenancyTests` holds both: every unique index and every alternate key on an aggregate root starts
  with `TenantId`, in the real model and in a probe aggregate's (a plain index, one sorted descending on
  every column and one with a pinned name and per-column settings, built by the real `AppDbContext`),
  with a second probe holding a key in each shape the convention leaves global as the check's control;
  and no unique index outside the aggregates includes `TenantId` in any place, a per-tenant index on
  the users being that check's control.

### Consequences

- Two tenants may hold the same business key. A key that must be unique across every tenant (a public
  handle) is a deliberate change to the convention loop, like a shared aggregate.
- A business key is declared as a unique index (`.claude/rules/persistence.md`), and another aggregate
  is referenced by its id: an alternate key, declared with `HasAlternateKey` or made by `HasPrincipalKey`,
  is not rewritten, and `TenancyTests` fails naming it.
- A unique constraint the convention does not reach (a child table's unique index or alternate key, a
  primary key a request chooses) stays unique across every tenant, and its 409, or the 500 of a fault
  on purpose, tells the caller that another tenant holds the value: ids are minted on the server and a
  business key is a unique index on the root, or the threat model's multi-tenancy gap records why a key
  stays global.
- A unique index on exactly a foreign key's columns is the one EF keeps for a one-to-one reference:
  replaced, EF adds its own again, global, under the name with a `1` appended, and `TenancyTests`
  fails naming it. The template's aggregates hold no such reference to each other; adding one means
  teaching the convention loop to leave that index as EF made it.
- The toggle-off model, and so the shipped `InitialCreate`, is unchanged; the regenerated
  multi-tenant `InitialCreate` carries `(TenantId, key)` for each such index.
