# ADR-0023: Outbox lease claim and per-handler checkpoints

- **Status**: accepted
- **Date**: 2026-10-08
- **Amended**: 2026-10-11 — the lease is also the fence on every bookkeeping write (see *Amendment* below)
- **Refines**: ADR-0018 (the transactional outbox stays; this decides how the processor dispatches,
  retries and fails). It does not supersede it.

## Context

ADR-0018 moved domain-event handlers behind an outbox: `OutboxWriteInterceptor` writes a row in the
aggregate's transaction and `OutboxProcessor` dispatches it afterwards. The first processor claimed a
batch with `FOR UPDATE SKIP LOCKED`, ran every handler of every message inside that claim transaction,
and saved all the bookkeeping with one `SaveChanges` at the end. Four forces made that shape wrong, each
observed in the audit:

1. **The retrying execution strategy re-runs its delegate.** `AddPersistence` enables
   `EnableRetryOnFailure`, so the delegate passed to `ExecuteAsync` is re-executed after a transient
   failure. With dispatch inside it, a blip at the final `SaveChanges` or `Commit` rolled every row back
   to pending, re-claimed the same rows and ran every handler again, e-mail included; the
   `CompletedHandlers` credits were lost with the rollback. Savepoints inside the transaction would not
   have changed this — only taking dispatch out of the retried delegate does.
2. **One transaction per batch means one failure per batch.** Deserialization and the
   `CompletedHandlers` parse ran before the per-message `try`, so an unreadable row (an event type
   removed in a deploy, a payload that no longer parses) threw out of the whole batch every five seconds:
   the dead-letter counter never fired, and the other messages' handlers re-ran each time. The row locks
   and the connection were also held for the whole batch's handler I/O.
3. **`AmbientTenant` was reset per message but saved per batch**, so under `--use-multitenancy` a
   handler's writes and their cascaded outbox rows were stamped with no tenant.
4. **Cancellation was classified by exception type.** `catch (Exception e) when (e is not
   OperationCanceledException)` let a handler's `HttpClient` timeout — an `OperationCanceledException`
   too — escape the processor as if the host were stopping: no attempt counted, no retry, and the
   `BackgroundService` stopped the host.

Smaller things rode along: the knobs were constants (5 s poll, 20 rows, five attempts, 30 s base delay —
dead letter after about 7.5 minutes, a short window for a real downstream outage), the loop slept after
a full batch (~4 messages/s), spans never carried an error status, and there was no backlog signal.

Constraints: `Result` over exceptions for expected failures; Infrastructure and Persistence stay peers;
no new package; the `OutboxMessages` schema does not change (the shipped `InitialCreate` and
`MigrationDriftTests` stay as they are); the claim SQL and its partial index, the persisted
`traceparent` and the "counters are incremented only from committed events" property are kept.

## Decision

1. **Lease-based claim; dispatch outside the transaction.** The claim transaction is unchanged SQL
   (`FOR UPDATE SKIP LOCKED … ORDER BY "OccurredAtUtc" LIMIT BatchSize`) and does one more thing: it
   stamps `NextAttemptAtUtc = now + LeaseDuration` on the claimed rows and commits. To every other
   processor a leased row is simply "not due" — the predicate the claim and the partial index already
   use, so no new column. The transaction lasts milliseconds; the retried delegate is pure database work
   and re-running it is harmless.
2. **One DI scope and one bookkeeping save per message.** Each message is dispatched in its own scope
   (fresh `DbContext`, fresh scoped handlers) and finishes with its own `SaveChanges`, inside the
   ambient tenant scope. A failed message leaves its neighbours untouched.
3. **Per-handler checkpoint.** `IDomainEventDispatcher.Dispatch(event, completed, checkpoint, ct)`
   calls back after each handler and before crediting it; the processor's checkpoint serialises the
   credit and saves the scope's tracked writes with it. "Credited" therefore always means "durable": a
   handler's write is never lost once it is credited, and a write that cannot be committed leaves its
   handler uncredited so it runs again.
