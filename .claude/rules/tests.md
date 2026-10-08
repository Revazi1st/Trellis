---
description: Testing conventions
paths: ["tests/**/*.cs"]
---

# Testing rules

- **xUnit v3** + **Shouldly** (`value.ShouldBe(...)`). Mocks: **NSubstitute**. Never FluentAssertions or Moq.
- Test names: `Method_Scenario_Outcome`.
- **Domain.UnitTests**: pure, no mocks. **Application.UnitTests**: handlers/decorators with faked ports (`Substitute.For<...>`), `FakeTimeProvider` for time.
- **IntegrationTests**: real PostgreSQL via `IntegrationTestBase` (Testcontainers + Respawn) — exercise the real Application pipeline against a real DB. **Do not mock EF or the DbContext.** Includes the migration-drift guard (`HasPendingModelChanges` — a model change without a migration fails here) and the domain-event serialization fitness test.
- **ArchitectureTests**: `LayerDependencyTests` for boundaries — add a rule when you introduce one. `ConventionTests` enforce the rules files (sealed non-public handlers, `internal sealed` endpoints, value-object `Create → Result<T>` shape, aggregate encapsulation, sealed-record events) — a new convention gets a test, not just prose. `NamingConventionTests` cover namespace-mirrors-folder, the `Async` suffix (SharedKernel is exempt: `Handle`/`Bind`/`Map` are named after their patterns) and role suffixes.
- **ApiTests/InProcess**: `ApiFactory` (`WebApplicationFactory` + Testcontainers PostgreSQL) runs the **real HTTP pipeline** — JWT 401/403, permission policies, idempotency reserve/duplicate/release, the auth rate limit, RFC 9457 shape — inside the SDK container on every `test` run. Aspire E2E stays opt-in (`RUN_ASPIRE_E2E=1`); contract-serialization guards are always on. The fixture contract (ADR-0021):
  - Every `CreateClient()` calls from its own simulated address (`ApiFactory.ClientAddressHeader`, applied by an outermost startup filter), so each client is its own anonymous rate-limit partition and no test spends another's `auth` budget. Model many users behind one host with `CreateClientFrom(address)`.
  - Fixtures state `Registration:Mode=Open`, and a host setting cannot be unset; a test about shipped defaults boots a plain `WebApplicationFactory<Program>` instead (`AuthFlowTests.Register_WithTheShippedProductionConfiguration_IsClosed`).
  - Kestrel-only behaviour (request-size limits) runs on a real listener: `RequestLimitTests.KestrelApiFactory` (in `EndpointMetadataTests.cs`) calls `UseKestrel(0)`, because TestServer does not enforce Kestrel limits.
  - `AuthFlowTests.Factory` swaps in observers instead of mocks: an adjustable clock (token expiry), a counting password hasher (timing oracle), a capturing `ILoggerProvider` (security events).
- **ApiTests/Ui** (absent with `--include-ui false`): UI.Common's clients and token provider over a scripted handler and a manual clock (`UiHarness`); every file sits behind `#if (IncludeUI)` and the csproj references UI.Common under the same guard.
- **OpenAPI**: `OpenApiDocumentTests` gate the committed `docs/api/openapi.v1.json` — every operation needs `.WithSummary()` **and** `.WithDescription()`, and health probes must stay out of the document. The CI contract-diff step catches *changed* wire shape; these catch a *new* endpoint shipped undocumented. Regenerate with `scripts/export-openapi.sh` after adding an endpoint.
- Use `TestContext.Current.CancellationToken` for cancellable calls.
