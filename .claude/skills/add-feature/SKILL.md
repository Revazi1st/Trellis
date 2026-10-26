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

## First: write the feature doc

- [ ] Before any code, draft `docs/features/{feature}.md` from the business description; it becomes
      the feature doc CI requires anyway [`check-docs-drift.sh`]. Record only the business content,
      shaped like `invoices.md`:
      - the domain in plain words, its terms turned into the types you will write (aggregate, child
        entities, value objects);
      - the lifecycle: states, transitions, the rule each one checks and the error code each refusal
        returns, with a `stateDiagram-v2` when there is more than one state;
      - per operation: the permission, and each error code with its status;
      - the endpoints table, which reads are cached, the domain events with their wire names and the
        handlers that react, and the tests per project.
- [ ] Check the statuses against the template's defaults before you build: 400 for input or a rule
      the request would break, 404 for what this caller cannot see, 409 for a state that forbids the
      transition or a concurrent write, 412 for a stale `If-Match`, 422 only for
      `Idempotency.FingerprintMismatch` (never for validation); the chooser is in
      `.claude/rules/domain.md`. Permissions, idempotency, paging bounds and the code shape are
      enforced by tests below, so the doc does not restate them.

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
      writes one line per use case) [`ConventionTests`, `DecoratorPipelineTests`]. A value-object or
      aggregate-rule error is attributed to its request field with `.AtField(nameof(command.X))`,
      so the 400 names the input (`CreateInvoiceCommandHandler` is the model).
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
      with a unit, and each new name, unit and `[LoggerMessage]` event id gets its row in
      `docs/deployment.md`'s *Telemetry reference* [`TelemetryNamingTests`, both ways]; nothing to
      register in a host.
- [ ] Options: a POCO with DataAnnotations, bound with `AddOptions<T>().Bind(...)` and validated at
      boot [`OptionsConventionTests`].

## Persistence (`src/Infrastructure/Trellis.Persistence/`) — new aggregate only

- [ ] `Configurations/{Aggregate}Configuration`: value objects as `ComplexProperty`, children
      `AutoInclude()` + field access. With multi-tenancy, add **no** tenant column or filter: every
      `AggregateRoot<>` gets them by convention [`TenancyTests`, all-options leg].
- [ ] A concurrency token on the root: map `xmin` as `InvoiceConfiguration` does (a `uint Version`
      with a private setter). A change to children alone still writes the root row and bumps it
      (`AggregateConcurrencyInterceptor`), so the token guards the whole aggregate
      [`AggregateConcurrencyTests`].
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
- [ ] `.RequirePermission(Permissions.X.Y)` on every endpoint, with its row in
      `EndpointMetadataTests.ExpectedPermissions` (and in the wire-level theory beside it);
      `.AllowAnonymous()` only together with an entry in `EndpointMetadataTests.AnonymousAllowlist`
      [`EndpointMetadataTests`].
- [ ] `.RequireIdempotency()` on every non-GET endpoint outside `auth/` [`EndpointMetadataTests`];
      anything the endpoint does after the handler succeeded uses `CancellationToken.None`.
- [ ] `.WithTags(...)`, `.WithSummary(...)` **and** `.WithDescription(...)` [`EndpointMetadataTests`,
      `OpenApiDocumentTests`]. Problem responses, `Location` and the conditional-GET members are added
      by shape (ADR-0025). A Result-produced status the shape cannot infer (an anonymous endpoint's
      401/403/409) is declared with a bare `.Produces(status)`, which the transformer gives the problem
      body; never `ProducesProblem` or a typed `Produces<T>(4xx)` [`OpenApiDocumentTests`].
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
- [ ] A page in `Pages/` that inherits `ProtectedPage` (overriding `RequiredPermission` when a permission
      gates the whole page), with a `<PageTitle>`, one `h1`, and a `class="back"` link to its list above
      the heading when it sits below one, there whatever the load state (`.claude/rules/ui.md`).
- [ ] The nav link in `Layout/MainLayout.razor` (`class="nav-link"`, inside the `AuthorizeView` its
      permission needs); the contracts namespace in `_Imports.razor`; the typed client registered in
      `AddSharedUi` (`DependencyInjection.cs`).
- [ ] Forms over a model in `Forms/` whose DataAnnotations mirror the command validator, with
      `<ServerValidation>`; success is an `INotificationService` notification (`.claude/rules/ui.md`)
      [`InvoiceFormMirrorTests` is the model].
- [ ] Built from the design system's documented components (`docs/design-system.md`, *Components*): no
      colour, `style=` attribute, `<style>` block or `.razor.css` in a page; a new class gets its rule in
      `app.css` and its subsection in the doc; a new colour is a `light-dark()` pair with its `--on-*`
      [`DesignSystemTests`].
- [ ] A render test over `PageHost` in `tests/Trellis.ApiTests/Ui/` for what the page shows in each load
      state [`InvoicePagesTests` is the model], and the page's sentence in the feature doc's *UI* section
      [`check-docs-drift.sh`].
