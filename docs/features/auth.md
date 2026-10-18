# Feature: Auth

Identity, JWT access tokens, rotating refresh tokens, and permission-based authorization (ADR-0007:
Identity lives in Persistence; the API maps custom endpoints — no `MapIdentityApi`).

## Application (`src/Core/Trellis.Application/Features/Auth/`)

Slices: `Login`, `Register`, `RefreshToken`, `Logout`, `RevokeSessions` — command + `internal sealed`
handler each. `TokenIssuer` mints the access+refresh pair (shared by login/refresh). Ports:
`IIdentityService`, `ITokenProvider`, `IRefreshTokenStore` (`Abstractions/`).

## Behavior

- **Access tokens**: HMAC-SHA256 JWTs carrying `sub`, email and one `permission` claim per effective
  permission (union across roles). Short-lived (`Authentication:AccessTokenMinutes`, default 15).
  The signing key is validated at API startup — boot fails if it is missing or under 32 chars.
  `dotnet new` writes a fresh random Development key into each generated project's
  `appsettings.Development.json` (`dev-only-` + 64 hex characters); every other environment
  supplies its own.
  Validation accepts HS256 only (`ValidAlgorithms`): a token naming another algorithm is rejected.
- **Refresh tokens**: opaque 256-bit values; only their SHA-256 hash is stored (`RefreshTokens`
  table, FK → `AspNetUsers` with cascade delete). **Single-use rotation**: consuming is one atomic
  conditional `UPDATE`, so a concurrent double-spend loses. **Replay detection**: presenting an
  already-consumed token revokes *all* of that user's outstanding tokens (OAuth security BCP) and
  logs a warning. A newly minted token is saved by the login/refresh command's unit of work — the
  store never commits on its own — so a command that fails after minting leaves no orphan row.
  Every refresh failure before minting (an unknown, expired or consumed token, or a user deleted
  since) answers the same 401 `Auth.InvalidRefreshToken`, so the answer never says which
  (`MapOnFailure` ahead of the issuing step in `RefreshTokenCommandHandler`).
- **Account lockout**: 5 failed password attempts lock the account for 15 minutes
  (`Auth.AccountLockedOut` → 403). A successful login resets the counter. This is per-account
  defense; the `auth` rate-limit policy (10/min per user/IP) is the per-source layer.
  Login answers 401 `Auth.InvalidCredentials` for an unknown e-mail and a wrong password alike,
  spending the same password-hash work on both (`DummyPasswordHash`), so neither the answer nor
  its timing tells them apart. A locked account answers 403 `Auth.AccountLockedOut` whatever
  password is supplied (lockout is checked first, so a 403 never confirms a password); that it
  confirms the account exists is an accepted risk (docs/threat-model.md).
- **Registration** is governed by `Registration:Mode` (`RegistrationOptions`, validated at boot):
  `Closed` — the default, and the value in `appsettings.json` — answers 403
  `Auth.RegistrationClosed` and creates nothing; `Open` lets anyone create a Member account.
  `appsettings.Development.json` sets `Open`; anywhere else, opt in with `Registration__Mode=Open`
  only once anyone on the network may read what a Member can. `RegisterCommandHandler` is the one
  place an account is created, so everything that comes with one sits behind this check.
- **Input limits**: validators reject a missing e-mail, password or refresh token, an e-mail over
  254 characters (`EmailAddress.MaxLength`, which also fits Identity's 256-character columns, so an
  accepted address can always be stored), a password over 128 (login and registration alike) and a
  refresh token over 512 (issued tokens are 43) with 400, before Identity, the password hasher or the
  token hash run.
- **Security events** (`SecurityEventLog`, `[LoggerMessage]`, written by `IdentityService`): 1101
  `LoginFailed` (Information, user id), 1102 `LoginFailedUnknownAccount` (Information, masked
  e-mail such as `j***@example.com`), 1103 `AccountLockedOut` (Warning, user id — the counter just
  tripped), 1104 `LoginRejectedLockedOut` (Information, user id), 1105 `FailedSignInNotCounted` and
  1106 `FailedSignInCountNotReset` (Warning, user id and Identity error codes — the lockout counter's
  writes report failure as an `IdentityResult`, never discarded). Never the password, never the
  e-mail as typed.
