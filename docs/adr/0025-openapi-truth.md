# ADR-0025: OpenAPI truth — one generated document per version, built by transformers

- **Status**: accepted
- **Date**: 2026-10-08
- **Refines**: ADR-0013 (the committed contract artifact) and ADR-0024 (the problem shape the document now declares).

## Context

`docs/api/openapi.v1.json` is the artifact consumers generate clients from, and the audit found it was
not the contract the API serves:

1. Every path read `/api/v{version}/…` with no `version` parameter declared, and `servers` pinned
   `http://localhost:5171/`. `Microsoft.AspNetCore.OpenApi` renders `ApiDescription.RelativePath`
   literally; substituting the URL-segment version is the job of `Asp.Versioning`'s API-explorer
   package, which this minimal-API host does not reference.
2. Eleven of twelve operations had no `operationId` (only `GetInvoice` carried a hand-written
   `.WithName`), so a generated client got `postApiV1InvoicesIdLines`-style names.
3. No operation documented a 400, a 404, the 201's `Location`, the conditional GET's
   `If-None-Match`/`ETag`/304 or any problem schema; the 401/403/409 entries that existed had no body.
4. The paging parameters were PascalCase `Page`/`PageSize` with no bounds; `InvoiceDto.Status` was a free
   string, `Version` a `uint32` and money `format: double`.
5. `GetInvoice` compared `If-None-Match` with the exact string it had emitted: the strong form, a list
   and `*` never produced a 304.
6. The only gate was a CI job that boots the app, curls the document through `python3 -m json.tool` and
   diffs — nothing ran inside `dotnet test`, and the committed format was not the server's.

## Decision

### One document per API version, with the packages already referenced

`ApiVersions` (Api.Common) is the single source for the route group's version set, the default version,
the document name (`v1` → `/openapi/v1.json`) and the path segment the document declares.
`AddApiDocumentation` calls `AddVersionedOpenApiDocument(ApiVersions.V1)`, which registers
`AddOpenApi("v1")` with:

- `ShouldInclude` = the endpoint's `ApiVersionMetadata` is mapped to that version — operations of
  another version, probes and the reference UI are excluded by construction;
- `VersionedDocumentTransformer`: substitutes `{version}` in every path with the concrete segment
  (`/api/v1/…`) and drops `servers` — a committed contract must not pin a host;
- `SecuritySchemeTransformer`, `ProblemComponentsDocumentTransformer` (below), the schema transformers
  (`StronglyTypedIdSchemaTransformer`, `DecimalSchemaTransformer`, `StringEnumSchemaTransformer`) and the
  operation transformers (`SecurityRequirementOperationTransformer`, `IdempotencyOperationTransformer`,
  then `ProblemResponsesOperationTransformer`, last because it completes the entries of the two before it).

Adding a v2 is `V2` in `ApiVersions`, `HasApiVersion(V2)` on the group and one more
`AddVersionedOpenApiDocument(V2)`.

**Not `Asp.Versioning.OpenApi`.** It is MIT and first-party, but it needs package approval, it depends on
`Asp.Versioning.Mvc.ApiExplorer` → `Asp.Versioning.Mvc` (the MVC versioning stack in a host that is
deliberately minimal-API only), and the value it adds beyond the substitution — deprecation and sunset
notices rendered into the document, `WithDocumentPerVersion()` — is for an API lifecycle this template has
not started (one version, no deprecation policy). The substitution is twenty-five lines over packages
already in the graph. **Swap recipe** if the lifecycle features are wanted later: add the package, delete
`VersionedDocumentTransformer` and the `ShouldInclude` line in `AddVersionedOpenApiDocument`, call
`services.AddApiVersioning(...).AddOpenApi()` and `app.MapOpenApi().WithDocumentPerVersion()`, regenerate
the snapshot and append a supersession line here. Every other transformer, the route naming and the gate
stay as they are.

### `operationId` is the endpoint class name, structurally

`IEndpoint.Map` returns the `RouteHandlerBuilder`, and `MapEndpoints` finishes every endpoint with
`.WithName(endpointType.Name)`. The route name is what `CreatedAtRoute` and link generation resolve and
what the generator emits as `operationId`, so an endpoint cannot ship without one and the id is always the
class name (`CreateInvoice`, `Login`). Endpoints never call `.WithName` themselves and refer to another
route with `nameof(GetInvoice)`. `OpenApiDocumentTests` still asserts uniqueness on the artifact itself,
because the artifact is what consumers get.

### Error responses by shape, from the record the clients deserialise into

