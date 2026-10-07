---
description: Minimal-API endpoint conventions for the Presentation/Api layer
paths: ["src/Presentation/Api/**/*.cs"]
---

# API layer rules

- **One endpoint per use case**, a class implementing `IEndpoint` (static-abstract `Map`) in `Features/{Feature}/`. Discovered by `MapEndpoints` onto the versioned `/api/v{version:apiVersion}` group (resolves to `/api/v1`).
- Handlers are `static`, inject the Application handler interface, and return `Results<TSuccess, ProblemHttpResult>`. Map the `Result` with `result.IsSuccess ? TypedResults.X(...) : CustomResults.Problem(result.Error)`.
- **Never throw for expected failures** — everything flows as `Result` → `CustomResults.Problem` (RFC 9457). The global exception handler is only for unhandled faults.
- **Bind `Api.Contracts` records**, not domain types or Application commands. Map contract → command in the handler.
- **Secure by default** (ADR-0021): every endpoint on the versioned group is authenticated by default (group `.RequireAuthorization()`, no fallback policy). Declare the permission with `.RequirePermission(Permissions.X.Y)`; a name outside `Permissions.All` throws at startup. An endpoint that must be reachable without a token calls `.AllowAnonymous()` **and** is added to `EndpointMetadataTests.AnonymousAllowlist` — the test fails otherwise. Probes and API docs are mapped on the host, never on the group.
- **Rate limits**: `.RequireRateLimiting(ApiHardeningExtensions.AuthRateLimitPolicy)` only on endpoints that accept a **guessable** credential (login, register); token endpoints and everything else sit under the global per-client limiter. Probes are exempt (`.DisableRateLimiting()`), never short-circuited.
- **Host hardening lives in `AddApiHardening`/`UseApiHardening`** — forwarded headers (trust never widens: loopback-only unless `ForwardedHeaders:KnownProxies/KnownNetworks` say otherwise), security headers, `no-store` on `/auth/`, 1 MB body / 32 KB header caps. Don't re-add any of it per endpoint; raise the body limit only on the endpoint that needs it.
- **Write endpoints add `.RequireIdempotency()`** (keys are scoped `(user, route, key)`; failed outcomes release the reservation) and **invalidate the read cache through the injected `CacheKeys` service** — never hand-build key strings; with multi-tenancy the key carries the tenant because a cache hit bypasses the EF tenant filter.
- **List endpoints** bind `[AsParameters] PageRequest` and return the `PagedResponse<T>` envelope (`.ToPagedResponse(...)`) — never a bare array.
- **Concurrency**: the unit of work never throws for an expected outcome — `IUnitOfWork.CommitAsync` returns a stale `xmin` as `UnitOfWorkErrors.ConcurrencyConflict`, which reaches the endpoint as a failed `Result` (409 via `CustomResults`). Nothing in Presentation catches EF exceptions.
- OpenAPI metadata comes from `TypedResults` + `.WithTags`/`.WithSummary`/`.WithDescription` (`EndpointMetadataTests` insists on summary and description). Scalar UI is dev-only.

`Api.Contracts` is a pure DTO surface — no references, no logic (ADR-0013).
