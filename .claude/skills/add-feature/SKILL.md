---
name: add-feature
description: Scaffold a new vertical-slice feature (aggregate or use case) by copying the Invoices reference slice end-to-end. Use when adding a new domain concept or use case to Trellis.
disable-model-invocation: false
---

# Add a feature

The `Invoice` slice is the canonical pattern: read `docs/features/invoices.md`, then mirror the files
it lists. This skill is a checklist, and every box below is either enforced by a test (named in
brackets, so you know what fails when you skip it) or by review. Work from the Domain outward; build
after each layer.

## Domain (`src/Core/Trellis.Domain/{Feature}/`) — new aggregate only

- [ ] Aggregate derives from `AggregateRoot<TId>`: a `static Create(...) → Result<T>` factory, a
      `private` parameterless constructor for EF, no public constructors or setters; state changes
      return `Result` and `Raise(...)` events [`ConventionTests`].
- [ ] Strongly-typed ID `readonly record struct XId(Guid Value) : IStronglyTypedId<XId>, IParsable<XId>`
      with `New() => new(Guid.CreateVersion7())`.
- [ ] Value objects: `sealed record` with `static Create(...) → Result<T>` [`ConventionTests`].
- [ ] `{Aggregate}Errors`: one `Error` per rule, codes `Area.Problem` (PascalCase, one dot), never
      reused for another meaning once shipped (clients branch on them, ADR-0024).
- [ ] Domain events: `sealed record` with `[DomainEventName("feature.event.v1")]` and members that
      are primitives, `Guid`, `DateTimeOffset` or strongly-typed IDs [`ConventionTests`,
      `DomainEventSerializationTests`].
- [ ] `{Aggregate}Specs`: one static factory per query shape; every ordering ends with
      `OrderBy(x => x.Id)` [`SpecificationPagingTests`, `ListPagedAsync` refuses one without it].
- [ ] Repository port `I{Aggregate}Repository` next to the aggregate.

## Application (`src/Core/Trellis.Application/Features/{Feature}/{UseCase}/`)

- [ ] `{UseCase}Command` / `Query` implementing `ICommand`, `ICommand<T>` or `IQuery<T>`.
- [ ] `{UseCase}CommandHandler` / `QueryHandler`: `internal sealed`, returns `Result`/`Result<T>`,
      orchestrates the aggregate and ports, holds no business rule, logs no outcome (the pipeline
      writes one line per use case) [`ConventionTests`, `DecoratorPipelineTests`].
- [ ] `{UseCase}CommandValidator`: `internal sealed : AbstractValidator<T>`, shape checks only
      (`NotEmpty` and a `MaximumLength` on every string); a `.Must(...)` rule carries
      `.WithErrorCode("Area.Problem")`. A command with a string property must have one
      [`CommandValidatorConventionTests`, `NamingConventionTests` (the `Validator` suffix)].
      Registration is automatic (Scrutor).
- [ ] A domain-event reaction goes in `Features/{Feature}/EventHandlers/`: `IDomainEventHandler<TEvent>`
      with a unique `[DomainEventHandlerName("feature.event.reaction")]` — the key the outbox credits
      it under, never changed while messages are in flight [`ConventionTests`]. Writes go through
      the unit of work (the outbox commits them), external side effects after them.
- [ ] Permissions: a constant in `Authorization/Permissions.cs`, listed in `Permissions.All`, granted
      to a role in `Roles.Permissions`. The MigrationService and the test hosts seed from that map;
      never grant one by hand.
- [ ] A meter or `ActivitySource` is named `Trellis.<Area>`, instruments `trellis.<area>.<name>`
      with a unit [`TelemetryNamingTests`]; nothing to register in a host.
- [ ] Options: a POCO with DataAnnotations, bound with `AddOptions<T>().Bind(...)` and validated at
      boot [`OptionsConventionTests`].

## Persistence (`src/Infrastructure/Trellis.Persistence/`) — new aggregate only

- [ ] `Configurations/{Aggregate}Configuration`: value objects as `ComplexProperty`, children
      `AutoInclude()` + field access. With multi-tenancy, add **no** tenant column or filter: every
      `AggregateRoot<>` gets them by convention [`TenancyTests`, all-options leg].
