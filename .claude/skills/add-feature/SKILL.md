---
name: add-feature
description: Scaffold a new vertical-slice feature (aggregate or use case) by copying the Invoices reference slice end-to-end. Use when adding a new domain concept or use case to Trellis.
disable-model-invocation: false
---

# Add a feature

The `Invoice` slice is the canonical pattern. To add a feature, mirror it across the layers. Read `docs/features/invoices.md` first, then the files it lists.

## For a new use case on an existing aggregate
1. **Application** — create `Features/{Feature}/{UseCase}/`:
   - `{UseCase}Command.cs` (or `Query`) implementing `ICommand`/`ICommand<T>`/`IQuery<T>`.
   - `{UseCase}CommandHandler.cs` — `internal sealed`, returns `Result`/`Result<T>`, orchestrates the aggregate + ports.
   - `{UseCase}CommandValidator.cs` — `internal sealed : AbstractValidator<...>` (shape checks only).
   Registration is automatic (Scrutor).
2. **Api** — `Features/{Feature}/{UseCase}.cs` implementing `IEndpoint`; bind an `Api.Contracts` record, map `Result` via `CustomResults.Problem`.
   - **Secure by default** (ADR-0021): the versioned group already authenticates every endpoint, and `EndpointMetadataTests` fails the build unless every non-anonymous endpoint has `.RequirePermission(...)` (only an endpoint on the caller's own sessions may be signed-in-only, listed in `AuthenticatedOnly`), every non-GET endpoint outside `auth/` has `.RequireIdempotency()`, every operation has `.WithSummary(...)` and `.WithDescription(...)`, and every `.AllowAnonymous()` has a matching `AnonymousAllowlist` entry. Under `--use-multitenancy` the tenant comes from the signed `tenant` claim only (ADR-0022): never put a tenant on a contract, route, query or header.
3. **Contracts** — add request/response records to `Api.Contracts/{Feature}/`.
4. **Tests** — Application.UnitTests for the handler; extend ApiTests if it's a notable flow.

## For a new aggregate
Also add: the aggregate + value objects + strongly-typed ID(s) + events + `{Aggregate}Errors` + `{Aggregate}Specs` in `Domain/{Feature}/`; an `IEntityTypeConfiguration<T>` + repository + `ConfigureConventions` ID line in Persistence (with multi-tenancy, tenant scoping is automatic for any `AggregateRoot<>` — do not add it to the configuration); then `./scripts/ef.sh migrations add Add{Feature}` (see the `add-migration` skill); Domain.UnitTests + IntegrationTests.

## Finish
- `./scripts/dotnet.sh build Trellis.slnx && ./scripts/dotnet.sh test Trellis.slnx`
- Add a permission constant to `Permissions` (and to `Permissions.All`) for the endpoint's `.RequirePermission(...)`, and grant it to a role in `Roles.Permissions`; the MigrationService and the test hosts seed from that map, never by hand.
- **Update `docs/features/{feature}.md`** (copy the invoices doc's shape).
