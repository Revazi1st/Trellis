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

**Caching**: `GetInvoice` is cache-aside via `HybridCache` (key `CacheKeys.Invoice(id)`); the mutating endpoints (`AddInvoiceLine`, `IssueInvoice`, `PayInvoice`, `VoidInvoice`) invalidate that key on success. HybridCache uses an in-memory L1 always; a Redis L2 is added when the **`UseRedis`** template toggle is on (Aspire `cache` resource + `AddRedisDistributedCache`).

## UI (`src/Presentation/UI/XTemplate.UI.Common/Pages/`)

`Invoices` (list), `InvoiceDetail` (view + pay/void), `CreateInvoice` — via `IInvoiceApiClient` over the contracts.

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
