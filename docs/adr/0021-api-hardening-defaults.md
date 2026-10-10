# ADR-0021: API hardening defaults

- **Status**: accepted
- **Date**: 2026-10-07

## Context

The API host shipped with sound pieces (JWT bearer, permission policies, two rate limiters, CORS,
forwarded-header support) that were each opt-in or quietly bypassable:

- The versioned `/api/v{version}` group required nothing. Every endpoint had to remember
  `.RequirePermission(...)`; a copied slice that forgot shipped public, while the OpenAPI transformer
  documented it as bearer-secured because it keyed on the *absence* of `AllowAnonymous`.
- Aspire's publishers set `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true`. The WebHost then clears both
  trusted-proxy lists before `AddApiHardening` adds configured entries, and `ForwardedHeadersMiddleware`
  skips the proxy check when both lists are empty: published output honoured `X-Forwarded-For` from
  anyone, so a caller could pick its own rate-limit partition (unlimited login attempts). The API also
  ran the middleware a second time on top of the WebHost's own pass.
- A Blazor Server host relays every user's calls from one socket. With `/auth/refresh` under the strict
  10/min `auth` policy, roughly ten active sessions turned silent refreshes into 429s.
- `/health` and `/alive` shared the global limiter, so probes could be answered 429 under load.
- The custom 401 challenge dropped `WWW-Authenticate`; any HMAC algorithm the token header named was
  accepted; no security headers; token responses were cacheable; Kestrel's ~28.6 MB body default
  applied to a JSON API whose largest request is under a kilobyte.
- `PermissionPolicyProvider` turned *any* policy name into a permission requirement, so a typo became a
  permission nobody holds: a silent, undiagnosable 403.
- Every in-process test client shared one anonymous rate-limit partition (TestServer reports no remote
  address), so fixtures drained a common 10/min auth bucket.

## Decision

**1. Deny-by-default at the API boundary.** `MapVersionedApiGroup` calls `.RequireAuthorization()`.
There is **no fallback policy**: probes and API docs are mapped on the host, outside the group, by
construction; a fallback policy would need exemptions for them and would silently govern whatever a
consumer maps on the host later. Endpoints that must be reachable without a token call
`.AllowAnonymous()` (which wins over group metadata) and are enumerated in
`EndpointMetadataTests.AnonymousAllowlist`: `auth/register`, `auth/login`, `auth/refresh`,
`auth/logout`. The OpenAPI security transformer keys on "has `IAuthorizeData` and no `IAllowAnonymous`".
`EndpointMetadataTests` reads the routing table and fails the build when an endpoint is neither
anonymous nor authorized, when the anonymous set differs from the allowlist, when a non-anonymous
endpoint lacks a catalogued permission (only `auth/revoke-all` is authenticated-only), when a non-GET
endpoint outside `auth/` lacks `RequireIdempotency()`, or when an endpoint lacks a summary or a
description. Group-level authentication alone would mask a forgotten `RequirePermission` as "signed in
is enough"; the test is what catches it. This is ADR-0014's layering applied to security conventions:
the rule lives in `.claude/rules/api.md`, the guarantee lives in a test.

**2. Forwarded-header trust never widens.** `X-Forwarded-For`/`-Proto` are honoured only from
`ForwardedHeaders:KnownProxies` / `ForwardedHeaders:KnownNetworks`. A `PostConfigure` restores
loopback-only trust (`::1`, `127.0.0.1`) whenever both lists end up empty, which is exactly the
`ASPNETCORE_FORWARDEDHEADERS_ENABLED` path, and `UseApiHardening` logs one startup warning naming
`ForwardedHeaders__KnownNetworks__0` / `ForwardedHeaders__KnownProxies__0`. It never fails the boot
(publish output must still start) and never degrades to trust-everyone. When the WebHost's startup
filter already runs the middleware, `UseApiHardening` does not run it again: a second pass would
consume one more, possibly client-written, `X-Forwarded-For` entry. Application hosts (UI.Web) do not
forward client addresses to the API, and the template never adds them to `KnownNetworks`.

