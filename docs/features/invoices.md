# Feature: Invoices

The **reference vertical slice** — the canonical shape to copy for new features (aggregates or use cases). It exercises every tactical pattern in the template.

## Domain (`src/Core/XTemplate.Domain/Invoices/`)

- **`Invoice`** — aggregate root (`AggregateRoot<InvoiceId>`, also `IAuditable`, `ISoftDeletable`).
  - Created via `static Create(customerName, EmailAddress, currency) → Result<Invoice>`; `private Invoice()` for EF.
  - Lifecycle (each returns `Result`, raises events, enforces invariants): `AddLine` (Draft only) → `Issue(dueDate)` (needs ≥1 line, raises `InvoiceIssuedDomainEvent`) → `MarkPaid(paidAt)` (Issued only, raises `InvoicePaidDomainEvent`) / `Void()`.
  - `Total` computed from lines via `Money` operator overloads.
- **`InvoiceLine`** — child entity behind a read-only `Lines`; created only through `Invoice.AddLine`.
- **Value objects** (EF complex types): `Money(decimal, string)`, `EmailAddress` — both with `Create → Result<T>`.
- **Strongly-typed IDs**: `InvoiceId`, `InvoiceLineId` (`readonly record struct … : IStronglyTypedId<>, IParsable<>`).
- **`InvoiceSpecs.Overdue(now)`**, **`InvoiceErrors`**, **`IInvoiceRepository`**.

## Application (`src/Core/XTemplate.Application/Features/Invoices/`)

Use-case slices, each = command/query + `internal sealed` handler + validator: `CreateInvoice`, `AddInvoiceLine`, `IssueInvoice`, `PayInvoice`, `VoidInvoice`, `GetInvoice`, `ListOverdueInvoices`. Plus `EventHandlers/SendReceiptOnInvoicePaid` (→ `IEmailSender`) and `InvoiceResponse` (read model).

## Persistence (`src/Infrastructure/XTemplate.Persistence/`)

`InvoiceConfiguration` / `InvoiceLineConfiguration` (complex-type mapping, `AutoInclude`, `"SoftDeleted"` filter), `InvoiceRepository`, ID conventions in `AppDbContext`.

## API (`src/Presentation/Api/XTemplate.Api/Features/Invoices/`)

One `IEndpoint` class per use case on `/api/v1/invoices…`, each `.RequirePermission(Permissions.Invoices.Read|Write)`, mapping `Result` → `TypedResults`/`CustomResults.Problem`. Wire contracts in `Api.Contracts/Invoices/`.

**Caching**: `GetInvoice` is cache-aside via `HybridCache` (key from the injected `CacheKeys` service); the mutating endpoints (`AddInvoiceLine`, `IssueInvoice`, `PayInvoice`, `VoidInvoice`) invalidate that key on success. A miss is never pinned (the stored null is removed so a later create isn't answered 404 for the TTL), and with the **`UseMultitenancy`** toggle the key carries the tenant — a cache hit bypasses the EF tenant filter, so unscoped keys would leak across tenants. HybridCache uses an in-memory L1 always; a Redis L2 is added when the **`UseRedis`** template toggle is on (Aspire `cache` resource + `AddRedisDistributedCache`).

**Pagination**: `GET /invoices/overdue` is paged — binds `PageRequest` (`?page=&pageSize=`, validated 1..100) and returns `PagedResponse<InvoiceDto>` (items + total count + page metadata). The query flows through the repository's `ListPagedAsync` over the existing `Specification<T>` seam.

**Optimistic concurrency**: `Invoice` carries the Postgres `xmin` concurrency token (`InvoiceConfiguration.UseXmin...`); a conflicting write throws `DbUpdateConcurrencyException`, mapped to **409** by `ConcurrencyExceptionHandler` in the API host.

**Transactional outbox**: `MarkPaid` raises `InvoicePaidDomainEvent`, which the `OutboxWriteInterceptor` persists to `OutboxMessages` in the same transaction; the `OutboxProcessor` background service dispatches it post-commit to `SendReceiptOnInvoicePaid` (ADR-0018). Domain events use primitive types so they serialize, and every event carries a stable wire name (`[DomainEventName("invoices.paid.v1")]`) so renaming a class never orphans in-flight rows (a serialization fitness test enforces both). Dispatch is production-shaped: rows are claimed `FOR UPDATE SKIP LOCKED` (replicas never double-dispatch), failures back off exponentially (30s→4m), per-handler completion is recorded so a retry never re-runs a handler that already succeeded, and exhausted or unresolvable messages are **dead-lettered** with a critical log and the `xtemplate.outbox.dead_lettered` counter — never dropped silently. Each row also carries the originating `traceparent` (dispatch spans rejoin the request's trace) and, with multi-tenancy, the originating tenant (restored via `AmbientTenant` while handlers run). A `DataRetentionJanitor` prunes processed rows, stale idempotency keys and dead refresh tokens on a timer (`Retention` config section).

**Idempotency**: the invoice command endpoints accept an `Idempotency-Key` header (max 128 chars, else **400**); a duplicate returns **409** (ADR-0018). The reservation is scoped `(user, route, key)` — different callers or endpoints reusing a value never collide — and only a *successful* outcome keeps the key: an exception or a 4xx/5xx result releases it so the request can be retried. Only a Postgres unique violation counts as duplicate; other database errors propagate instead of masquerading as 409. The UI's typed client sends a fresh key per logical POST (`ApiHeaders.IdempotencyKey`), so the resilience handler's automatic retries can never double-commit.

**Business metrics**: `InvoiceMetrics` (meter `XTemplate.Invoices`) exports `xtemplate.invoices.issued` (tagged by currency) and `xtemplate.invoices.paid`, incremented by outbox-dispatched event handlers so they count only committed events.

## UI (`src/Presentation/UI/XTemplate.UI.Common/Pages/`)

`Invoices` (paged list with pager controls), `InvoiceDetail` (view + pay/void; action errors are kept separate from load errors so they survive the post-action reload), `CreateInvoice` — via `IInvoiceApiClient` over the contracts. The session outlives the 15-minute access token: `TokenAuthenticationStateProvider` checks `exp` and rotates through `POST /auth/refresh` (single-flight, since refresh tokens are single-use); `BearerTokenHandler` always attaches a currently-valid token via `IAccessTokenSource`.

## Tests

`Domain.UnitTests/Invoices` (invariants, events, spec) · `Application.UnitTests/Invoices` (handlers + pipeline) · `IntegrationTests` (roundtrip, spec→SQL, interceptors, soft-delete, event→email) · `ApiTests` (E2E lifecycle + error contracts).

## Endpoints

| Method | Route | Permission |
|---|---|---|
| POST | `/api/v1/invoices` | invoices:write |
| POST | `/api/v1/invoices/{id}/lines` | invoices:write |
| POST | `/api/v1/invoices/{id}/issue` | invoices:write |
| POST | `/api/v1/invoices/{id}/pay` | invoices:write |
| POST | `/api/v1/invoices/{id}/void` | invoices:write |
| GET | `/api/v1/invoices/{id}` | invoices:read |
| GET | `/api/v1/invoices/overdue` | invoices:read |

> When you change this feature's behavior, update this file (the CI docs-drift check enforces it).