- **The caller on every signal**: right after authentication, `UserTelemetryMiddleware` (Api.Common)
  puts the signed-in caller's user id in the log scope (`CallerUserId`; not `UserId`, which the security
  events above use for the account an event is about) and on the request span (`enduser.id`), so the
  outcome line of a rejected command names the account, and so does the request span of a 403 from a
  missing permission, which writes no log line at the shipped levels (ASP.NET Core logs the failure at
  Information under `Microsoft.AspNetCore`, held at Warning outside Development). The id only, never the
  e-mail; a request without a valid access token (a sign-in, a registration) carries neither.
  `docs/deployment.md` lists both in its *Telemetry reference*.
- **Logout** revokes the presented refresh token (idempotent, anonymous — possession of the token is
  the authorization; never trips replay detection). **Revoke-all** signs the caller out everywhere.
- **Tenant (multi-tenancy toggle)**: every user has exactly one `TenantId` (`AspNetUsers`, required,
  indexed), minted into the access token as the `tenant` claim (`ITenantContext.ClaimType`); the API
  trusts only that claim — no header — and rejects a token without it (401, `invalid_token`).
  Self-registration provisions a fresh tenant (a GUID) for the registrant, where `Registration:Mode`
  allows registration at all; the seeded admin and the Development demo data belong to
  `Seed:TenantId` (`default`). ADR-0022.
- **Seeding**: the MigrationService seeds roles/permissions always, the admin user always, and demo
  invoices only in Development. The local-dev admin is `admin@example.com` / `Admin123!`
  (`Seed__AdminEmail` / `Seed__AdminPassword`; with multi-tenancy, `Seed__TenantId` names the
  admin's — and the demo data's — tenant, default `default`). Outside Development it refuses to
  seed the well-known default admin password — set `Seed__AdminPassword`. The `Seed` section is
  validated at boot, and every Identity call in the seed (`IdentitySeed`, in Persistence) is
  checked: a password Identity rejects fails the run (exit 1) with every Identity error named. A
  rerun converges — missing roles and permission claims are added, an admin without its role gets
  it back, an existing admin's password is left alone.
- **Registration is one transaction**: the account and its Member role are created together inside
  the execution strategy's transaction. If the role cannot be assigned the account is rolled back —
  a reported failure answers 500 `Auth.RegistrationFailed`, nothing is created, and the e-mail can
  register again (no role-less account that signs in with no permissions). Two registrations of one
  e-mail racing each other are decided by the unique user-name index: the loser gets the same 409
  `Auth.EmailAlreadyInUse` as a sequential duplicate (ADR-0018, amendment of 2026-10-11). A password or
  e-mail Identity refuses answers 400 `Auth.RegistrationRejected` with one fixed sentence per Identity
  error code — never Identity's own text, which repeats the e-mail.

## API (`src/Presentation/Api/Trellis.Api/Features/Auth/`)

| Method | Route | Auth |
|---|---|---|
| POST | `/api/v1/auth/register` | anonymous, `auth` rate limit, `Registration:Mode` |
| POST | `/api/v1/auth/login` | anonymous, `auth` rate limit |
| POST | `/api/v1/auth/refresh` | anonymous, global rate limit |
| POST | `/api/v1/auth/logout` | anonymous, global rate limit |
| POST | `/api/v1/auth/revoke-all` | authenticated |

The strict `auth` policy guards the endpoints that take a guessable credential (register, login);
refresh and logout take an unguessable token and sit under the global per-client limiter only, so a
shared source such as the Blazor Server host never turns silent refreshes into 429s (ADR-0021).
Every other endpoint on the versioned group requires authentication by default; these four are the
whole anonymous allowlist (`EndpointMetadataTests`). Each remaining endpoint but `revoke-all` requires
exactly one permission, pinned row by row in `EndpointMetadataTests.ExpectedPermissions` (a write
declared with `invoices:read` fails the build), and proved on the wire with clients that
`ApiFactory.CreateClientWithPermissions(...)` mints through the host's own `ITokenProvider`: the one
permission gets the endpoint's own answer, every other permission a 403. In the OpenAPI document each operation's
`operationId` is its endpoint class (`Register`, `Login`, `Refresh`, `Logout`, `RevokeSessions`), the
400/429/500 problem responses (401/403 on `revoke-all`) are declared by shape, and the Result statuses
an anonymous operation's shape cannot imply are declared on the endpoint with a bare `.Produces(status)`:
login 401/403, register 403/409, refresh 401 — each with the `ProblemResponse` body (ADR-0025,
`OpenApiDocumentTests.Anonymous_auth_operations_declare_their_result_statuses`).

