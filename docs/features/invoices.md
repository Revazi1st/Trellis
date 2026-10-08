# Feature: Invoices

The **reference vertical slice** — the canonical shape to copy for new features (aggregates or use cases). It exercises every tactical pattern in the template.

## Domain (`src/Core/Trellis.Domain/Invoices/`)

- **`Invoice`** — aggregate root (`AggregateRoot<InvoiceId>`, also `IAuditable`, `ISoftDeletable`).
  - Created via `static Create(customerName, EmailAddress, currency) → Result<Invoice>`; `private Invoice()` for EF.
  - Lifecycle (each returns `Result`, raises events, enforces invariants): `AddLine` (Draft only) → `Issue(dueDate, now)` (needs ≥1 line, raises `InvoiceIssuedDomainEvent`) → `MarkPaid(paidAt)` (Issued only, raises `InvoicePaidDomainEvent`) / `Void()` (Draft or Issued; Voided is terminal).
  - Due date: `Issue` rejects a missing (`default`) or past due date — past relative to the `now` the handler takes from `TimeProvider` — with `InvoiceErrors.DueDateInPast` (400), and `IssueInvoiceCommandValidator` answers a body without one first; an invoice is never overdue the moment it is issued.
  - `Void()` raises `InvoiceVoidedDomainEvent` (wire name `invoices.voided.v1`); no handler ships with it — the outbox dispatches it to none and marks it processed, so subscribe when a reaction is needed.
  - `Total` computed from lines via `Money` operator overloads.
- **`InvoiceLine`** — child entity behind a read-only `Lines`; created only through `Invoice.AddLine`.
- **Value objects** (EF complex types): `Money(decimal, string)`, `EmailAddress` — both with `Create → Result<T>`.
- **Strongly-typed IDs**: `InvoiceId`, `InvoiceLineId` (`readonly record struct … : IStronglyTypedId<>, IParsable<>`).
- **`InvoiceSpecs.Overdue(now)`**, **`InvoiceErrors`**, **`IInvoiceRepository`**.

## Application (`src/Core/Trellis.Application/Features/Invoices/`)

Use-case slices, each = command/query + `internal sealed` handler + validator: `CreateInvoice`, `AddInvoiceLine`, `IssueInvoice`, `PayInvoice`, `VoidInvoice`, `GetInvoice`, `ListOverdueInvoices`. Plus `EventHandlers/SendReceiptOnInvoicePaid` (→ `IEmailSender`) and `InvoiceResponse` (read model).

## Persistence (`src/Infrastructure/Trellis.Persistence/`)

`InvoiceConfiguration` / `InvoiceLineConfiguration` (complex-type mapping, `AutoInclude`, `"SoftDeleted"` filter), `InvoiceRepository`, ID conventions in `AppDbContext`. With `UseMultitenancy`, the `TenantId` shadow column, its index and the `"Tenant"` filter come from the aggregate-root convention in `AppDbContext.OnModelCreating` (ADR-0022) — `InvoiceConfiguration` contains no tenant code, and neither should yours.

## API (`src/Presentation/Api/Trellis.Api/Features/Invoices/`)

One `IEndpoint` class per use case on `/api/v1/invoices…`, each `.RequirePermission(Permissions.Invoices.Read|Write)`, mapping `Result` → `TypedResults`/`CustomResults.Problem`. Wire contracts in `Api.Contracts/Invoices/`.

**Error contract** (ADR-0024): every failure is an RFC 9457 problem with a stable `code` (what clients branch on), `type` = `urn:problem-type:{code}`, a human `title`, `detail`, `instance`, `traceId` and `correlationId`. A 400 carries `errors[]` — one `{field, code, description}` per failure, `field` being the request's JSON path (validator failures: `customerName` / `Validation.NotEmpty`) or `null` for a whole-request error (value objects, aggregate rules). `POST /invoices {"customerName":"","customerEmail":"x","currency":"USD"}` answers:

```json
{ "type": "urn:problem-type:Validation.General", "title": "Validation failed", "status": 400,
  "detail": "One or more validation errors occurred.", "instance": "/api/v1/invoices",
  "code": "Validation.General", "correlationId": "…", "traceId": "…",
  "errors": [ { "field": "customerName", "code": "Validation.NotEmpty", "description": "'Customer Name' must not be empty." } ] }
```

The slice's codes: `Invoice.NotFound` (404), `Invoice.NotDraft` / `Invoice.NotIssued` / `Invoice.AlreadyFinalized` (409), `Invoice.NoLines` / `Invoice.DueDateInPast` / `Invoice.EmptyCustomerName` / `Invoice.EmptyLineDescription` / `Invoice.CurrencyMismatch` (400, `field: null`), `EmailAddress.*` and `Money.*` from the value objects (400, `field: null`), `Idempotency.InvalidKey` (400) and `Idempotency.Duplicate` (409) from the filter, `Concurrency.Conflict` (409) from the unit of work. A new error is a new `InvoiceErrors` member — nothing changes in the API.