**3. Rate limits follow what the endpoint accepts.** The `auth` policy (10/min per user, else per
client address) guards endpoints that accept a **guessable** credential: `/auth/login`,
`/auth/register`. Endpoints that accept an **unguessable** token (`/auth/refresh`, `/auth/logout`) sit
under the global limiter only (100/min per source): throttling a 256-bit random value per source buys
nothing, and replay already revokes the token family. `/health` and `/alive` are exempt from rate
limiting (`.DisableRateLimiting()`): a throttled probe reads as an unhealthy instance. They are not
`.ShortCircuit()`ed, because routing runs implicitly at the front of the pipeline and short-circuiting
would also skip the correlation-id and exception-handling middleware.

**The per-web-host login ceiling is deliberate.** A Blazor Server host signs every user in from one
address, so it gets 10 logins per minute in total. That is the credential-guessing control working as
designed, not a defect to tune: the ceiling stays fixed (it is a security control, not a tunable).
Refreshes are no longer affected (rule 3), and the UI treats only a 401 on refresh as the end of a
session. A deployment that needs more has two paths: configure the web host's network in
`KnownNetworks` **and** add a forwarding handler to the UI's API client yourself (a deliberate,
per-environment widening of trust), or move to the BFF shape ADR-0007 names as the production path.

**4. RFC 6750 challenge.** The 401 problem response carries `WWW-Authenticate: Bearer`, or
`Bearer error="invalid_token"` when a presented token was rejected, so clients can tell "refresh" from
"sign in".

**5. One signing algorithm.** `TokenValidationParameters.ValidAlgorithms = [HS256]`, the only
algorithm `JwtTokenProvider` mints. A token naming any other algorithm is rejected, even when signed
with the right key.

**6. Security headers.** `SecurityHeadersMiddleware` (in `UseApiHardening`) sets
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY` and
`Content-Security-Policy: frame-ancestors 'none'` on every response, including problem documents, and
`Cache-Control: no-store` on every `/api/{version}/auth/` response. The CSP is limited to
`frame-ancestors` so the development-only Scalar page keeps working.

**7. Request-size limits.** `AddApiHardening` sets Kestrel's `MaxRequestBodySize` to 1 MB and
`MaxRequestHeadersTotalSize` to 32 KB, configurable through Kestrel's own keys
(`Kestrel:Limits:MaxRequestBodySize`, `Kestrel:Limits:MaxRequestHeadersTotalSize`). A larger body is
answered 413.

**8. Permissions are a closed catalogue.** `PermissionPolicyProvider` synthesizes policies only for
names in `Permissions.All` (built once, `AllowsCachingPolicies = true`); any other name falls back to
the default provider, so ordinary named policies work and a typo is an unknown policy. ADR-0007's
"policy per permission on demand" now means "per *catalogued* permission".
`RequirePermission` throws at startup for a name outside the catalogue.

**9. Each test client is its own rate-limit partition.** `ApiFactory` gives every client a distinct
simulated remote address through an outermost test startup filter, so fixtures stop sharing the auth
bucket and tests can model "many users behind one host" explicitly (`CreateClientFrom`).

## Consequences

- Forgetting authorization on a new endpoint is a red build, not a public endpoint. Adding an anonymous
  endpoint is a reviewed two-line change (the endpoint and the allowlist entry).
- The deny-by-default group means every non-anonymous endpoint runs with an authenticated principal,
  which is what lets per-request context (the tenant, ADR-0022) come from the principal.
- Security events name an account by user id; a failed sign-in for an address that matches no account
  has no user id, so event 1102 logs the masked e-mail (first character plus domain, `j***@example.com`),
  enough to spot a run against one domain, and never the full address or the password.
- `aspire publish` output boots safely with no proxy configured; it rate-limits the ingress address
  until the operator sets `ForwardedHeaders__KnownNetworks__0`, and the startup log says so.
- `/auth/refresh` and `/auth/logout` responses can no longer be 429 because of other users' logins.
- Requests over 1 MB are refused at the server. A feature that legitimately uploads more raises the
  limit for its endpoint (`[RequestSizeLimit]` / `IHttpMaxRequestBodySizeFeature`), not globally.
- A new permission is a new constant in `Permissions` (and a role grant); a misspelt one stops the boot.
- The committed OpenAPI document does not change for the shipped endpoints (ADR-0013): the transformer
  reasons from declarations now, and the declarations already matched.