Contracts in `Api.Contracts/Auth/AuthContracts.cs`. Errors flow as RFC 9457 problem details whose
`code` is the identifier below (ADR-0024): `Auth.InvalidCredentials` 401, `Auth.AccountLockedOut` 403,
`Auth.InvalidRefreshToken` 401, `Auth.EmailAlreadyInUse` 409, `Auth.RegistrationClosed` 403,
`Auth.RegistrationRejected` 400, `Auth.RegistrationFailed` 500, and 400
`Validation.General` with `errors[]` (`{field, code, description}`, e.g. `email` / `Validation.NotEmpty`)
for input that fails a validator. The host's own rejections use the same vocabulary:
`Auth.Unauthenticated` (the bearer challenge, 401), `Auth.Forbidden` (403), `RateLimit.Exceeded` (429).

## Protecting an endpoint

Every endpoint on the versioned group already requires a signed-in caller; what you add says who else is
let through. `reports:read` below is a placeholder for a permission of yours: a constant in
`Permissions`, listed in `Permissions.All` and granted to a role in `Roles.Permissions` (the seeding and
the test hosts read that map), with its row in `EndpointMetadataTests.ExpectedPermissions`.

1. **One permission, the default.** `.RequirePermission(Permissions.Reports.Read)` admits a caller whose
   token carries `reports:read`. No token, or one that fails validation: **401** `Auth.Unauthenticated`
   with `WWW-Authenticate: Bearer` (`error="invalid_token"` when a token was presented). A valid token
   without the permission: **403** `Auth.Forbidden`.
2. **Signed in is the whole requirement**, for an action on the caller's own account (`revoke-all`): no
   permission call, and a row in `EndpointMetadataTests.AuthenticatedOnly`, because the test otherwise
   reads a missing `RequirePermission` as a mistake. Rejected: the same 401.
3. **Anonymous**, for an endpoint that must work without a token: `.AllowAnonymous()` and a row in
   `EndpointMetadataTests.AnonymousAllowlist`, reviewed together. Nobody is refused by authorization; an
   endpoint that takes a guessable credential adds
   `.RequireRateLimiting(ApiHardeningExtensions.AuthRateLimitPolicy)`, and a source past its ten
   attempts a minute gets **429** `RateLimit.Exceeded` with `Retry-After`.
4. **A permission plus a further condition** (office hours, a claim from another system): register a
   named policy in the API host
   (`builder.Services.AddAuthorizationBuilder().AddPolicy("ReportsDuringOfficeHours", …)`) and stack it
   beside the permission, `.RequirePermission(Permissions.Reports.Read).RequireAuthorization("ReportsDuringOfficeHours")`.
   Both must pass; a caller who fails either gets **403** `Auth.Forbidden`. A named policy never stands in
   for the permission: `EndpointMetadataTests` wants exactly one catalogued permission per endpoint, and a
   name outside `Permissions.All` is an ordinary policy, not a permission any role is granted.
5. **This record, not only this kind of record.** A permission says what a caller may do to reports, not
   to which report; a per-record rule is the recipe below, and a caller who fails it gets **404**, the
   same answer as for a record that does not exist.

### Ownership: a record only its owner may see

Permissions are per endpoint; a rule per record (the owner, a team) is the feature's own, and it is
what OWASP lists first among API risks when it is missing.

- **The owner is a domain fact.** The aggregate stores an `OwnerId`, the `ICurrentUser.UserId` the
  handler passes to `Create`. Never authorize on the audit `CreatedBy`: it is bookkeeping text stamped by
  Persistence (`AuditingInterceptor`), the acting user's id for a signed-in caller but a name for one
  without an id (`system` for the migration job), and the domain never sees it.