**Caching**: `GetInvoice` is cache-aside via `HybridCache` (key from the injected `CacheKeys` service); the mutating endpoints (`AddInvoiceLine`, `IssueInvoice`, `PayInvoice`, `VoidInvoice`) invalidate that key on success. A miss is never pinned (the stored null is removed so a later create isn't answered 404 for the TTL), and with the **`UseMultitenancy`** toggle the key carries the tenant — a cache hit bypasses the EF tenant filter, so unscoped keys would leak across tenants. HybridCache uses an in-memory L1 always; a Redis L2 is added when the **`UseRedis`** template toggle is on (Aspire `cache` resource + `AddRedisDistributedCache`). Entries live 30 s in the L1 and 5 min in the L2 (`DefaultEntryOptions` in `Program.cs`): eviction reaches only the instance that handled the write (and the shared L2), so another API replica may serve the previous state, and its ETag, for up to 30 s.

**Pagination**: `GET /invoices/overdue` is paged — `?page=` (1..10000, default 1) and `?pageSize=` (1..100, default 20), bound inline with `[Range(PagingLimits.…)]` so the document states the camelCase names and the bounds (ADR-0025); `ListOverdueInvoicesQueryValidator` enforces the same bounds (a value outside answers 400 `Validation.General` with `field: "page"` / `"pageSize"`), and the response is `PagedResponse<InvoiceDto>` (items + total count + page metadata). The query flows through the repository's `ListPagedAsync` over the existing `Specification<T>` seam, which pages with checked arithmetic (the page cap keeps `OFFSET` bounded; an offset can never wrap negative). Results are ordered by due date, then by id — the unique tiebreaker every paged specification must end with (`ListPagedAsync` refuses one that does not, or that pages itself), so invoices due at the same instant never repeat or vanish across pages.

**Optimistic concurrency**: `Invoice` carries the Postgres `xmin` concurrency token (`InvoiceConfiguration.UseXmin...`). A conflicting write is an expected outcome, not an exception: `IUnitOfWork.CommitAsync` (`AppDbContext`) turns EF's `DbUpdateConcurrencyException` into `UnitOfWorkErrors.ConcurrencyConflict` (`Concurrency.Conflict`), the UnitOfWork decorator returns it as the command's `Result`, and the endpoint maps it to **409** through `CustomResults` like any other conflict (logged at Information, no exception handler involved).

**Transactional outbox**: `MarkPaid` raises `InvoicePaidDomainEvent`, which the `OutboxWriteInterceptor` persists to `OutboxMessages` in the same transaction; the `OutboxProcessor` background service dispatches it post-commit to `SendReceiptOnInvoicePaid` (ADR-0018). Domain events use primitive types so they serialize, and every event carries a stable wire name (`[DomainEventName("invoices.paid.v1")]`) so renaming a class never orphans in-flight rows (a serialization fitness test enforces both). Dispatch is production-shaped (ADR-0023): a short `FOR UPDATE SKIP LOCKED` claim leases a batch (replicas never double-dispatch), every message is then dispatched in its own scope outside the transaction, and each handler is credited under its stable `[DomainEventHandlerName]` (never its CLR name, so a rename cannot re-run it) with its own commit — the checkpoint — so a retry never re-runs a handler that already succeeded and a credited handler's writes are never lost. Failures back off exponentially (30 s, 1 m, 2 m, 4 m, 8 m, 15 m, 15 m; `Outbox` config section) and the message is **dead-lettered** on the eighth failure (≈45 min), as are an unknown event type and an unparsable payload at once — with a critical log and the `trellis.outbox.dead_lettered` counter, never dropped silently; `docs/deployment.md` has the re-drive runbook. A handler past `Outbox:DispatchTimeout` is cancelled and retried like any failure; a stopping host hands its unfinished leases back. Each row also carries the originating `traceparent` (dispatch spans rejoin the request's trace) and, with multi-tenancy, the originating tenant (restored via `AmbientTenant` while handlers run). A `DataRetentionJanitor` prunes processed rows, stale idempotency keys and dead refresh tokens on a timer (`Retention` config section).

**Idempotency**: the invoice command endpoints accept an `Idempotency-Key` header (max 128 chars and not blank, else **400** `Idempotency.InvalidKey` — omit the header to opt out, a blank value is never a silent opt-out); a duplicate returns **409** `Idempotency.Duplicate` (ADR-0018; both through `CustomResults`, ADR-0024). The reservation is scoped `(user, route, key)` — different callers or endpoints reusing a value never collide — and only a *successful* outcome keeps the key: an exception or a 4xx/5xx result releases it so the request can be retried. Work an endpoint does after its command committed (the read-cache invalidation) runs with `CancellationToken.None`, so a client that disconnects after the commit cannot make the filter release a key whose command already applied. Known limits until idempotency v2: a duplicate of a *completed* request is answered 409 without the original response (replay is planned), and a process crash between reservation and commit leaves the key reserved until `Retention:IdempotencyKeyAge` (default 2 days) — retry with a new key. Only a Postgres unique violation counts as duplicate; other database errors propagate instead of masquerading as 409. The UI's typed client sends a fresh key per logical POST (`ApiHeaders.IdempotencyKey`); ServiceDefaults' blanket resilience handler never retries POST, PUT, PATCH or DELETE, so a lost response is not re-sent behind the caller's back.