`ProblemComponentsDocumentTransformer` publishes `components.schemas.ProblemResponse` (whose `errors[]`
items reference `FieldError`) generated from `Api.Contracts.Common.ProblemResponse`/`FieldError` — the
same records the UI reads (ADR-0024); it requires only `type`, `title`, `status` and `code`, the members every
problem carries (`errors` is absent outside a validation 400, `detail` on a bare 404/405).
`ProblemResponsesOperationTransformer` then documents, per
operation and from its shape: a 400 wherever there is input to reject (a body, a route id, a query
parameter), a 404 wherever a route id can miss, a 409 on a write to an addressed resource (state rule or
concurrency; the idempotency 409 keeps its own sentence and gains this one), 429 and 500 everywhere, and
gives every 4xx/5xx — including the 401/403 and idempotency entries earlier transformers add — the
`application/problem+json` body by reference. A 201 declares `Location`. Responses are emitted in status
order so a diff reads.

The shape cannot see every Result an endpoint returns. An anonymous endpoint has no security requirement
and no route id, so the 401/403/409 its handler produces through `CustomResults.Problem` (login's
`Auth.InvalidCredentials`/`Auth.AccountLockedOut`, register's `Auth.RegistrationClosed`/`Auth.EmailAlreadyInUse`,
refresh's `Auth.InvalidRefreshToken`) are declared on the endpoint with a bare, typeless
`.Produces(status)`; the transformer fills the empty content the generator leaves with the same
`ProblemResponse` reference. Never `ProducesProblem` (it inlines the framework's `ProblemDetails`, not the
Contracts record) or a typed `Produces<T>(4xx)`, and never a status the shape already adds.
`OpenApiDocumentTests.Anonymous_auth_operations_declare_their_result_statuses` pins the auth ones.

### Conditional GET is declared and honoured as RFC 9110 says

`.SupportsConditionalGet()` puts `ConditionalGetMetadata` on a GET; the transformer then declares the
`If-None-Match` header parameter, the `ETag` header on the 200 and the 304. The handler evaluates the
header with `HttpRequest.IsNotModified(etag)`: `EntityTagHeaderValue.TryParseList` plus a weak comparison,
so the weak form, the strong form, a list and `*` all match, and an absent or unparsable header serves the
full response.

### Schema truth

- Money: `DecimalSchemaTransformer` sets `format: decimal` on every `decimal`/`decimal?` (what Kiota and
  NSwag map back to `System.Decimal`). The `number | string` type unions stay: they truthfully reflect that
  the host reads numbers from strings (`JsonSerializerDefaults.Web`); making the API strict would be a wire
  change for another decision.
- Status: `InvoiceDto.Status` is `Api.Contracts.Invoices.InvoiceStatus`, serialised by name
  (`JsonStringEnumConverter<T>`); `StringEnumSchemaTransformer` pins `type: string` next to the `enum`
  values the generator emits. Contracts may not reference the Domain (ADR-0013), so the enum is a
  mirror, mapped by name at the API seam; `ContractMirrorTests` (ArchitectureTests) keeps it equal to
  the domain enum member for member.
- Version: `long` (`format: int64`); it is the row version behind the weak ETag, opaque to clients.
- Paging: `PageRequest` is gone. List endpoints bind `page`/`pageSize` inline with
  `[Range(PagingLimits.…)]` (1..10000, 1..100, default 20), which is how the document gets camelCase
  names, bounds and defaults; the query's validator enforces the same bounds (a value outside answers 400
  naming the field), and the repository pages with checked arithmetic behind `ThrowIfLessThan` guards, so
  no offset can wrap negative. The page cap bounds deep `OFFSET` scans.

### The committed format and the gate

The committed file is the server's own serialisation (`/openapi/v1.json` as served, plus the final
newline every text file in the repository has) — no reformatting tool in between.
`OpenApiSnapshotTests` fetches the document from the in-process host on every `dotnet test` run,
inside the SDK container too, and fails with the first differing JSON pointer when it does not match the
committed file; `UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/Trellis.ApiTests` (forwarded by
`scripts/dotnet.sh`) or `scripts/export-openapi.sh` against a running API regenerates it. The CI step
that boots the app and diffs stays as the independent check of the same bytes. `OpenApiDocumentTests`
gate the content (prose on every operation, probes out, unique `operationId`s, no `{version}`/`servers`,
the problem schema on every error, 400/404 by shape, the anonymous auth statuses, `Location`,
conditional-GET members, paging bounds, typed status/money/version), and `InvoiceContractTests` proves
the same promises on the wire.

## Consequences

- A new endpoint is documented by construction: `Map` returns the builder, the name and the error
  responses follow from the class and the shape; the author adds a summary and a description, declares
  `.SupportsConditionalGet()` where it applies, regenerates the snapshot and commits the diff.
- A generated client gets typed status, `decimal` money, `long` versions, a `ProblemResponse` type for
  every error, correct method names and no `{version}` to fill in.
- The document is a gate in every test run, not only in one CI job; a wire change cannot land without its
  regenerated artifact in the same commit.
- Known limits: a strongly-typed id in a route is documented as `type: string` without `format: uuid`
  (parameter schemas do not pass through the schema transformer; a follow-up), and the deprecation /
  sunset lifecycle is not modelled — the swap recipe above is the path when it is.
