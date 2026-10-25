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
- Delete `Features/Invoices/` (8 use-case slices, `ListInvoices/` and `ListOverdueInvoices/` among them, + `EventHandlers/` + `InvoiceResponse.cs`) and `Observability/InvoiceMetrics.cs`, and the `Trellis.Invoices` rows (`trellis.invoices.issued`, `trellis.invoices.paid`, `trellis.invoice.currency`) from the *Telemetry reference* in `docs/deployment.md`: `TelemetryNamingTests` fails on a documented name the code no longer emits.
- `DependencyInjection.cs`: drop `AddSingleton<InvoiceMetrics>()`. Leave the pipeline's `TryDecorate` lines alone: Invoices holds the only queries (`GetInvoice`, `ListInvoices`, `ListOverdueInvoices`), and `TryDecorate`, unlike Scrutor's `Decorate` (which throws when it finds nothing to wrap), lets an assembly without a single query handler boot. Your first query gets the decorators by itself.
- `Authorization/Permissions.cs`: remove `Permissions.Invoices`, its two entries in `Permissions.All`, and the `Member` grant in `Roles.Permissions` — replace with your own permissions rather than leaving Member empty.

## 3. Persistence
- Delete `Configurations/InvoiceConfiguration.cs`, `Configurations/InvoiceLineConfiguration.cs`, `Repositories/InvoiceRepository.cs`.
- `AppDbContext.cs`: the `Invoices` DbSet and the two `InvoiceId`/`InvoiceLineId` lines in `ConfigureConventions` (with multi-tenancy, the `"Tenant"` filter is a convention over every aggregate root — nothing per-aggregate to re-target).
- `DependencyInjection.cs`: the `IInvoiceRepository` registration.
- `Outbox/DomainEventSerialization.cs` anchors the Domain assembly via `typeof(Invoice)` — re-anchor to any surviving Domain type.
- **A deployed database may still hold outbox rows for the invoice events** (`invoices.issued.v1`, `invoices.paid.v1`, `invoices.voided.v1`). Drain the outbox before deploying the removal (no pending rows of those types); a row left behind dead-letters as `unknown_type`, and `docs/deployment.md` ("Outbox operations") shows how to delete it.

## 4. Api
- Delete `Api/Features/Invoices/` (8 endpoints, `ListInvoices.cs` among them) and `Api.Contracts/Invoices/` (the DTOs and the `InvoiceStatus` wire enum).
- `Program.cs`: no telemetry line to remove — ServiceDefaults subscribes every `Trellis.*` meter, so deleting `InvoiceMetrics` is enough.
- `Trellis.Api.http`: delete the invoice requests (the list request, step 3, included) and keep the sign-in and register ones as the start of your own walkthrough.
- `Api.Common/Caching/CacheKeys.cs`: replace the `Invoice(id)` method with keys for your feature — keep the class, it is the tenant-scoping seam.
- **Regenerate the OpenAPI document**: `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/Trellis.ApiTests` (`./scripts/dotnet.sh` forwards the variable) and commit `docs/api/openapi.v1.json`. `OpenApiSnapshotTests` fails until the committed document drops the invoice operations and schemas.

## 5. UI (if included)
- Delete `Pages/Invoices.razor`, `Pages/InvoiceDetail.razor`, `Pages/CreateInvoice.razor`, `Services/IInvoiceApiClient.cs`, `Services/InvoiceApiClient.cs`. In `wwwroot/app.css` the `.filters`/`.filter` rules styled the invoice list's filter row and `.back` its back links: keep them for your own list and detail pages, or delete them.
- `DependencyInjection.cs`: the `IInvoiceApiClient` registration. `Layout/MainLayout.razor`: the Invoices nav link. `_Imports.razor`: `@using Trellis.Api.Contracts.Invoices`.
- `Invoices.razor` owned the home route `/` — give the UI a new home page or login redirects into a 404.

## 6. MigrationService
Delete `SeedSampleDataAsync`, its `Seeded` helper and its call in `MigrationWorker.cs` (plus dead usings). Role/permission seeding reads `Roles.Permissions` and adapts by itself.