- [ ] Run the `ui-reviewer` subagent on the change before finishing; its findings are claims to check
      (AGENTS.md).

## Tests and docs

- [ ] Domain.UnitTests for the invariants, with the two lifecycle theories `InvoiceTests` gives every
      transition (`Failed_transitions_raise_no_domain_events`,
      `Every_transition_on_a_voided_invoice_is_rejected`) copied for a new aggregate;
      Application.UnitTests for the handlers, IntegrationTests
      against PostgreSQL for persistence and specifications, ApiTests for the HTTP contract of a
      notable flow (the fitness suites above already cover every endpoint). `docs/development.md`
      (*Tests*) maps what each project proves and where to invest.
- [ ] Test support beside the Invoice one (`.claude/rules/tests.md`, ADR-0009 amendment): a
      `{Feature}Factory` in `tests/TestSupport/{Feature}/` (namespace `Trellis.TestSupport.{Feature}`:
      valid commands and aggregates, an optional parameter per value), linked into
      Application.UnitTests and IntegrationTests like `InvoiceFactory`, by
      `<Compile Include="..\TestSupport\{Feature}\{Feature}Factory.cs" Link="TestSupport\{Feature}\{Feature}Factory.cs" />`
      in each csproj; an IntegrationTests `{Feature}Driver` (`IServiceProvider` extensions, one per use
      case, each running the real handler and returning the id) and an ApiTests `{Feature}HttpDriver`
      (`HttpClient` extensions, the create over `PostWithIdempotencyKeyAsync`). Arrange-only, no generic
      `SendAsync`, never in a toggle-conditional file.
- [ ] Handler tests in `Application.UnitTests/Features/{Feature}/{UseCase}/{UseCase}CommandHandlerTests.cs`
      (namespaces follow the folders), and the validators' bounds in
      `Features/{Feature}/{Feature}CommandValidatorTests.cs`: the maximum accepted and one more rejected
      naming the property, at the Domain constant (`InvoiceCommandValidatorTests` is the model). Every
      arrange step ends in `ShouldBeSuccess()` [RSLT001 fails the Release build on a dropped `Result`,
      an awaited one included, in tests too; a deliberate drop is `_ = …` with a reason].
- [ ] **`docs/features/{feature}.md`** (kebab-case: `ExchangeRates` → `exchange-rates.md`), shaped
      like `invoices.md`, plus a line in `docs/INDEX.md` [`check-docs-drift.sh`: a change under the
      feature's folders without its doc fails CI]. A load-bearing decision gets an ADR (the `new-adr`
      skill).
- [ ] The new endpoints' requests in `src/Presentation/Api/Trellis.Api/Trellis.Api.http`, and the
      feature doc opening like `invoices.md`: the domain in plain words, a state diagram when the
      aggregate has a lifecycle, and a *Try it* table matching those requests.
- [ ] `dotnet build Trellis.slnx -c Release && dotnet test Trellis.slnx` (or `./scripts/dotnet.sh …`
      without a local SDK), then `dotnet format --verify-no-changes`.
- [ ] Run the reviewers the change needs before finishing (AGENTS.md, *Boundaries*): `code-reviewer`;
      `architecture-guard` when a project or a package was added; `test-reviewer` for the new tests;
      `security-reviewer` when a permission, an allowlist or who may call an endpoint changed. Their
      findings are claims to check against the code.

## Not shown by the Invoices slice: decide deliberately

The reference slice is one aggregate. These shapes are not in it; each has a home to start from:

- **Records only their owner may see**: `docs/features/auth.md`, *Protecting an endpoint* (a domain
  `OwnerId`, scoped in the specification, the read cache keyed by owner or skipped).
- **A command that changes several aggregates**, and reactions in another aggregate:
  `.claude/rules/application.md` (one aggregate per command; tracked loads, check everything, then
  mutate).
- **A business key that must be unique** (a code, an e-mail): a unique index whose constraint name is
  pinned and registered in `UniqueConstraintErrors`, raced in a test (`.claude/rules/persistence.md`).
- **Work on a timer**: `docs/architecture.md`, *Scheduled use cases*.
- **Telling another system**: an integration event from a translate-and-send handler,
  `docs/architecture.md`; being told by one (a webhook): the `add-integration` skill, step 7.
- **An external identity provider**: `docs/features/auth.md`, *Adding an external identity provider*.
- **Keyset paging.** The slice pages by offset (`?page=&pageSize=`, capped at page 10000), which is
  right for a bounded list a person pages through. A list that grows without bound, or feeds an
  endless scroll, pages by keyset instead: order by the sort key and then `Id` (the same total order
  every paged specification ends with), select the rows after the last one the client saw on that
  pair, take one more than the page size to know whether another page follows, and return the last
  pair as an opaque cursor the client sends back. There is no total count and no jumping to page 40,
  and the response is not the `PagedResponse<T>` envelope, so a cursor envelope is a contract
  decision for an ADR.