- [ ] One line in `AppDbContext.ConfigureConventions` per new ID; a `DbSet`; the repository in
      `Repositories/` registered in `DependencyInjection.cs` [`NamingConventionTests` (the
      `Repository` suffix)].
- [ ] `./scripts/ef.sh migrations add Add{Feature} --project src/Infrastructure/Trellis.Persistence`
      (the `add-migration` skill) [`MigrationDriftTests`]. Never edit `Migrations/` by hand.

## Contracts and API

- [ ] Request/response records in `src/Presentation/Api/Trellis.Api.Contracts/{Feature}/`: no logic,
      no references [`LayerDependencyTests`]. A Contracts enum that mirrors a domain enum gets a
      `ContractMirrorTests` fact.
- [ ] One `internal sealed` endpoint class per use case in `Trellis.Api/Features/{Feature}/`,
      implementing `IEndpoint` (`Map` returns the `RouteHandlerBuilder`). `MapEndpoints` names the
      route after the class, which is the OpenAPI `operationId`: never call `.WithName` yourself
      [`ConventionTests`, `OpenApiDocumentTests`].
- [ ] `.RequirePermission(Permissions.X.Y)` on every endpoint; `.AllowAnonymous()` only together with
      an entry in `EndpointMetadataTests.AnonymousAllowlist` [`EndpointMetadataTests`].
- [ ] `.RequireIdempotency()` on every non-GET endpoint outside `auth/` [`EndpointMetadataTests`];
      anything the endpoint does after the handler succeeded uses `CancellationToken.None`.
- [ ] `.WithTags(...)`, `.WithSummary(...)` **and** `.WithDescription(...)` [`EndpointMetadataTests`,
      `OpenApiDocumentTests`]. Problem responses, `Location` and the conditional-GET members are added
      by shape (ADR-0025): never hand-write a `Produces(4xx)`.
- [ ] Map the `Result`: `result.IsSuccess ? TypedResults.X(...) : CustomResults.Problem(result.Error)`.
      A new HTTP status or problem `code` the API did not answer before gets a row in
      `ProblemDetailsContractTests`.
- [ ] Reads cached with `HybridCache` use a key from the injected `CacheKeys` service (add a method
      there; it carries the tenant under multi-tenancy); every write that changes the cached state
      evicts it with `CancellationToken.None` [`InvoiceReadCacheTests` is the model].
- [ ] Lists bind `page`/`pageSize` inline with `[Range(PagingLimits...)]` and return
      `PagedResponse<T>` [`OpenApiDocumentTests`]; a GET that honours `If-None-Match` declares
      `.SupportsConditionalGet()` and uses `HttpRequest.IsNotModified(etag)`.
- [ ] Multi-tenancy: the tenant comes from the signed `tenant` claim only (ADR-0022). Never put a
      tenant on a contract, a route, a query string or a header.
- [ ] **Regenerate the OpenAPI document**: `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/Trellis.ApiTests`
      (`./scripts/dotnet.sh` forwards the variable) and commit `docs/api/openapi.v1.json`
      [`OpenApiSnapshotTests` fails until you do].

## UI (if included)

- [ ] A typed client method over the contracts, through `ApiCall`; POSTs through the idempotent helper.
- [ ] Forms over a model in `Forms/` whose DataAnnotations mirror the command validator, with
      `<ServerValidation>`; success is an `INotificationService` notification; pages inherit
      `ProtectedPage` (`.claude/rules/ui.md`).

## Tests and docs

- [ ] Domain.UnitTests for the invariants, Application.UnitTests for the handlers, IntegrationTests
      against PostgreSQL for persistence and specifications, ApiTests for the HTTP contract of a
      notable flow (the fitness suites above already cover every endpoint).
- [ ] **`docs/features/{feature}.md`** (kebab-case: `ExchangeRates` → `exchange-rates.md`), shaped
      like `invoices.md`, plus a line in `docs/INDEX.md` [`check-docs-drift.sh`: a change under the
      feature's folders without its doc fails CI]. A load-bearing decision gets an ADR (the `new-adr`
      skill).
- [ ] `dotnet build Trellis.slnx -c Release && dotnet test Trellis.slnx` (or `./scripts/dotnet.sh …`
      without a local SDK), then `dotnet format --verify-no-changes`.
