# ADR-0007: Identity in Persistence, custom JWT auth endpoints, permission-based authorization

- **Status**: accepted
- **Date**: 2026-07-15
- **Amended**: 2026-10-08 — account lockout, and why it stays visible (see *Amendment* below)
- **Amended**: 2026-10-11 — adding or swapping the token issuer is not a contained change (see *Amendment (2026-10-11)* below)
- **Amended**: 2026-10-18 — a missing user id never broke auditing; what it does to the audit columns now (see *Amendment (2026-10-18)* below)

## Context

The template needs authentication and authorization that is self-contained (no external IdP dependency), works across the API and both UI heads, and stays legible. Two sub-decisions: where Identity lives given the Infrastructure ∥ Persistence split (ADR-0002), and whether to use ASP.NET Core Identity's built-in `MapIdentityApi` endpoints.

## Decision

**Identity lives in Persistence.** `ApplicationUser : IdentityUser<Guid>`, `AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`, the `IIdentityService` implementation, and the rotating `RefreshToken` store all sit in `Trellis.Persistence/Identity`. Identity is a persistence-backed subsystem (it owns tables and queries), so this keeps Infrastructure and Persistence cleanly parallel — the strongest justification for the split.

**Token *minting* lives in Infrastructure.** `JwtTokenProvider` (`ITokenProvider`) turns an `AuthenticatedUser` into a signed JWT and opaque refresh token — pure token concerns, no persistence — matching the peer arrangement.

**Custom auth endpoints, not `MapIdentityApi`.** `Features/Auth` has hand-written register/login/refresh use cases. `MapIdentityApi` issues opaque bearer tokens with no room for permission claims and hides the flow; our JWTs embed the user's permissions as `permission` claims so authorization is a local, stateless check.

**Permission-based authorization.** Endpoints declare `RequirePermission(Permissions.Invoices.Write)`. A `PermissionPolicyProvider` materializes a policy per permission on demand (no up-front registration); a `PermissionAuthorizationHandler` checks the claim. Permissions are granted to roles as claims and seeded by the MigrationService (Administrator = all; Member = invoices:read). Refresh tokens are single-use (rotated on every refresh, only their SHA-256 hash persisted).

## Consequences

- Auth is self-contained: no external IdP, stateless authorization, works for API and both UI heads.
- The permission catalogue (`Permissions`) is the single authorization vocabulary shared by endpoints, seeding, and tokens.
- Bootstrap admin credentials come from the `Seed` configuration section (dev defaults documented, overridable per environment).
- Keycloak/OIDC remains a future toggle (its Aspire integration is still preview) — the `CurrentUser`/permission seam makes swapping the issuer a contained change.
  *(Corrected 2026-10-11: it is not contained; see the amendment below.)*
- Blazor circuit auth is demo-grade (a bearer token held per circuit); production guidance is BFF/cookies (ADR-0015).

## Amendment (2026-10-08): account lockout

The decision above left Identity's lockout columns as dead schema: per-account brute force was
throttled only by the per-source rate limit, which a distributed guesser does not trip. Identity's
lockout is now on (`Lockout.AllowedForNewUsers`, 5 failed passwords, 15 minutes; a successful login
resets the counter), checked before the password, and it **stays visible**: a locked account answers
403 `Auth.AccountLockedOut` whatever password is supplied, and the UI shows the problem's `detail`
instead of "wrong password".

- **Why not collapse it into the 401.** Hiding the lockout would leave a legitimate user retrying a
  correct password against a locked account with no way to know why, and support with no signal. A
  403 never confirms a password (lockout is checked first); that it confirms an account exists is an
  accepted risk, recorded in `docs/threat-model.md`.
- **The timing oracle is closed separately.** An unknown e-mail and a wrong password both answer
  401 `Auth.InvalidCredentials` and both spend one password-hash verification (`DummyPasswordHash`
  for the unknown address), so neither the answer nor its latency tells them apart.
- **Every outcome is a security event** (`SecurityEventLog`, events 1101–1104: failed sign-in,
  failed sign-in for an unknown account with the e-mail masked, lockout tripped, sign-in rejected
  while locked), carrying the user id, never a password or an e-mail as typed.
- Per-source throttling stays the API's `auth` rate-limit policy (ADR-0021); lockout is the
  per-account layer. Fitness: `AuthSecurityTests` (thresholds, reset), `AuthFlowTests` (4 × 401 →
  403 → 403, the timing oracle through a counting hasher, the events through a capturing logger).

## Amendment (2026-10-11): an external issuer is not a contained change

The consequence above called swapping the issuer "a contained change" behind the `CurrentUser` and
permission seam. Tracing what a second bearer scheme touches shows it is not:

- the permission policies name no scheme, so tokens from two issuers need a policy scheme that forwards by
  issuer before any policy can see them;
- the problem-details challenge, the forbidden body and, with multi-tenancy, the fail-closed tenant check
  are events of the local scheme only, and each scheme needs them;
- the HS256 algorithm pin belongs to the tokens this API mints, and a provider's scheme validates its own
  asymmetric keys;
- `ICurrentUser.UserId` is a `Guid` read from `NameIdentifier`, which a provider's `sub` rarely is, so
  external principals need a claims transformation to a local user, or auditing, ownership and the
  idempotency key's per-user scope all break;
  *(Corrected 2026-10-18: auditing did not read the id; see the amendment below.)*
- the Blazor UI signs in by posting a password, so external users need an authorization-code flow or a
  backend-for-frontend, which is a new package.

The decision stands: the template stays self-contained, with no external IdP. `docs/features/auth.md`
(*Adding an external identity provider*) is the checklist for a project that adds one, and a toggle that
did it for every generated project would be a new ADR.

## Amendment (2026-10-18): what a missing user id does to auditing

The 2026-10-11 amendment counted auditing among what breaks when an external principal has no
`ICurrentUser.UserId`. It did not break: `AuditingInterceptor` stamped `ICurrentUser.UserName` and never
read the id. It reads the id now, because the audit columns hold the acting user's id instead of their
e-mail (ADR-0027's amendment of the same date), and falls back to the name only for a caller without
one, which is how the migration job's `SystemCurrentUser` stamps `system`. An external principal without
the claims transformation is therefore stamped with its token's name claim, or with nothing when it has
none: an audit trail that cannot be joined to a user, not a failure. Ownership and the idempotency key's
per-user scope break as stated. `docs/features/auth.md` (*Adding an external identity provider*) says
the same.
