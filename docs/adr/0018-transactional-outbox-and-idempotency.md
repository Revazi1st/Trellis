# ADR-0018: Transactional outbox for domain events + command idempotency

- **Status**: accepted
- **Date**: 2026-07-16

## Context

Domain events were dispatched **inside** `SaveChanges` (ADR-0006's original interceptor), so a side-effecting handler (`SendReceiptOnInvoicePaid` sends an email) ran before the transaction committed. With EF retry now owned by the DbContext (P2), a transient Postgres blip could re-run the save and **re-send the email**; a rolled-back transaction could send an email for a payment that never happened. Separately, POST commands weren't idempotent — a client retry created duplicate invoices/payments.

## Decision

**Transactional outbox.** `OutboxWriteInterceptor` serializes each pending domain event into an `OutboxMessage` row **in the same transaction** as the aggregate change (no handlers run during `SaveChanges`). `OutboxProcessor` (a `BackgroundService` in the long-running API host, not the one-shot MigrationService) polls unprocessed messages, deserializes, and dispatches them **after commit**, marking each processed (with bounded retries). Side effects therefore run at-least-once, only after the data is durably committed.

- Domain events crossing the outbox use **primitive/simple types** (e.g. `InvoicePaidDomainEvent` carries a `string` email, not the `EmailAddress` value object) so they JSON round-trip. Strongly-typed IDs round-trip via the SharedKernel JSON converter.

**Command idempotency.** An opt-in `IdempotencyFilter` endpoint filter reserves an `Idempotency-Key` header value (`IIdempotencyStore` → `IdempotencyKeys` table, PK-enforced) before the handler runs; a duplicate key short-circuits with **409**. If the handler throws, the key is released so a genuinely-failed request can be retried. Applied to the invoice command endpoints.

## Consequences

- Closes the concurrency/idempotency/outbox/retry interaction: retried transactions no longer re-fire side effects (outbox), duplicate requests can't double-apply (idempotency), and lost updates are caught (xmin token, P2).
- Eventual delivery of side effects (post-commit) instead of synchronous — the correct trade-off for external effects.
- Two new tables (`OutboxMessages`, `IdempotencyKeys`). Response-replay for idempotency (returning the original response body) is the documented evolution; the current filter is at-most-once with a 409 on replay.