- **Enforce it where rows are found.** A specification (`ReportSpecs.OwnedBy(ownerId, id)`) or the
  repository's load filters by owner, so a non-owner's read and write both find nothing and answer the
  aggregate's own `NotFound`, exactly as the `"SoftDeleted"` filter hides a deleted invoice. Answer 403 instead
  only where the record's existence is not itself sensitive.
- **Mind the read cache.** The cache-aside endpoint (`GetInvoice` is the model) answers a hit without
  running the handler, so an owner check inside the handler never runs on a hit. An owner-scoped read
  puts the owner in its `CacheKeys` key (and its writes evict that key) or is not cached at all.
- **Seeing everything is its own endpoint**, with its own permission and an unscoped specification, not a
  flag on the owner-scoped one.
- **Test it with two users.** Two clients from `ApiFactory.CreateClientWithPermissions(...)` are two
  users: the second reads and writes the first's record and gets 404, before and after the record is
  cached.

Invoices are deliberately not owner-scoped: an invoice belongs to the business, every Member may read it,
and the reference slice keeps it that way.

## Adding an external identity provider

The API signs and checks its own tokens (ADR-0007). Putting an OpenID Connect provider (Entra ID,
Keycloak, Auth0) in front of it, beside the local sign-in or instead of it, is not one more
`AddJwtBearer`; a second bearer scheme breaks in these places, so go through them all:

- **Pick the scheme by issuer.** The permission policies name no authentication scheme
  (`PermissionPolicyProvider` builds them from `RequireAuthenticatedUser` and the permission alone), so
  they authenticate with the default scheme. Register the provider's scheme
  (`AddJwtBearer("External", …)` with its `Authority` and `Audience`) and make the default a policy scheme
  (`AddPolicyScheme`) whose `ForwardDefaultSelector` reads the token's `iss` and forwards to the local
  `JwtBearerDefaults.AuthenticationScheme` or to `External`.
- **Give every scheme the same events.** `AddApiAuthentication` wires the problem-details challenge
  (`OnChallenge`, which also writes `WWW-Authenticate`) and the forbidden body (`OnForbidden`) on the
  local scheme only; without them the external scheme's rejections reach clients as the status-code
  pages' generic `Http.Unauthorized` and `Http.Forbidden` problems, not `Auth.Unauthenticated` and
  `Auth.Forbidden`.
<!--#if (UseMultitenancy) -->
- **Keep the tenant check on the new scheme.** With multi-tenancy the local scheme's `OnTokenValidated`
  fails a token without a `tenant` claim (ADR-0022); the external scheme needs it too, or such a token
  reads system-scoped rows instead of being refused.
<!--#endif -->
- **Do not carry the HS256 pin over.** `ValidAlgorithms = [SecurityAlgorithms.HmacSha256]` describes the
  tokens this API mints. The external scheme validates against the provider's published keys with
  `ValidAlgorithms` set to its asymmetric algorithm (RS256, ES256), never a symmetric one.
- **Translate the provider's claims into this API's.** Turn off `MapInboundClaims` on that scheme and add
  an `IClaimsTransformation` that finds the local user for the token's `iss` and `sub` and adds a
  `ClaimTypes.NameIdentifier` holding that user's `Guid`, plus one `permission` claim per permission
  (from the provider's roles or the local user's). `CurrentUser` reads `NameIdentifier` as a `Guid`; a
  provider's `sub` is rarely one, and without the mapping `ICurrentUser.UserId` is null for every external
  caller, which breaks ownership, puts them all in the idempotency filter's single `anonymous`
  partition, stamps the audit columns with the token's name claim (nothing, without one) instead of a
  user id, and leaves their log lines and request spans without `CallerUserId` and `enduser.id`.
- **Leave out what the provider owns.** Registration, the lockout and refresh tokens belong to the local
  sign-in; an external user gets none of them.
<!--#if (IncludeUI) -->
- **UI.Web needs a sign-in of its own.** The Blazor pages post a password to `/auth/login`; an external
  user signs in through an OpenID Connect authorization-code flow or a backend-for-frontend (ADR-0015),
  and the OIDC handler is a new package: ask first.