4. **Unreadable rows are dead-lettered at once, as a `Result`.** An unknown wire name or a payload /
   `CompletedHandlers` column that does not parse is a permanent, expected failure
   (`OutboxErrors.UnknownEventType`, `OutboxErrors.PoisonPayload`): the row is dead-lettered with
   `Attempts` untouched and the batch goes on.
5. **A dispatch budget and a derived lease.** Handlers see a token linked to the host's token and a
   timer of `DispatchTimeout`. `LeaseDuration` is not a knob: `DispatchTimeout × (BatchSize + 1)`, the
   longest a batch can legitimately take plus one slot for bookkeeping. A lease therefore expires only on
   a crashed or hung process. A graceful stop releases the leases of the rows it did not finish
   (`ExecuteUpdate` with `CancellationToken.None`, best effort), so a rolling deploy does not delay them.
6. **Cancellation is classified by token, not type**, at every background-service and decorator site:
   `when (e is not OperationCanceledException || !stoppingToken.IsCancellationRequested)`, keyed on the
   host's token, never on the linked dispatch token. A downstream timeout and a budget expiry are
   retryable failures; only a stopping host is not. `ConventionTests` scans `src/` and `tools/` for the
   type-only form.
7. **Validated `OutboxOptions`** (`Outbox` section): `PollInterval` 5 s, `BatchSize` 20, `MaxAttempts` 8,
   `BaseRetryDelay` 30 s, `MaxRetryDelay` 15 min, `DispatchTimeout` 30 s — DataAnnotations on the POCO,
   the host runs them at boot, the cross-field rule (`MaxRetryDelay ≥ BaseRetryDelay`) sits next to the
   binding, exactly as `RetentionOptions`. The back-off is a pure, capped, overflow-safe function
   (`OutboxBackoff`): 30 s, 1 m, 2 m, 4 m, 8 m, 15 m, 15 m, dead letter on the eighth failure (≈45 min).
8. **Drain loop.** A poll that claimed a full batch claims again at once; the loop sleeps only after a
   short one. A failing claim counts as zero and sleeps, so a broken database cannot hot-loop it.
9. **Telemetry.** The span starts before deserialization, carries `trellis.outbox.message_id`,
   `trellis.outbox.event_type`, `trellis.outbox.attempt` and `trellis.outbox.outcome`
   (`processed` / `retry_scheduled` / `dead_lettered`), and gets `SetStatus(Error)` + `AddException` on
   failure and dead letter. The meter gains `trellis.outbox.retries`, `trellis.outbox.dispatch.duration`
   and two gauges sampled by the processor on each poll (`trellis.outbox.pending`,
   `trellis.outbox.oldest_pending_age`); every instrument declares its unit, and the metric attribute
   `event.type` becomes `trellis.outbox.event_type` (a reserved semantic-convention namespace is not ours
   to use). A retry logs a Warning with the exception; a dead letter stays Critical.
10. **Re-drive is a documented SQL runbook** (`docs/deployment.md`, "Outbox operations"), not an admin
    endpoint: an endpoint needs a permission, an OpenAPI entry, contract tests and a UI decision, and the
    table is the outbox's console anyway.

## Consequences

- **No migration.**[^1] The lease reuses `NextAttemptAtUtc`; only its documentation changed.
- **Delivery stays at-least-once, now with a tighter bound.** The residual duplicate-dispatch risks are a
  process that crashes after a handler's side effect and before its checkpoint, and a handler that
  ignores its token past the lease. After a crash, in-flight rows reappear within `LeaseDuration`
  (10.5 min with the defaults); shorten `BatchSize` or `DispatchTimeout` to shorten it.
- **A failed bookkeeping save costs one message, not the batch**: when the database fails a message's
  failure, dead-letter or Processed save, the processor logs a Warning and goes on with the next message
  while that row keeps its lease (dispatched again when it expires, credited handlers skipped), anything
  else that ends a batch early releases the unreached rows' leases before it propagates, and since the
  budget bounds handlers rather than saves, a slow database can push a batch past its lease as well.
