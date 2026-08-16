---
description: Minimal-API endpoint conventions for the Presentation/Api layer
paths: ["src/Presentation/Api/**/*.cs"]
---

# API layer rules

- **One endpoint per use case**, a class implementing `IEndpoint` (static-abstract `Map`) in `Features/{Feature}/`. Discovered by `MapEndpoints` onto the versioned `/api/v{version:apiVersion}` group (resolves to `/api/v1`).
- Handlers are `static`, inject the Application handler interface, and return `Results<TSuccess, ProblemHttpResult>`. Map the `Result` with `result.IsSuccess ? TypedResults.X(...) : CustomResults.Problem(result.Error)`.
- **Never throw for expected failures** — everything flows as `Result` → `CustomResults.Problem` (RFC 9457). The global exception handler is only for unhandled faults.
- **Bind `Api.Contracts` records**, not domain types or Application commands. Map contract → command in the handler.
- **Authorize** with `.RequirePermission(Permissions.X.Y)`. Anonymous endpoints call `.AllowAnonymous()`.
- **Write endpoints add `.RequireIdempotency()`** (keys are scoped `(user, route, key)`; failed outcomes release the reservation) and **invalidate the read cache through the injected `CacheKeys` service** — never hand-build key strings; with multi-tenancy the key carries the tenant because a cache hit bypasses the EF tenant filter.
- **List endpoints** bind `[AsParameters] PageRequest` and return the `PagedResponse<T>` envelope (`.ToPagedResponse(...)`) — never a bare array.
- **Concurrency**: `DbUpdateConcurrencyException` (the `xmin` token) becomes a 409 via `ConcurrencyExceptionHandler` — don't catch it in endpoints.
- OpenAPI metadata comes from `TypedResults` + `.WithTags`/`.WithSummary`. Scalar UI is dev-only.

`Api.Contracts` is a pure DTO surface — no references, no logic (ADR-0013).