<!--#endif -->
- **Test the scheme without the provider**: sign an RS256 token with a key the test generates, point the
  external scheme's issuer and signing key at it, and assert a permission policy's 200 and 403 and the
  401 problem body.

## UI

`Login.razor` signs in and `Register.razor` creates a Member account via `IAuthApiClient`;
`TokenAuthenticationStateProvider` (UI.Common) checks `exp` and transparently rotates through
`/auth/refresh` (single-flight, 30 s before expiry) so the session outlives the access token. Only a
401 from refresh signs the circuit out; a 429, a 5xx or an unreachable API keeps the stored pair,
holds off the next attempt for 5 s and keeps the user signed in (ADR-0021).

Signing out forgets the pair at once, then revokes the refresh token with `POST /auth/logout`
(`IAuthApiClient.LogoutAsync`, best-effort: if the API cannot be reached the token simply expires);
UI.Web also revokes it when a circuit closes (`RevokeSessionOnCircuitClose`), since a closed circuit
takes its tokens with it. An open protected page reacts to the session ending — sign-out, or a
rejected refresh — by going to `login?returnUrl=<the page>`, and the sign-in page returns there
afterwards; only a plain relative path inside the app is honoured (`LoginRedirect`), so the query
string cannot be used as an open redirect.

The clients show what the API said rather than a guess: the lockout 403, a password-policy 400 and
the register 409 display the problem's `detail`, a 429 says how long to wait (`Retry-After`), a 5xx
or an unreachable API reads "The service is unavailable right now", and a wrong password reads as
the server's invalid-credentials sentence. A call that gets no usable answer — no response, a
timeout, an open circuit, an unreadable body — is the service-unavailable sentence too, never an
exception (`ApiCall`). The seeded-admin hint on the sign-in page renders only when the host enables
`UiOptions.ShowDevelopmentHints` (UI.Web: in Development).

Auth rejections are RFC 9457 everywhere: JwtBearer challenges (401) and forbidden (403) responses
carry problem-details bodies, and rate-limited requests get a 429 problem with `Retry-After` — all
written by `IProblemDetailsService` (`ProblemCatalog` + `WriteProblemAsync`), so they carry `code`,
`type`, `traceId` and `correlationId` like every other problem (ADR-0024).
Every 401 carries `WWW-Authenticate: Bearer`: the challenge's (`error="invalid_token"` when a presented
token was rejected) and the ones a Result produces, a wrong password or a rejected refresh token (the
problem-details enrichment adds it). Every `/auth/` response is `Cache-Control: no-store`, whatever the
endpoint sets, as is every other `/api/` response unless its endpoint chose its own (ADR-0021 amendment).

## Tests

`Application.UnitTests/Auth` (login/refresh/register/logout/revoke-all handlers with faked ports,
the validators, and the convention that every command with string input has a validator) ·
`IntegrationTests/AuthSecurityTests` (lockout thresholds + reset, replay revocation, revoke
idempotency — real Identity + PostgreSQL) · `ApiTests/InProcess/AuthRateLimitTests` (login 429 with
`Retry-After`, refresh chains and twelve sessions through one host never throttled) ·
`EndpointMetadataTests` (anonymous allowlist, each endpoint's one permission and its wire-level 403,
challenge header, algorithm pin) ·
`ApiTests/InProcess/AuthFlowTests` (open and closed registration, an unknown mode failing the
boot; rotation, replay killing the family, logout twice, revoke-all; lockout 4×401 → 403 → 403
with its security events; equal hash work for an unknown e-mail; refresh-token expiry on an
adjustable clock; 400 for missing fields) ·
`ApiTests/Ui` (client-side: failure sentences, transport failures as results, refresh tolerance and
hold-off, bearer attachment, sign-out revoking the refresh token, the return-URL guard, protected
pages leaving when the session ends, the development-only sign-in hint; fake handlers, no browser) ·
`ApiTests/InProcess/TenantIsolationTests` (multi-tenancy toggle, all-options leg: the token carries
the tenant claim, a token without it is 401, `X-Tenant-Id` never overrides the claim).

> When you change this feature's behavior, update this file, and docs/threat-model.md when the
> change is security-relevant (the CI docs-drift check enforces both for auth code).
