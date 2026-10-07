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
  Validation accepts HS256 only (`ValidAlgorithms`): a token naming another algorithm is rejected.
- **Refresh tokens**: opaque 256-bit values; only their SHA-256 hash is stored (`RefreshTokens`
  table, FK → `AspNetUsers` with cascade delete). **Single-use rotation**: consuming is one atomic
  conditional `UPDATE`, so a concurrent double-spend loses. **Replay detection**: presenting an
  already-consumed token revokes *all* of that user's outstanding tokens (OAuth security BCP) and
  logs a warning.
- **Account lockout**: 5 failed password attempts lock the account for 15 minutes
  (`Auth.AccountLockedOut` → 403). A successful login resets the counter. This is per-account
  defense; the `auth` rate-limit policy (10/min per user/IP) is the per-source layer.
- **Logout** revokes the presented refresh token (idempotent, anonymous — possession of the token is
  the authorization; never trips replay detection). **Revoke-all** signs the caller out everywhere.
- **Seeding**: the MigrationService seeds roles/permissions always, the admin user always, and demo
  invoices only in Development. The local-dev admin is `admin@example.com` / `Admin123!`
  (`Seed__AdminEmail` / `Seed__AdminPassword`). Outside Development it refuses to seed the
  well-known default admin password — set `Seed__AdminPassword`.

## API (`src/Presentation/Api/Trellis.Api/Features/Auth/`)

| Method | Route | Auth |
|---|---|---|
| POST | `/api/v1/auth/register` | anonymous, `auth` rate limit |
| POST | `/api/v1/auth/login` | anonymous, `auth` rate limit |
| POST | `/api/v1/auth/refresh` | anonymous, global rate limit |
| POST | `/api/v1/auth/logout` | anonymous, global rate limit |
| POST | `/api/v1/auth/revoke-all` | authenticated |

The strict `auth` policy guards the endpoints that take a guessable credential (register, login);
refresh and logout take an unguessable token and sit under the global per-client limiter only, so a
shared source such as the Blazor Server host never turns silent refreshes into 429s (ADR-0021).
Every other endpoint on the versioned group requires authentication by default; these four are the
whole anonymous allowlist (`EndpointMetadataTests`).

Contracts in `Api.Contracts/Auth/AuthContracts.cs`. Errors flow as RFC 9457 problem details
(`Auth.InvalidCredentials` 401, `Auth.AccountLockedOut` 403, `Auth.InvalidRefreshToken` 401,
`Auth.EmailAlreadyInUse` 409).

## UI

`Login.razor` signs in and `Register.razor` creates a Member account via `IAuthApiClient`;
`TokenAuthenticationStateProvider` (UI.Common) checks `exp` and transparently rotates through
`/auth/refresh` (single-flight) so the session outlives the access token; a failed rotation signs
the circuit out.

Auth rejections are RFC 9457 everywhere: JwtBearer challenges (401) and forbidden (403) responses
carry problem-details bodies, and rate-limited requests get a 429 problem with `Retry-After`.
A 401 also carries `WWW-Authenticate: Bearer` (`error="invalid_token"` when a presented token was
rejected), and every `/auth/` response is `Cache-Control: no-store`.

## Tests

`Application.UnitTests/Auth` (login/refresh handler logic with faked ports) ·
`IntegrationTests/AuthSecurityTests` (lockout thresholds + reset, replay revocation, revoke
idempotency — real Identity + PostgreSQL) · `ApiTests/InProcess/AuthRateLimitTests` (login 429 with
`Retry-After`, refresh chains and twelve sessions through one host never throttled) ·
`EndpointMetadataTests` (anonymous allowlist, permissions, challenge header, algorithm pin).

> When you change this feature's behavior, update this file (the CI docs-drift check enforces it).
