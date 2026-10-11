# Trellis

Trellis is a .NET 10 solution built around a rich domain model and Clean Architecture: vertical-slice
features inside layered projects, minimal APIs, hand-rolled CQRS that returns `Result`s instead of
throwing, EF Core on PostgreSQL, ASP.NET Core Identity with JWT and permission-based authorization,
and Aspire orchestration.

| Folder | What lives there |
|---|---|
| `src/Core` | SharedKernel, Domain, Application: the business rules and the use cases |
| `src/Infrastructure` | Infrastructure and Persistence, peer adapters that never reference each other |
| `src/Presentation/Api` | The API host, its shared plumbing and its public contracts |
<!--#if (IncludeUI) -->
| `src/Presentation/UI` | The Blazor web host and the component library it renders |
<!--#endif -->
| `src/Shared` | Aspire service defaults: telemetry, health checks, resilience |
| `tools` | The Aspire AppHost and the migration job |
| `tests` | Unit, architecture, integration (Testcontainers) and in-process API tests |

## Run it

You need Docker and the .NET 10 SDK; `aspire run` also needs the Aspire CLI
(`dotnet tool install -g Aspire.Cli`).

```bash
# PostgreSQL, the migration job, the app and the Aspire dashboard:
aspire run
# the same, without the Aspire CLI:
dotnet run --project tools/Trellis.AppHost
```

<!--#if (UseMultitenancy) -->
Multi-tenancy is on: regenerate the initial migration before the first run
([docs/development.md](docs/development.md), "Multi-tenancy").

<!--#endif -->
Sign in as `admin@example.com` / `Admin123!`; that account and the demo data are seeded in
Development only (production seeding wants your own password, see
[docs/deployment.md](docs/deployment.md)). In Development the API serves its reference at `/scalar/v1`.

No .NET SDK on the host? Build and test through the SDK container (`scripts/dotnet.sh`) or open the
folder in the dev container: [docs/development.md](docs/development.md).

## Build and test

From the repository root (`global.json` selects the test platform):

```bash
dotnet build Trellis.slnx -c Release
dotnet test Trellis.slnx                     # integration and API tests start PostgreSQL in Docker
```

## Continuous integration

`.github/workflows/ci.yml` runs the secret scan, the format check, the Release build, every test with
coverage, the API contract (the OpenAPI document diffed against the running app), the container smoke
(the production images published and booted with only the documented configuration) and the
SDK-container build; its docs-drift job honours a `docs-not-needed` pull-request label, which you create in your
repository. `.github/workflows/codeql.yml` runs on a public repository, and on a private one only once
GitHub Advanced Security code scanning is enabled and the `CODEQL_ENABLED` repository variable
(Settings → Secrets and variables → Actions → Variables) is `true`. Dependabot is configured in
`.github/dependabot.yml`.

## Where to look next

- [AGENTS.md](AGENTS.md): the conventions and boundaries, written for people and coding agents alike.
- [docs/INDEX.md](docs/INDEX.md): architecture, development, deployment, the threat model and the
  decision records in `docs/adr/`.
- [docs/features/invoices.md](docs/features/invoices.md): the reference slice. The `add-feature`,
  `remove-feature` and `add-integration` skills in `.claude/skills/` start from it.
- [CHANGELOG.md](CHANGELOG.md): record this project's changes under `[Unreleased]`.

## Template provenance

This solution was generated from a `dotnet new` template. .trellis\-template.json records the
template package, its version, and the name and options used; [docs/upgrading.md](docs/upgrading.md)
uses it to adopt a later template version. Keep it, unedited.

## Licensing

`LICENSE` is the MIT notice of the template this solution was generated from. The MIT terms ask
that the notice stay with substantial portions of that template, so keep it; it does not license
your product. Choose terms for your own code and add them as a license file of your own (or put your
terms in `LICENSE` and move the template's notice to a file such as `THIRD-PARTY-NOTICES.md`). The
NuGet packages the solution references carry their own licenses; `Directory.Packages.props` and
ADR-0011 explain the license ceilings the build enforces.