## 7. Tests — two kinds
- **Invoice-specific → delete**: `Domain.UnitTests/Invoices/` (keep the `StronglyTypedIdParsingTests` cases worth porting to your IDs), `Application.UnitTests/Features/Invoices/` (the handler tests per use case and `InvoiceCommandValidatorTests`), `IntegrationTests/InvoicePersistenceTests.cs`, `ApiTests/InProcess/InvoiceContractTests.cs` and `InvoiceReadCacheTests.cs` (write the same two for your feature if it caches or answers conditional GETs), `ApiTests/InvoiceApiFlowTests.cs` (the Aspire E2E — write one for your flow in `AspireE2ECollection` with its `Category` trait, starting with `appHost.UseThrowawayDatabase()` so it never opens your persistent `aspire run` database; until then CI's "Aspire end-to-end" step finds no test and passes), `ArchitectureTests/ContractMirrorTests.cs`'s `InvoiceStatus` fact, `ApiTests/Ui/InvoiceFormTests.cs`, `ApiTests/Ui/InvoiceFormMirrorTests.cs` (the forms' lengths equal the Domain constants: write the same for your forms) and `ApiTests/Ui/InvoiceApiClientTests.cs` (the typed client sends the loaded version as `If-Match` and the list's status only when chosen: write the same if your client makes conditional writes or filtered reads), `ApiTests/Ui/InvoicePagesTests.cs` (the list's filter in the address, the back links: its `PageHost` renders any page of yours the same way), and the UiTests flows that drive the invoice pages (the login test asserts the Invoices title).
  - The Invoice test support, each piece after porting its shape to your aggregate (the fixture tests below arrange through it): `tests/TestSupport/Invoices/InvoiceFactory.cs` together with its `<Compile Include="..\TestSupport\Invoices\InvoiceFactory.cs" …/>` line in `tests/Trellis.Application.UnitTests/Trellis.Application.UnitTests.csproj` and `tests/Trellis.IntegrationTests/Trellis.IntegrationTests.csproj` (left behind, the dangling link breaks the build), `IntegrationTests/InvoiceDriver.cs` and `ApiTests/InvoiceHttpDriver.cs`.
  - Keep `tests/TestSupport/ResultAssertions.cs`, its csproj links, `IntegrationTests/TestCurrentUser.cs`, and `ApiTests/AppHostTestingExtensions.cs` (`UseThrowawayDatabase`) with its `ThrowawayDatabaseTests`: they are feature-free.
- **Invoice-as-fixture → port to your aggregate, don't delete the coverage**. These tests arrange through `InvoiceDriver`/`InvoiceFactory` (`ConcurrencyTests`, `AggregateConcurrencyTests`, `AuditingInterceptorTests`, `SoftDeleteInterceptorTests`, `PaginationTests`, `OutboxTests`, `OutboxRetryTests`, `SpecificationPagingTests`, `UtcConversionTests`, `TenancyTests`, and `DecoratorPipelineTests` in Application.UnitTests) and through `InvoiceHttpDriver` (`ProblemDetailsContractTests`, `IdempotencyPostCommitTests`, `IdempotencyReplayTests`, `TenantIsolationTests`, and `InvoiceReadCacheTests`, whose twin you write for a cached feature): have `<YourAggregate>Factory` and the two drivers in place first (the `add-feature` skill's test-support box writes them with your feature) and the ported tests follow.
  - Persistence and outbox: `OutboxTests`, `OutboxRetryTests` (its test handlers subscribe to `InvoicePaidDomainEvent`; `OutboxBackoffTests`/`OutboxOptionsTests` are invoice-free and stay), `AuditingInterceptorTests`, `SoftDeleteInterceptorTests`, `ConcurrencyTests`, `AggregateConcurrencyTests` (its model test is invoice-free; the behaviour tests add lines), `PaginationTests`, `SpecificationPagingTests`, `UtcConversionTests`, `TenancyTests`, `DomainEventSerializationTests` (also anchors the Domain assembly on `Invoice`).
  - Pipeline: `DecoratorPipelineTests`, `UseCaseOutcomeTests` and their `PipelineHarness` (handler discovery is reflection-based, but the sample inputs are invoice commands and queries).
  - Endpoint metadata: `EndpointMetadataTests` keeps its rules, but its rows name invoice routes: replace the eight `ExpectedPermissions` rows and the eight `InlineData` rows of `Each_endpoint_admits_its_one_permission_and_refuses_every_other` with one per endpoint of yours (the bodies it sends live in its `SendAsync` switch), and point the facts that call `invoices/overdue` or create an invoice (the security-header row, the permission-less token, the cache headers, the two 401 challenges, the algorithm pin) at an endpoint of yours with the same shape.
  - HTTP contract: `EndpointContractTests`, `ProblemDetailsContractTests`, `CommitFailureTests`, `IdempotencyReplayTests`, `IdempotencyPostCommitTests`, `TenantIsolationTests` (401/403, the problem shape, the unit-of-work failures and idempotency all ride invoice endpoints), `ContractSerializationTests`, and `OpenApiDocumentTests` (its status-enum, money, conditional-GET, conditional-write, `Location` and paging facts name invoice operations: retarget each to an operation of yours that has the shape, or delete the fact when none does).
  - UI (if included): `UiHarness` and the client tests that script invoice responses (`ApiCallTests`, `ApiResponsesTests`, `BearerTokenHandlerTests`, `NotificationServiceTests`, `ServerValidationTests`).
  - Domain kernel: `DomainEventsWithoutHandlersTests`, `SharedKernelPrimitiveTests`.
- `ArchitectureTests/ArchitectureRules.cs`: `DomainAssembly`/`ApiContractsAssembly` anchor on `Invoice`/`InvoiceDto` — re-anchor.
- `grep -rl Invoice tests` afterwards lists anything this step missed.

## 8. Docs
Delete `docs/features/invoices.md` and the paragraph in `README.md` that points newcomers to it; fix the mentions of the slice as the reference pattern in `AGENTS.md`, `.claude/rules/` and `.claude/skills/add-feature/`. A final `grep -ri invoice` catches stragglers (XML doc comments citing it as an example may simply be reworded).

## 9. Migration + finish
Delete `src/Infrastructure/Trellis.Persistence/Migrations/` and regenerate the single initial migration (pre-release convention, see `add-migration`):
```bash
./scripts/ef.sh migrations add InitialCreate --project src/Infrastructure/Trellis.Persistence
./scripts/dotnet.sh build Trellis.slnx && ./scripts/dotnet.sh test Trellis.slnx
```
The migration-drift test (`HasPendingModelChanges`) fails if the snapshot no longer matches the model.
