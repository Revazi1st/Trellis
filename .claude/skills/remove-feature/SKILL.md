---
name: remove-feature
description: Delete the Invoices reference slice from a generated project. Use when the sample feature has served its purpose and you want a clean solution without it.
disable-model-invocation: false
---

# Remove the Invoices reference slice

Invoices is scaffolding — every real project deletes it. Two rules first:

- **Add your own first feature before removing this one** (`add-feature` skill). Several infrastructure tests use `Invoice` as their fixture aggregate; port them to your aggregate instead of deleting the coverage.
- **Delete from the Domain outward.** Removing `Domain/Invoices` first turns every consumer into a compile error, so the compiler dictates each following step. Starting at the UI leaves dead inner code compiling silently.

After each step `./scripts/dotnet.sh build Trellis.slnx` — the error frontier should move outward; nothing disappears quietly.

## 1. Domain
Delete `src/Core/Trellis.Domain/Invoices/` (aggregate, line entity, IDs, events, errors, specs, `IInvoiceRepository`). `Domain/Common` is shared, not invoice-owned: `EmailAddress` is used by Auth (`RegisterCommandValidator`) — keep it. `Money` + `Currency` have no other consumer today — delete them (and `MoneyTests`) unless your features want them.

## 2. Application
- Delete `Features/Invoices/` (7 use-case slices + `EventHandlers/` + `InvoiceResponse.cs`) and `Observability/InvoiceMetrics.cs`.
- `DependencyInjection.cs`: drop `AddSingleton<InvoiceMetrics>()`.
- `Authorization/Permissions.cs`: remove `Permissions.Invoices`, its two entries in `Permissions.All`, and the `Member` grant in `Roles.Permissions` — replace with your own permissions rather than leaving Member empty.

## 3. Persistence
- Delete `Configurations/InvoiceConfiguration.cs`, `Configurations/InvoiceLineConfiguration.cs`, `Repositories/InvoiceRepository.cs`.
- `AppDbContext.cs`: the `Invoices` DbSet and the two `InvoiceId`/`InvoiceLineId` lines in `ConfigureConventions` (with multi-tenancy, the `"Tenant"` filter is a convention over every aggregate root — nothing per-aggregate to re-target).
- `DependencyInjection.cs`: the `IInvoiceRepository` registration.
- `Outbox/DomainEventSerialization.cs` anchors the Domain assembly via `typeof(Invoice)` — re-anchor to any surviving Domain type.

## 4. Api
- Delete `Api/Features/Invoices/` (7 endpoints) and `Api.Contracts/Invoices/`.
- `Program.cs`: no telemetry line to remove — ServiceDefaults subscribes every `Trellis.*` meter, so deleting `InvoiceMetrics` is enough.
- `Api.Common/Caching/CacheKeys.cs`: replace the `Invoice(id)` method with keys for your feature — keep the class, it is the tenant-scoping seam.

## 5. UI (if included)
- Delete `Pages/Invoices.razor`, `Pages/InvoiceDetail.razor`, `Pages/CreateInvoice.razor`, `Services/IInvoiceApiClient.cs`, `Services/InvoiceApiClient.cs`.
- `DependencyInjection.cs`: the `IInvoiceApiClient` registration. `Layout/MainLayout.razor`: the Invoices nav link. `_Imports.razor`: `@using Trellis.Api.Contracts.Invoices`.
- `Invoices.razor` owned the home route `/` — give the UI a new home page or login redirects into a 404.

## 6. MigrationService
Delete `SeedSampleDataAsync` and its call in `MigrationWorker.cs` (plus dead usings). Role/permission seeding reads `Roles.Permissions` and adapts by itself.

## 7. Tests — two kinds
- **Invoice-specific → delete**: `Domain.UnitTests/Invoices/`, `Application.UnitTests/Invoices/`, `IntegrationTests/InvoicePersistenceTests.cs`, `ApiTests/InvoiceApiFlowTests.cs` (the Aspire E2E — write one for your flow), and the UiTests flows that drive the invoice pages (the login test asserts the Invoices title).
- **Invoice-as-fixture → port to your aggregate, don't delete the coverage**: `OutboxTests`, `AuditingInterceptorTests`, `SoftDeleteInterceptorTests`, `ConcurrencyTests`, `PaginationTests`, `TenancyTests`, `DomainEventSerializationTests` (also anchors the Domain assembly on `Invoice`), `DecoratorPipelineTests` (handler discovery is reflection-based but the sample inputs are invoice commands), `ApiTests/InProcess/EndpointContractTests` (401/403, RFC 9457, idempotency all ride invoice endpoints), `ContractSerializationTests`.
- `ArchitectureTests/ArchitectureRules.cs`: `DomainAssembly`/`ApiContractsAssembly` anchor on `Invoice`/`InvoiceDto` — re-anchor.

## 8. Docs
Delete `docs/features/invoices.md`; fix the mentions of the slice as the reference pattern in `AGENTS.md`, `.claude/rules/` and `.claude/skills/add-feature/`. A final `grep -ri invoice` catches stragglers (XML doc comments citing it as an example may simply be reworded).

## 9. Migration + finish
Delete `src/Infrastructure/Trellis.Persistence/Migrations/` and regenerate the single initial migration (pre-release convention, see `add-migration`):
```bash
./scripts/ef.sh migrations add InitialCreate --project src/Infrastructure/Trellis.Persistence
./scripts/dotnet.sh build Trellis.slnx && ./scripts/dotnet.sh test Trellis.slnx
```
The migration-drift test (`HasPendingModelChanges`) fails if the snapshot no longer matches the model.
