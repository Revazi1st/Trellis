# ADR-0027: Data retention for bookkeeping tables

- **Status**: accepted
- **Date**: 2026-10-08
- **Amended**: 2026-10-18 — the audit columns hold the acting user's id, not their e-mail (see *Amendment (2026-10-18)* below)
- **Prior art**: [CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture) by Jason Taylor (MIT): stamping audit columns with the user's id rather than a display name (the 2026-10-18 amendment)

## Context

Three tables exist only to make other mechanisms safe: `OutboxMessages` (ADR-0018, ADR-0023),
`IdempotencyKeys` (ADR-0018) and `RefreshTokens` (ADR-0007). Each gains a row per event, per keyed
request or per sign-in and refresh, and nothing ever removed one: every deployment grew them forever,
and the outbox's claim query slowed with its history. The rows are not business data; each stops
being useful after a bounded time, but deleting one too early breaks the mechanism it serves (an
idempotency key a client is about to retry with, a consumed refresh token whose replay must still
revoke its family, a dead letter nobody has looked at yet).

## Decision

- **`DataRetentionJanitor`** (Persistence, a `BackgroundService` in the API host) sweeps the three tables
  on a timer with set-based `ExecuteDelete` statements: no entity is loaded or tracked, the statements
  are idempotent, and every replica may run them.
- **Windows live in `RetentionOptions`** (`Retention` section), each with a range checked at boot
  (`ValidateDataAnnotations().ValidateOnStart()` in the API; a bad value stops it naming the key):
  `SweepInterval` 1 h (1 s – 1 day), `InitialDelay` 1 min before the first sweep (0 – 1 h, so booting
  replicas do not all sweep at once), `ProcessedOutboxAge` 3 days (≥ 1 h), `DeadLetteredOutboxAge`
  30 days (≥ 1 day: dead letters are the alert queue and wait for a human), `IdempotencyKeyAge` 2 days
  (≥ 1 h: keys guard short-horizon retries), `DeadRefreshTokenAge` 30 days (≥ 1 day; keep it at least
  `Authentication:RefreshTokenDays`, because a consumed token is what makes a replay detectable).
  Every window is capped at 365 days.
- **What is swept**: processed outbox rows by `ProcessedAtUtc`, dead-lettered ones by `OccurredAtUtc`,
  idempotency keys by `CreatedAtUtc` whatever their state, refresh tokens by `ConsumedAtUtc` once consumed, by `ExpiresAtUtc` otherwise.
  **Pending outbox rows are never touched.**
- **A failed sweep is retried by the next one** (logged at Warning); the janitor never stops the host.
  Its catch filter keys on the host token (ADR-0023's cancellation rule).
- **Business data is out of scope.** Aggregates are soft-deleted (`ISoftDeletable`, ADR-0006) and
  kept; an erasure or export path for personal data, and retention of the audit columns that hold the
  acting user's e-mail, are product decisions each project makes for its own jurisdiction
  (`docs/threat-model.md`).
  *(Amended 2026-10-18: the audit columns hold the acting user's id, not their e-mail; see the amendment below.)*

## Consequences

- The bookkeeping tables are bounded by write rate × window; `docs/deployment.md` documents the
  settings and the idempotency table's size.
- A new table that grows forever gets a sweep in the janitor and a window in `RetentionOptions`, with a
  seeded row either side of its cutoff in `DataRetentionJanitorTests` (`.claude/rules/persistence.md`).
- Fitness: `DataRetentionJanitorTests` (every predicate one second either side of its cutoff, pending
  rows survive), `OptionsConventionTests` (the ranges are attributes and an out-of-range value stops the
  API at boot).

## Amendment (2026-10-18): the audit columns hold the user id

`AuditingInterceptor` stamps `CreatedBy` and `ModifiedBy` with the acting user's id
(`ICurrentUser.UserId`) instead of their user name, which is their e-mail; a caller without an id, the
migration job, is stamped with its name (`system`). The id is the key the rest of the system already
uses for a user (refresh tokens, the idempotency partition, the security events), it does not change
when the e-mail does, and the columns, copied into every auditable row and every backup of it, no
longer spell out an address. No DDL: the columns stay `varchar(256)`, and rows written before keep the
e-mail they were stamped with until a project rewrites them. An id is still personal data, pseudonymous
rather than anonymous, so the decision above stands: an erasure or export path, and how long the columns
are kept, remain each project's (`docs/threat-model.md`).
