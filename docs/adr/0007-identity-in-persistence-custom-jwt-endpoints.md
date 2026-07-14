# ADR-0007: Identity in Persistence, custom JWT auth endpoints, permission-based authorization

- **Status**: accepted
- **Date**: 2026-07-15

## Context

The template needs authentication and authorization that is self-contained (no external IdP dependency), works across the API and both UI heads, and stays legible. Two sub-decisions: where Identity lives given the Infrastructure ∥ Persistence split (ADR-0002), and whether to use ASP.NET Core Identity's built-in `MapIdentityApi` endpoints.

## Decision

**Identity lives in Persistence.** `ApplicationUser : IdentityUser<Guid>`, `AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>`, the `IIdentityService` implementation, and the rotating `RefreshToken` store all sit in `XTemplate.Persistence/Identity`. Identity is a persistence-backed subsystem (it owns tables and queries), so this keeps Infrastructure and Persistence cleanly parallel — the strongest justification for the split.

**Token *minting* lives in Infrastructure.** `JwtTokenProvider` (`ITokenProvider`) turns an `AuthenticatedUser` into a signed JWT and opaque refresh token — pure token concerns, no persistence — matching the peer arrangement.

**Custom auth endpoints, not `MapIdentityApi`.** `Features/Auth` has hand-written register/login/refresh use cases. `MapIdentityApi` issues opaque bearer tokens with no room for permission claims and hides the flow; our JWTs embed the user's permissions as `permission` claims so authorization is a local, stateless check.

**Permission-based authorization.** Endpoints declare `RequirePermission(Permissions.Invoices.Write)`. A `PermissionPolicyProvider` materializes a policy per permission on demand (no up-front registration); a `PermissionAuthorizationHandler` checks the claim. Permissions are granted to roles as claims and seeded by the MigrationService (Administrator = all; Member = invoices:read). Refresh tokens are single-use (rotated on every refresh, only their SHA-256 hash persisted).

## Consequences

- Auth is self-contained: no external IdP, stateless authorization, works for API and both UI heads.
- The permission catalogue (`Permissions`) is the single authorization vocabulary shared by endpoints, seeding, and tokens.
- Bootstrap admin credentials come from the `Seed` configuration section (dev defaults documented, overridable per environment).
- Keycloak/OIDC remains a future toggle (its Aspire integration is still preview) — the `CurrentUser`/permission seam makes swapping the issuer a contained change.
- Blazor circuit auth (M7) will be demo-grade (bearer token in the client); production guidance is BFF/cookies.