- **Handler rules** (`.claude/rules/application.md`): handlers run at-least-once; honour the
  `CancellationToken`; do unit-of-work writes before external side effects (a write that fails at the
  checkpoint un-credits the handler, so an e-mail sent before it would go out again); never call
  `SaveChanges` yourself — the outbox commits per handler.
- **Operators notice**: the dead-letter window grows from ≈7.5 to ≈45 minutes; the `Outbox` section is
  the place to tune it; dashboards built on `event.type` move to `trellis.outbox.event_type`; a stalled
  drain is visible as `trellis.outbox.oldest_pending_age` climbing long before anything dead-letters.
- **Tests**: `OutboxRetryTests` (throwing, timing out, writing and unreadable messages; two concurrent
  processors; a stopping host; the gauges and the spans), `OutboxBackoffTests`, `OutboxOptionsTests`,
  the dispatcher checkpoint tests, and the writing-handler case in `TenancyTests` on the all-options leg.


## Amendment (2026-10-11): the lease is also the fence

Decision 1 made a claimed row invisible to the other processors by stamping `NextAttemptAtUtc`, and
the consequences admitted the residue: a processor that outlived its lease — a handler ignoring its
token, a database slower than the budget — kept writing. Its failure path ran `context.Update(message)`
and rewrote every column, so a stale processor could set a row the next claimant had finished back to
Pending, or dead-letter the new owner's row. The fix reuses the same column once more:

- **`NextAttemptAtUtc` is the `OutboxMessage` concurrency token.** Every bookkeeping save of the
  message scope — each checkpoint, Processed, the back-off, the dead letter — is an
  `UPDATE … WHERE "Id" = … AND "NextAttemptAtUtc" = <the lease this processor stamped>`. A re-claim
  stamps a later lease, a finished row keeps the new owner's, a re-drive stamps `now()`: none equals the
  stale lease, so the save affects no row and EF raises `DbUpdateConcurrencyException` for the outbox
  entry. The processor books that as **`lease_lost`** (the span's outcome and Error status, the
  `trellis.outbox.dispatch.duration` outcome, Warning 1303), drops its tracker and writes nothing; the
  credits it checkpointed before losing the lease were saved under it and stand. A handler's own stale
  row at a checkpoint is not a lost lease — only an exception entry of type `OutboxMessage` counts;
  anything else is that attempt's ordinary, retryable failure.
- **A batch stops short of its lease.** Before each message the processor checks that a whole
  `DispatchTimeout` still fits before `leaseUntil`; if not, the rest of the batch is handed back
  (Warning 1306, the same release a stopping host does) instead of being dispatched into a certain
  `lease_lost`.
- **Signals follow the save.** The `trellis.outbox.retries` and `trellis.outbox.dead_lettered`
  increments and the retry (Warning 1301) and dead-letter (Critical 1302) lines are emitted after the
  row was written, not before: a save that fails — the database gone, the lease lost — announces
  nothing. Every outbox line carries an event id in the 1300 range (`OutboxLog`), and the span's
  `error.type` is the failing exception's full type name, or the `Outbox.*` code of an unreadable row.
- **Claim order is `ORDER BY "OccurredAtUtc", "Id"`**, and the rows of one save are written one
  microsecond apart, so an Issued event is claimed before the Paid event of the same save and two
  replicas see one order.
- **The model snapshot changed; the table did not.** See the footnote under *Consequences*.

The residual duplicate-dispatch window is as before (a crash between a side effect and its checkpoint,
a handler past the lease); what changed is that the stale processor can no longer corrupt the row.
`OutboxRetryTests` proves it: a processor that wakes up after its lease was taken over fails to book
either a back-off or a dead letter over the Processed row.

[^1]: Amended 2026-10-11: the concurrency-token annotation of the amendment below changed the EF model
    snapshot, so the shipped `InitialCreate` was regenerated (pre-release convention) with the
    `OutboxMessages` DDL unchanged. A database already migrated with the earlier `InitialCreate` adds a
    migration instead (`migrations add OutboxLeaseFence`), which comes out empty apart from the snapshot.