**Business metrics**: `InvoiceMetrics` (meter `Trellis.Invoices`, exported by every host through ServiceDefaults' `Trellis.*` subscription) exports `trellis.invoices.issued` (attribute `trellis.invoice.currency`) and `trellis.invoices.paid`, unit `{invoice}`, incremented by outbox-dispatched event handlers so they count only committed events.

**Conditional GET**: `InvoiceDto.Version` (a `long`, `format: int64`) surfaces the xmin concurrency
token, `GET /invoices/{id}` emits it as a weak ETag (`W/"42"`) and declares `.SupportsConditionalGet()`,
so the document lists `If-None-Match`, the `ETag` header and the 304. The header is parsed, not
string-compared (RFC 9110 §13.1.2): the weak form, the strong form `"42"`, a list and `*` all answer
**304** with no body, a stale or unparsable tag answers 200 — pollers stop re-downloading unchanged
aggregates. (Conditional writes stay guarded by xmin → 409 at commit; an `If-Match` precondition is
a candidate follow-up.)

**Wire types**: `InvoiceDto.Status` is the Contracts enum `InvoiceStatus` serialised by name
(`"Draft" | "Issued" | "Paid" | "Voided"`, a string enum in the document; `ContractMirrorTests` keeps it
equal to the domain enum) and money is `decimal` (`format: decimal`).

**OpenAPI** (ADR-0025): `/openapi/v1.json` describes exactly the v1 operations with concrete `/api/v1/…`
paths and no `servers`; every operation's `operationId` is its endpoint class (`CreateInvoice`,
`GetInvoice`, …); the bearer security scheme is declared (Scalar can authorize); every operation
documents its error responses by shape — 400 where there is input, 404 where a route id can miss, 409 on
a write to an addressed resource (and the `Idempotency-Key` + duplicate 409 on every `RequireIdempotency()`
endpoint), 401/403 on secured ones, 429 and 500 everywhere — all referencing the `ProblemResponse`
component generated from the Contracts record; the 201 declares `Location`. The document is committed at
`docs/api/openapi.v1.json` as the server serialises it: `OpenApiSnapshotTests` compares it with the
in-process host on every test run (`UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/Trellis.ApiTests` or
`scripts/export-openapi.sh` regenerates it) and CI diffs it against the running app.

## UI (`src/Presentation/UI/Trellis.UI.Common/Pages/`)

`Invoices` (paged list with pager controls), `InvoiceDetail` (the full lifecycle: add lines and issue while Draft, pay/void after; action errors are kept separate from load errors so they survive the post-action reload), `CreateInvoice` — via `IInvoiceApiClient` over the contracts. The create, add-line and issue forms validate before sending with DataAnnotations that mirror the command validators (`Forms/InvoiceForms.cs`: required, lengths, amount ≥ 0); the issue form's date picker starts at tomorrow (UTC), the first due date the API accepts (`Invoice.DueDateInPast`). What the API still rejects comes back as `errors[]`: each field error under its input, a whole-request error (`field: null`) in the form's alert (`ServerValidation`). Created, issued, paid and voided are announced as notifications. The session outlives the 15-minute access token: `TokenAuthenticationStateProvider` checks `exp` and rotates through `POST /auth/refresh` (single-flight, since refresh tokens are single-use); `BearerTokenHandler` always attaches a currently-valid token via `IAccessTokenSource`.

## Tests

`Domain.UnitTests/Invoices` (invariants, events, spec) · `Application.UnitTests/Invoices` (handlers + pipeline) · `IntegrationTests` (roundtrip, spec→SQL, interceptors, soft-delete, event→email; with multi-tenancy, `TenancyTests`: stamping, filtering, the aggregate-root convention, outbox tenant restore) · `ApiTests` (E2E lifecycle + error contracts; `CommitFailureTests`: a commit conflict is a 409 and a throwing commit an opaque 500, both releasing the key; `IdempotencyPostCommitTests`: a disconnect after the commit keeps the key; `InvoiceReadCacheTests`: every write evicts the cached invoice and retires its ETag, a 404 is never cached; `InvoiceContractTests`: 304 for every matching `If-None-Match` form, paging bounds answer 400 naming the field, `Location` resolves; `OpenApiSnapshotTests` + `OpenApiDocumentTests`: the committed document is the served one and says all of this; with multi-tenancy, `TenantIsolationTests`: cross-tenant read/write 404, scoped overdue list, header ignored, cache scoped — all-options leg) · `ApiTests/Ui` (the forms' validator mirrors and due-date floor, `errors[]` placed under their inputs, success notifications).

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
