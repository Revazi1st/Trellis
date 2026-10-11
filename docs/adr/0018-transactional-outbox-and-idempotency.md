# ADR-0018: Transactional outbox for domain events + command idempotency

- **Status**: accepted — supersedes the in-transaction domain-event dispatch of ADR-0006; processor semantics (lease claim, per-handler checkpoints, back-off, dead-letter window) refined by ADR-0023
- **Amended**: 2026-10-08 — idempotency v2: the reservation rides the command's unit of work and the stored response is replayed (see *Amendment* below)
- **Amended**: 2026-10-11 — declared unique constraints: every registered constraint becomes its `Result`, any unregistered constraint still throws (see *Amendment (2026-10-11)* below)
- **Date**: 2026-07-16

## Context

Domain events were dispatched **inside** `SaveChanges` (ADR-0006's original interceptor), so a side-effecting handler (`SendReceiptOnInvoicePaid` sends an email) ran before the transaction committed. With EF retry owned by the DbContext (`EnableRetryOnFailure`), a transient Postgres blip could re-run the save and **re-send the email**; a rolled-back transaction could send an email for a payment that never happened. Separately, POST commands weren't idempotent — a client retry created duplicate invoices/payments.

## Decision

**Transactional outbox.** `OutboxWriteInterceptor` serializes each pending domain event into an `OutboxMessage` row **in the same transaction** as the aggregate change (no handlers run during `SaveChanges`). `OutboxProcessor` (a `BackgroundService` in the long-running API host, not the one-shot MigrationService) polls unprocessed messages, deserializes, and dispatches them **after commit**, marking each processed (with bounded retries). Side effects therefore run at-least-once, only after the data is durably committed.

- Domain events crossing the outbox use **primitive/simple types** (e.g. `InvoicePaidDomainEvent` carries a `string` email, not the `EmailAddress` value object) so they JSON round-trip. Strongly-typed IDs round-trip via the SharedKernel JSON converter.

**Command idempotency.** An opt-in `IdempotencyFilter` endpoint filter reserves an `Idempotency-Key` header value (`IIdempotencyStore` → `IdempotencyKeys` table, PK-enforced) before the handler runs; a duplicate key short-circuits with **409**. If the handler throws, the key is released so a genuinely-failed request can be retried. Applied to the invoice command endpoints. *(Superseded by the 2026-10-08 amendment below: the reservation is no longer written before the handler runs, and a retry is answered with the stored response rather than refused.)*

## Consequences

- Closes the concurrency/idempotency/outbox/retry interaction: retried transactions no longer re-fire side effects (outbox), duplicate requests can't double-apply (idempotency), and lost updates are caught (the `xmin` concurrency token).
- Eventual delivery of side effects (post-commit) instead of synchronous — the correct trade-off for external effects.
- Two new tables (`OutboxMessages`, `IdempotencyKeys`). Response replay for idempotency (returning the original response) was named here as the documented evolution; the amendment below delivers it.

## Amendment (2026-10-08): idempotency v2 — reserve inside the unit of work, replay the stored response

### Why

The original filter left two holes exactly where the header exists to help. It committed the key in its **own** `SaveChanges` before the handler ran, so a process kill between the reservation and the command's commit blocked the client's retry for `Retention:IdempotencyKeyAge` (2 days) for an operation that never happened. And a retry of an operation that *did* happen — a client re-sending after a lost response — was answered "already processed" with no id and no `Location`, so the client could not finish what it started.

### Decision

- **The reservation rides the command's unit of work.** `IIdempotencyStore` becomes `FindAsync(key)` → `None | InFlight(reservedAtUtc) | Completed(requestFingerprint, StoredResponse)`, `Reserve(key, fingerprint)` — which only *tracks* the row on the request's scoped `AppDbContext` — and `CompleteAsync(key, StoredResponse)`. The UnitOfWork decorator's commit therefore writes the reservation, the aggregate and the outbox rows in one transaction. `TryReserveAsync`/`ReleaseAsync` are gone: nothing is written before the commit, so there is nothing to release. A command that fails before its commit (validation, not found, concurrency conflict, exception) leaves no reservation and its key may be retried as is.
- **The commit race is decided by the primary key.** Two first requests both pass the lookup, both reserve, both run; the loser's `SaveChanges` hits `PK_IdempotencyKeys` and its whole unit of work rolls back. `AppDbContext.CommitAsync` maps that one unique violation to `IdempotencyErrors.Duplicate` (409 through the Result seam, ADR-0004 amendment) — which is why the `IdempotencyErrors` catalogue lives in `Application.Abstractions`, where Persistence can reach it. Any other unique violation still throws. *(Refined by the 2026-10-11 amendment below: any **unregistered** constraint still throws; a registered one becomes its `Result`.)*
- **Scope and fingerprint.** The stored key is `SHA-256(user:METHOD:path:client key)` — the method joins the composite. The fingerprint is `SHA-256(method, path + query, SHA-256 of the body)` where the body is the one the endpoint bound, re-serialised with the host's JSON options: the filter runs after binding, when the request stream is spent, and the canonical form is the better definition anyway (whitespace or member order is not a different request; a changed value is).
- **The row** (`IdempotencyKeys`) gains `RequestFingerprint`, `StatusCode`, `ContentType`, `Body`, `Location` and `CompletedAtUtc`; a row without a `StatusCode` is in flight. `InitialCreate` was regenerated (pre-release convention; the template repository's all-options CI leg regenerates it again with the tenant columns).
- **What a retry gets.** Completed with the same fingerprint and a stored body: the response, byte for byte, with `Idempotency-Replayed: true`; `Location` is the only header replayed. Completed with the same fingerprint but no stored body: 409 `Idempotency.Duplicate` — the command applied once, read the resource. Completed with a different fingerprint: 422 `Idempotency.FingerprintMismatch` — the key was reused for another request, a client bug rather than a retry. In flight: 409 `Idempotency.InFlight` with `Retry-After: 1`. A blank key is still a 400 `Idempotency.InvalidKey`. The 422 is written from `IdempotencyProblems.FingerprintMismatch` (a `ProblemDescriptor` next to the filter) because a 422 has no `ErrorType`; every other outcome goes through `CustomResults`.
- **The replay cap.** The body is stored up to 64 KB (`IdempotencyFilter.DefaultMaxReplayBodyBytes`), per endpoint via `RequireIdempotency(maxReplayBodyBytes:)`. A larger response is served unchanged and completed without a body, so its retry is the 409 above. It is a property of an endpoint's response shape rather than a deployment knob, which is why it is not configuration.
- **Completion is one small write after the response went out**, never with the request's token. A fault after the commit (post-commit work must use `CancellationToken.None`, `.claude/rules/api.md`) ends the request with a 500 but completes the reservation without a body: the retry is a 409, never a second apply.
- **Retention is unchanged**: the janitor sweeps `IdempotencyKeys` by `CreatedAtUtc` whatever the state.

### Known residue

A crash between the commit and `CompleteAsync` leaves a committed command with no stored response: the row reads in flight (409 `Idempotency.InFlight`) until retention sweeps it (default 2 days). The same window opens if storing the outcome fails (logged at Warning). It cannot be closed without serialising the HTTP response inside the command's transaction, which would break the pipeline's commit boundary; the client's remedy is to read the resource.

### Consequences

- Clients may retry a keyed POST safely and receive the original answer; the UI's typed client can opt back in to automatic POST retries for the calls it keys (follow-up in UI.Common).
- One extra `SELECT` per keyed request (the lookup) and one `UPDATE` per successful one (the completion).
- Fitness: `IdempotencyReplayTests` (replayed 201 and 204, the 422, a commit race held at a barrier, an in-flight duplicate held at a gate, a failed command, the cap), `IdempotencyStoreTests` (the three states against Postgres, nothing written before the commit, the PK race, retention), `IdempotencyPostCommitTests`, `MigrationDriftTests`.

## Amendment (2026-10-11): declared unique constraints

### Why

The 2026-10-08 amendment mapped exactly one unique violation, `PK_IdempotencyKeys`, and said any other
still throws. Registration showed the cost: two registrations of one e-mail both pass the account
lookup and Identity's own uniqueness check, the second INSERT loses at Identity's unique `UserNameIndex`
(the user name is the e-mail), and the caller got a 500 for what a sequential duplicate answers as 409.

### Decision

- **One map, by constraint name.** `UniqueConstraintErrors` (internal to Persistence) maps a pinned
  constraint name to the `Error` its violation stands for: `PK_IdempotencyKeys` → `Idempotency.Duplicate`,
  `UserNameIndex` → `Auth.EmailAlreadyInUse`. Each name is pinned by its configuration
  (`IdempotencyKeyConfiguration.PrimaryKeyName`, `ApplicationUserConfiguration.UserNameIndexName`), not
  inherited silently from a library default.
- **Both commit points consult it.** `AppDbContext.CommitAsync` (the unit of work) and
  `IdentityService.RegisterAsync` (whose Identity writes commit in their own transaction) catch a
  `DbUpdateException` only when the map knows its constraint, clear the change tracker and return the
  mapped `Result`.
- **Any unregistered constraint still throws.** A violation nobody declared is a model the code did not
  foresee: a 500 with an Error log, never a guessed 409. There is deliberately no blanket "unique
  violation → Conflict". Registering a constraint means pinning its name, adding a row to the map and
  racing it in a test.
- **One code, one type.** Identity's reported rejections no longer share `Auth.RegistrationFailed`
  with the role-assignment rollback: a duplicate Identity reports itself is `Auth.EmailAlreadyInUse`
  (409), anything else is 400 `Auth.RegistrationRejected` whose description is a fixed sentence per
  `IdentityError.Code` (Identity's own descriptions echo the e-mail into the response and the outcome
  log); `Auth.RegistrationFailed` stays the 500 for the rollback.

### Consequences

- A lost registration race answers exactly what a sequential duplicate answers, and exactly one account
  exists (`RegistrationRaceTests`, which holds both registrations past Identity's checks before either
  inserts). `RegistrationRejectionTests` pins the codes and the PII-free descriptions.

