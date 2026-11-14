<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="eng/readme/title-dark.svg">
  <img alt="Trellis: Clean Architecture the tests enforce, beside a terminal with its two install commands" src="eng/readme/title-light.svg" width="100%">
</picture>

[![NuGet](https://img.shields.io/nuget/v/Trellis.Templates?logo=nuget)](https://www.nuget.org/packages/Trellis.Templates)
[![NuGet downloads](https://img.shields.io/nuget/dt/Trellis.Templates?logo=nuget)](https://www.nuget.org/packages/Trellis.Templates)
[![Template CI](https://github.com/Revazi1st/Trellis/actions/workflows/template-ci.yml/badge.svg)](https://github.com/Revazi1st/Trellis/actions/workflows/template-ci.yml)
[![CI](https://github.com/Revazi1st/Trellis/actions/workflows/ci.yml/badge.svg)](https://github.com/Revazi1st/Trellis/actions/workflows/ci.yml)
[![CodeQL](https://github.com/Revazi1st/Trellis/actions/workflows/codeql.yml/badge.svg)](https://github.com/Revazi1st/Trellis/actions/workflows/codeql.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![GitHub stars](https://img.shields.io/github/stars/Revazi1st/Trellis?style=flat&logo=github)](https://github.com/Revazi1st/Trellis/stargazers)

</div>

```bash
dotnet new install Trellis.Templates
dotnet new trellis -n Your.Project
```

Documentation: https://revazi1st.github.io/Trellis/

This README follows `main`. The package on nuget.org is the latest release (the badge above shows its
version), and [`CHANGELOG.md`](CHANGELOG.md) lists what each release holds and, under *Unreleased*, what
`main` has added since. The documentation site describes the latest release, so a page or an option
this README links there can arrive with the next release.
[docs/upgrading.md](docs/upgrading.md#versions-and-support) says which versions get fixes.

**Trellis** is a `dotnet new` template for .NET 10 that generates a rich-DDD solution with
*Clean Architecture the tests enforce*. The solution has vertical-slice features, CQRS without a mediator, `Result`s instead of exceptions,
PostgreSQL, Identity with JWT, Aspire orchestration and an optional Blazor UI, in a repository written
for AI agents as carefully as for people. Like the lattice a gardener puts up for a climbing plant, it
carries the weight and sets the direction while your features grow, then disappears under them.

Options are flags of `dotnet new`, and options in the Visual Studio and Rider new-solution dialogs.

| Option | Default | What it does |
|---|---|---|
| [`--include-ui`](https://revazi1st.github.io/Trellis/start/options/#--include-ui) | on | Include the Blazor web UI (UI.Common + UI.Web) and wire it into the Aspire AppHost. |
| [`--use-redis`](https://revazi1st.github.io/Trellis/start/options/#--use-redis) | off | Add a Redis-protocol distributed cache (HybridCache L2), Valkey by default, and wire it into the Aspire AppHost. |
| [`--use-serilog`](https://revazi1st.github.io/Trellis/start/options/#--use-serilog) | off | Use Serilog for structured logging (console sink) in the API and web hosts, alongside OpenTelemetry. |
| [`--use-multitenancy`](https://revazi1st.github.io/Trellis/start/options/#--use-multitenancy) | off | Add a tenant seam: a tenant claim on the principal (ITenantContext), a TenantId shadow column and a "Tenant" EF query filter on every aggregate root. Regenerate the initial migration after enabling. |
| [`--include-ui-tests`](https://revazi1st.github.io/Trellis/start/options/#--include-ui-tests) | off | Include a browser-driven UI test project (Playwright). Tests are opt-in at run time via RUN_UI_TESTS=1. |
| [`--skip-restore`](https://revazi1st.github.io/Trellis/start/options/#--skip-restore) | off | Skip the automatic 'dotnet restore' after the project is created. |

## Table of contents 🧭

- [Give it a star ⭐](#give-it-a-star-)
- [Getting started 🚀](#getting-started-)
  - [Before you start](#before-you-start)
  - [Install, update, uninstall](#install-update-uninstall)
  - [Create your solution](#create-your-solution)
  - [Run it](#run-it)
  - [Sign in](#sign-in)
  - [What it looks like](#what-it-looks-like)
  - [Walk an invoice through its life](#walk-an-invoice-through-its-life)
  - [Look at the data](#look-at-the-data)
  - [Deploy it](#deploy-it)
- [Domain overview 🧾](#domain-overview-)
- [Use cases 📋](#use-cases-)
  - [Invoices](#invoices)
  - [Accounts](#accounts)
- [Folder structure 📂](#folder-structure-)
- [How a request flows 🚦](#how-a-request-flows-)
- [Authorization 🔑](#authorization-)
- [Testing 🧪](#testing-)
- [Fun features 🎉](#fun-features-)
- [Built for AI agents 🤖](#built-for-ai-agents-)
- [FAQ ❓](#faq-)
- [Comparisons 🔭](#comparisons-)
- [Contribution 🤝](#contribution-)
- [Credits 💐](#credits-)
- [License 📜](#license-)

## Give it a star ⭐

If Trellis saves you a week of wiring, or shows you a pattern done the way you hoped to see it, star
the repository: stars are how other .NET developers find it. Thank you!

## Getting started 🚀

### Before you start

- **Docker**: PostgreSQL, the integration tests (Testcontainers) and the SDK container need it. Docker
  Desktop and Linux Docker Engine are the tested engines; OrbStack, Colima and Podman are covered in
  [docs/development.md](docs/development.md#docker-engines-other-than-docker-desktop).
- **.NET 10 SDK** on the host, to run the orchestrated app. Building and testing work without it too,
  through `./scripts/dotnet.sh` (build its image once with
  `docker build -t trellis-sdk:10.0 scripts/sdk-image`) or the dev container.
- **Aspire CLI** for `aspire run`: `dotnet tool install -g Aspire.Cli`. Optional, since
  `dotnet run --project tools/Trellis.AppHost` starts the same app.
- **PowerShell** (`pwsh`), only to install the browsers for the optional Playwright tests.

[docs/development.md](docs/development.md#prerequisites) says what needs each tool and the minimum
version of each. Once the solution exists, run `./scripts/doctor.sh`: it checks each of these but
PowerShell on your machine, the development certificate and the AppHost's ports too, changes nothing,
and names the fix for anything missing.

### Install, update, uninstall

From nuget.org:

```bash
dotnet new install Trellis.Templates     # the latest release
dotnet new update --check-only           # is a newer version out?
dotnet new update                        # take it
dotnet new uninstall Trellis.Templates   # remove the template
```

To try `main` instead, install from a clone of this repository:

```bash
git clone https://github.com/Revazi1st/Trellis.git
dotnet new install ./Trellis
```

A newer template changes nothing in a solution you already created:
[docs/upgrading.md](docs/upgrading.md) adopts a later version by regenerating and diffing.
`dotnet new uninstall ./Trellis` removes a folder install. A folder install records `0.0.0-source` as
the template version; packing first (`dotnet pack eng/Trellis.TemplatePack -o nupkg`) and installing
the `.nupkg` records the package version instead.

### Create your solution

```bash
dotnet new trellis -n Acme.Shop -o Acme.Shop
cd Acme.Shop
```

Name the project with dot-separated parts made of letters and digits, each starting with a letter
(`Acme.Shop`): hyphens, spaces and underscores turn into underscores in namespaces, which analyzer rule
CA1707 rejects.

Paths in this README are the template's own; in your solution, `Trellis` reads as your project's
name (`tools/Trellis.AppHost` becomes `tools/Acme.Shop.AppHost`). The solution arrives with its tests,
its CI (`.github/workflows/ci.yml`, `codeql.yml` and `dependabot-auto-merge.yml`), the docs, the ADRs
and the AI assets, plus a README of its own, an empty CHANGELOG and `.trellis-template.json`, which
records the template version and your options ([docs/upgrading.md](docs/upgrading.md) reads it to
adopt a later version). On a private repository, the generated CodeQL job runs once code scanning is
enabled there and the `CODEQL_ENABLED` repository variable is `true`, and skips until then.

### Run it

```bash
aspire run                                   # PostgreSQL, migrations, the API and the web UI, under the Aspire dashboard
dotnet run --project tools/Trellis.AppHost   # the same app, without the Aspire CLI
```

From Rider or Visual Studio, run the **Trellis.AppHost** project: its `https` launch profile opens the
dashboard at <https://localhost:17171> (trust the development certificate once with
`dotnet dev-certs https --trust`), its `http` profile at <http://localhost:15171>, and a generated
solution picks free ports of its own when you create it. The dashboard lists `postgres`, the
`migrations` worker (it migrates, seeds and exits while the API waits), the `api` with a **Scalar**
link to its API reference, the `web` UI, and `mailpit`, which catches the e-mail the API sends (run
mode only: `aspire publish` leaves e-mail to your relay).

No .NET SDK on your machine? Choose **Reopen in Dev Container**, start PostgreSQL with
`docker compose up -d` (host port 5433), then run **Trellis.MigrationService** once, **Trellis.Api**
(<http://localhost:5171>) and **Trellis.UI.Web** (<http://localhost:5170>) from their
`(dev container)` launch profiles. `aspire run` cannot orchestrate from inside a container
([ADR-0008](docs/adr/0008-containerized-sdk-and-aspire-orchestration.md));
[docs/development.md](docs/development.md) has the details.

### Sign in

In Development, the migration worker seeds one administrator: `admin@example.com` /
`Admin123!`. Open the `web` resource and sign in, or get a token from the API with the first request
below.

### What it looks like

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="eng/readme/shots/first-run-invoices-1280-dark.png">
  <img alt="The Invoices page of the web UI: a filter per state above a table of the seeded invoices, each with its customer, status, total, due date, creation date and a View link." src="eng/readme/shots/first-run-invoices-1280-light.png" width="100%">
</picture>

<p align="center">
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="eng/readme/shots/first-run-invoice-400-dark.png">
  <img alt="A draft invoice on a phone-width screen: its status, e-mail, total and due date, its line, then the forms that add a line, issue it or void it." src="eng/readme/shots/first-run-invoice-400-light.png" width="300">
</picture>
</p>

CI's browser tests save these pictures of a first run: the invoice list on a laptop and one invoice
on a phone, showing the invoices the migration worker seeds in Development.

The UI is built from a design system of its own, with a light and a dark theme, a switch the user
keeps and tokens that export as JSON for a design tool
([docs/design-system.md](docs/design-system.md), [ADR-0032](docs/adr/0032-ui-design-system.md)).

### Walk an invoice through its life

Open [`src/Presentation/Api/Trellis.Api/Trellis.Api.http`](src/Presentation/Api/Trellis.Api/Trellis.Api.http)
in Rider, Visual Studio, or VS Code with the REST Client extension. Set `@host` to the `api` resource's
URL from the dashboard (the launch profiles and the dev container serve `http://localhost:5171`) and
choose the `dev` environment, which `src/Presentation/Api/http-client.env.json` fills with the seeded
admin and a demo member. Then send the requests in order, starting with a sign-in:

```http
POST {{host}}/api/v1/auth/login
Content-Type: application/json

{
  "email": "{{adminEmail}}",
  "password": "{{adminPassword}}"
}
```

The answer is a 200 with an `accessToken`: paste it into `@token`. Next, create a draft. The create
requires an `Idempotency-Key`, because a repeated create would otherwise be a second draft:

```http
POST {{host}}/api/v1/invoices
Authorization: Bearer {{token}}
Content-Type: application/json
Idempotency-Key: {{createKey}}

{
  "customerName": "Ada Lovelace",
  "customerEmail": "ada@example.com",
  "currency": "EUR"
}
```

A 201 comes back with a `Location` header and the new `id`, which goes into `@invoiceId`. The next
requests list the invoices (your draft comes first), add a line, issue it, read it back with its
`ETag` and again with `If-None-Match` for a 304, and pay it. Seconds later the receipt, handed to
`SendReceiptOnInvoicePaid` by the outbox after the commit, is in the `mailpit` resource's **Inbox**
(run without the AppHost, the api logs it instead, until you configure SMTP). The last requests show the guard rails: issuing again with the
`ETag` from before the payment is a 412, the create replayed with the same key returns the first 201
with `Idempotency-Replayed: true`, and a member who registers and signs in gets a 403 for a create.
[docs/features/invoices.md](docs/features/invoices.md#try-it) tables every step with its
answer.

Rather click? The `api` resource's **Scalar** link opens the API reference at `/scalar/v1`, which
takes the same bearer token, and the `web` UI walks the same lifecycle: sign in as the admin, create an
invoice, then add a line, issue and pay it from its page.

### Look at the data

Beside `postgres`, the dashboard has a **pgAdmin** tile, which opens without a login, and the
`mailpit` resource's **Inbox** link shows every e-mail the API sent. The `cache`
resource of a solution generated with `--use-redis true` has no browser tile: RedisInsight is SSPL,
which the license policy keeps out ([ADR-0011](docs/adr/0011-license-ceiling-policy.md)).

### Deploy it

`aspire publish` writes a Docker Compose file and an `.env` from the same model `aspire run` uses, and
the images come from `dotnet publish /t:PublishContainer`, with no Dockerfiles.
[docs/deployment.md](docs/deployment.md#deploy-to-one-host) is the ordered quick start for one host,
and [docs/threat-model.md](docs/threat-model.md) lists what to decide before production.

## Domain overview 🧾

The template ships one sample feature, invoicing: small enough to read in one sitting, and complete
enough to exercise every tactical pattern the template has. In plain words, before any architecture:

An invoice bills one customer, in one currency, for one or more lines of work. It starts as a
**draft**, the only state that takes new lines, and it is in the invoice list from the moment it
exists. **Issuing** it freezes the lines and sets a due date that may not lie in the past; once that
date passes, the invoice also shows up in the overdue list. **Paying** an issued invoice closes it and
e-mails the customer a receipt, **voiding** cancels a draft or an issued one, and a paid or voided
invoice never changes again. Administrators may do all of this; members may only read.

```mermaid
stateDiagram-v2
    direction LR
    [*] --> Draft: Create
    Draft --> Draft: AddLine
    Draft --> Issued: Issue (at least one line, due date not past)
    Issued --> Paid: MarkPaid (receipt e-mailed after the commit)
    Draft --> Voided: Void
    Issued --> Voided: Void
    Paid --> [*]
    Voided --> [*]
    note right of Issued: overdue once its due date has passed
```

Every arrow is a method on `Invoice` that returns a `Result`. Called from the wrong state, it answers
409 (`Invoice.NotDraft`, `Invoice.NotIssued`, `Invoice.AlreadyIssued` or `Invoice.AlreadyFinalized`)
and changes nothing. [docs/features/invoices.md](docs/features/invoices.md) tells the rest, file by
file. When your own feature is due, the [`add-feature`](.claude/skills/add-feature/SKILL.md) skill
copies this slice layer by layer, and [`remove-feature`](.claude/skills/remove-feature/SKILL.md)
deletes it once yours exists.

## Use cases 📋

A use case is one folder under `src/Core/Trellis.Application/Features/<Feature>/`: a command or a
query, its `internal sealed` handler and, where there is input to check, a validator. Its endpoint is
one class under `src/Presentation/Api/Trellis.Api/Features/<Feature>/`.

### Invoices

| Use case | Request | Permission |
|---|---|---|
| **CreateInvoice**: a draft for one customer, in one currency | `POST /api/v1/invoices`, with an `Idempotency-Key` | `invoices:write` |
| **AddInvoiceLine**: a line of work on a draft | `POST /api/v1/invoices/{id}/lines` | `invoices:write` |
| **IssueInvoice**: freeze the lines and set a due date | `POST /api/v1/invoices/{id}/issue` | `invoices:write` |
| **PayInvoice**: close an issued invoice | `POST /api/v1/invoices/{id}/pay` | `invoices:write` |
| **VoidInvoice**: cancel a draft or an issued invoice | `POST /api/v1/invoices/{id}/void` | `invoices:write` |
| **GetInvoice**: one invoice, with its `ETag` | `GET /api/v1/invoices/{id}` | `invoices:read` |
| **ListInvoices**: every invoice, newest first, or those in one state | `GET /api/v1/invoices?status=Issued` | `invoices:read` |
| **ListOverdueInvoices**: issued invoices past their due date | `GET /api/v1/invoices/overdue` | `invoices:read` |

Once a command commits, the outbox delivers its events to three reactions: `SendReceiptOnInvoicePaid`
e-mails the receipt, and `RecordMetricsOnInvoiceIssued` and `RecordMetricsOnInvoicePaid` count issued
and paid invoices on the `Trellis.Invoices` meter. In the web UI, the Invoices page lists them with a
filter per state, an invoice's own page adds lines, issues, pays or voids it, and a form creates one.

### Accounts

| Use case | Request | Who may call it |
|---|---|---|
| **Register**: a Member account, while `Registration:Mode` is `Open` (Development opens it) | `POST /api/v1/auth/register` | anyone |
| **Login**: a short-lived access token and a single-use refresh token | `POST /api/v1/auth/login` | anyone |
| **RefreshToken**: a new pair for a refresh token; presenting a spent one revokes all of the user's tokens | `POST /api/v1/auth/refresh` | whoever holds the token |
| **Logout**: revoke the refresh token presented | `POST /api/v1/auth/logout` | whoever holds the token |
| **RevokeSessions**: sign out everywhere | `POST /api/v1/auth/revoke-all` | a signed-in user |

Repeated wrong passwords lock an account, and the `auth` rate limit caps attempts from one source
([docs/features/auth.md](docs/features/auth.md)). One more piece is reference material rather than a
use case: `IExchangeRateProvider`, a port with a typed HTTP client behind it, shows how to depend on an
API you do not own
([docs/features/exchange-rates.md](docs/features/exchange-rates.md)).

## Folder structure 📂

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="eng/readme/architecture-dark.svg">
  <img alt="The solution as a bento of ring cards, each project a card holding its real folders: Presentation (Api, Api.Common, Api.Contracts, UI.Common, UI.Web) on top, Infrastructure and Persistence side by side as peers, Application and Domain with SharedKernel below them with the Invoices slice expanded, and Shared (ServiceDefaults) and tools (AppHost, MigrationService, Analyzers) at the bottom. Every arrow points inward." src="eng/readme/architecture-light.svg" width="100%">
</picture>

Projects are grouped by ring, and a feature is a vertical slice through them: the invoice use cases
are folders in Application, their endpoints are classes in Api, and their mapping lives in
Persistence. Dependencies point inward, and `LayerDependencyTests` fails when one points the wrong
way. Infrastructure and Persistence are peers that never reference each other, where the mainstream
templates ship one Infrastructure project with a Persistence folder inside
([ADR-0002](docs/adr/0002-infrastructure-persistence-split.md)).

<p align="center">
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="eng/readme/rings-dark.svg">
  <img alt="Concentric rings: Domain and SharedKernel at the centre, Application around them, Infrastructure and Persistence as the two halves of the next ring, Presentation outside." src="eng/readme/rings-light.svg" width="160">
</picture>
</p>

| Ring | Projects | What lives there |
|---|---|---|
| **Core** | SharedKernel, Domain, Application | business rules and use cases, in plain C# |
| **Infrastructure** | Infrastructure, Persistence | the driven adapters: e-mail, JWT and outbound HTTP; EF Core and Identity |
| **Presentation** | Api, Api.Common, Api.Contracts; UI.Common, UI.Web | the driving adapters: the HTTP API and the Blazor UI |
| **Shared** | ServiceDefaults | Aspire's cross-cutting defaults: OpenTelemetry, health, service discovery, resilience |
| `tools` | AppHost, MigrationService, Analyzers | orchestration, the migrate-and-seed worker, the RSLT001 analyzer |

The pictures are drawn from the solution tree by
[`eng/readme/render-diagrams.py`](eng/readme/render-diagrams.py), in a light and a dark version. They
are plain SVG, so Figma or Inkscape opens them as editable vectors.
[docs/architecture.md](docs/architecture.md) describes the rings and the key patterns in words.

## How a request flows 🚦

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="eng/readme/pipeline-dark.svg">
  <img alt="Paying an invoice as a rail of numbered steps: the HTTP request, hardening, the permission and idempotency filters and the endpoint; the Tracing, Logging, Validation and UnitOfWork decorators around the handler and the aggregate; the commit that writes the outbox row with the invoice; and past the outbox boundary, OutboxProcessor, the event handlers and the receipt e-mail." src="eng/readme/pipeline-light.svg" width="100%">
</picture>

There is no mediator. Each endpoint is one class that takes its handler straight from dependency
injection, and four decorators wrap every handler, outermost first: **Tracing** opens the use case's
span, **Logging** writes one outcome line inside it, **Validation** runs the validators and answers
400 before the handler is reached, and **UnitOfWork**, for commands only, commits a successful
`Result` once and a failed one never. `DecoratorPipelineTests` pins that order
([ADR-0003](docs/adr/0003-hand-rolled-cqrs.md)). Paying an invoice is this whole handler
([`PayInvoiceCommandHandler.cs`](src/Core/Trellis.Application/Features/Invoices/PayInvoice/PayInvoiceCommandHandler.cs)):

```csharp
internal sealed class PayInvoiceCommandHandler(IInvoiceRepository invoices, TimeProvider timeProvider)
    : ICommandHandler<PayInvoiceCommand>
{
    public async Task<Result> Handle(PayInvoiceCommand command, CancellationToken cancellationToken)
    {
        Invoice? loaded = await invoices.GetByIdAsync(command.InvoiceId, cancellationToken);

        return loaded.ToWrite(command.InvoiceId, command.ExpectedVersion)
            .Bind(invoice => invoice.MarkPaid(timeProvider.GetUtcNow()));
    }
}
```

`ToWrite` answers 404 for a missing invoice and 412 for a stale `If-Match`. The rule itself lives in
the aggregate, which reports a refusal as a value instead of throwing
([`Invoice.cs`](src/Core/Trellis.Domain/Invoices/Invoice.cs)):

```csharp
public Result MarkPaid(DateTimeOffset paidAtUtc)
{
    if (Status != InvoiceStatus.Issued)
    {
        return Result.Failure(InvoiceErrors.NotIssued);
    }

    Status = InvoiceStatus.Paid;
    PaidAtUtc = paidAtUtc;
    Raise(new InvoicePaidDomainEvent(Id, CustomerEmail.Value));
    return Result.Success();
}
```

The endpoint turns a failed `Result` into an RFC 9457 problem whose stable `code` clients branch on
(`Invoice.NotIssued`, 409), through one seam for the whole API
([ADR-0004](docs/adr/0004-result-over-exceptions.md), [ADR-0024](docs/adr/0024-problem-details-v2.md)).
A `Result` that nobody reads is a bug the compiler can see, so analyzer **RSLT001** fails the Release
build on a statement that drops one; `_ = …` with a one-line reason is the only deliberate discard
([ADR-0030](docs/adr/0030-discarded-result-analyzer.md)).

`Raise` sends nothing. `OutboxWriteInterceptor` writes the event to `OutboxMessages` in the same
transaction as the invoice, and after the commit `OutboxProcessor` hands it to
`SendReceiptOnInvoicePaid`, crediting that handler under its stable `[DomainEventHandlerName]`, so a
retry never runs a handler that already succeeded
([ADR-0018](docs/adr/0018-transactional-outbox-and-idempotency.md),
[ADR-0023](docs/adr/0023-outbox-lease-and-checkpoints.md)).
[docs/architecture.md](docs/architecture.md#a-request-stage-by-stage) takes a request through every
stage, and [docs/features/invoices.md](docs/features/invoices.md#from-a-payment-to-its-receipt)
draws the path from a payment to its receipt.

## Authorization 🔑

Secure by default: every endpoint on the versioned API group requires a signed-in caller, and four
auth endpoints (register, login, refresh and logout) are the whole anonymous allowlist. On top of
that, each endpoint names the one permission it needs from the catalogue in
[`Permissions.cs`](src/Core/Trellis.Application/Authorization/Permissions.cs), and the seeded roles
grant them: an **Administrator** holds every permission, a **Member** holds `invoices:read`. A token
carries one `permission` claim per permission its user's roles grant. Paying an invoice
([`PayInvoice.cs`](src/Presentation/Api/Trellis.Api/Features/Invoices/PayInvoice.cs)):

```csharp
public static RouteHandlerBuilder Map(IEndpointRouteBuilder app) => app
    .MapPost("invoices/{id}/pay", Handle)
    .RequirePermission(Permissions.Invoices.Write)
    .RequireIdempotency()
    .SupportsConditionalWrite()
    // … then the tags, summary and description for the OpenAPI document
```

| Who calls it | What they get |
|---|---|
| a token carrying `invoices:write` (the seeded admin) | the endpoint's own answer: 204 once paid, or the invoice's 404, 409 or 412 |
| a valid token without it (a member) | **403** `Auth.Forbidden` |
| no token, or one that fails validation | **401** `Auth.Unauthenticated`, with `WWW-Authenticate: Bearer` |

The other shapes follow the same how-to
([docs/features/auth.md](docs/features/auth.md#protecting-an-endpoint)): an action on the caller's own
account needs only a signed-in caller (`revoke-all`); an anonymous endpoint calls `.AllowAnonymous()`
and joins the allowlist, with the `auth` rate limit when it takes a guessable credential; a permission
plus a further condition stacks a named policy beside it; and a rule about *which* record (its owner,
a team) belongs to the feature and answers 404, as for a record that does not exist. A new permission
is a constant in `Permissions`, listed in `Permissions.All` and granted to a role in
`Roles.Permissions`.

## Testing 🧪

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="eng/readme/testing-dark.svg">
  <img alt="The test suites as cards under a rail of the layers, each as wide as the layers it runs through: the opt-in UiTests from the browser and the opt-in Aspire end-to-end test from the AppHost, both to PostgreSQL; ApiTests from Shared and IntegrationTests from Persistence, both to a real PostgreSQL; Application.UnitTests and Domain.UnitTests in memory; and ArchitectureTests as a bracket over every project." src="eng/readme/testing-light.svg" width="100%">
</picture>

Most of a feature's tests belong in `Domain.UnitTests` and `IntegrationTests`: a rule is cheapest to
pin where it lives, and a query or a transaction only proves itself against PostgreSQL. The fitness
suites already cover every endpoint's permission, problem shape, idempotency and OpenAPI entry, so an
`ApiTests` test is for what they cannot see. [docs/development.md](docs/development.md#tests) tables
each test project (what it proves, what it needs and when a test of yours belongs in it), maps where a
new test belongs and says which helper serves which goal.

```bash
dotnet test Trellis.slnx                                  # everything; the Aspire E2E skips unless RUN_ASPIRE_E2E=1
./scripts/dotnet.sh test Trellis.slnx                     # the same through the SDK container, no local SDK
./scripts/dotnet.sh test tests/Trellis.Domain.UnitTests   # one project
```

**Conventions are tests, not prose.** `LayerDependencyTests` holds the arrows of the pictures above,
`ConventionTests` refuses a handler that runs another use case, `ErrorCodeVocabularyTests` keeps every
error code in `Area.Problem` form with one meaning, `OptionsConventionTests` boots the API with broken
settings, `TelemetryNamingTests` holds the telemetry reference in `docs/deployment.md` to the code,
`ContainerImageTests` admits only container images whose license it names, and `EndpointMetadataTests`
pins each endpoint's permission. Every rule has a positive control: a planted
violation it must catch (`PlantedViolations`).

**Gates beyond the tests** ([docs/development.md](docs/development.md#gates-beyond-the-tests)):

- the OpenAPI snapshot: the served document must equal the committed one;
- docs drift, ADR links and ADR history;
- a coverage floor per ring;
- Template CI, which tests what the template generates.

## Fun features 🎉

**Idempotency keys that replay.** `POST /api/v1/invoices` requires an `Idempotency-Key`, and the
reservation commits with the command, so a failed command leaves none. A retry with the same key and
body gets the stored response byte for byte, with `Idempotency-Replayed: true`
([ADR-0018](docs/adr/0018-transactional-outbox-and-idempotency.md),
[runbook](docs/deployment.md#idempotency-keys)).

**ETags and `If-Match`.** `GET /api/v1/invoices/{id}` sends a strong `ETag` from the row's version, and
`If-None-Match` turns an unchanged read into a 304 with no body. The invoice writes take `If-Match`: a
stale tag is a 412 `Invoice.VersionMismatch` that changes nothing
([docs/features/invoices.md](docs/features/invoices.md)).

**An outbox that leases and checkpoints.** The processor claims due rows with `FOR UPDATE SKIP LOCKED`
and leases them, so two replicas never dispatch the same row at once. Each handler is credited at its
own checkpoint, only the failed ones are retried with back-off, and a row that keeps failing is
dead-lettered for a SQL re-drive ([ADR-0023](docs/adr/0023-outbox-lease-and-checkpoints.md),
[runbook](docs/deployment.md#outbox-operations)).

**Multi-tenancy on the principal** (`--use-multitenancy`). The tenant comes from the `tenant` claim
minted at sign-in and from nowhere else: there is no tenant header, and a token without the claim is
refused. Every aggregate root gets a `TenantId` column and a `"Tenant"` query filter by convention, so
another tenant's invoice is a 404 ([ADR-0022](docs/adr/0022-tenant-on-the-principal.md)).

**HybridCache, with Valkey behind it.** `GET /api/v1/invoices/{id}` is cache-aside: an in-memory copy
always, and with `--use-redis` a copy in Valkey, over the Redis protocol, that every replica shares.
Keys come from one `CacheKeys` service (tenant-scoped under multi-tenancy), every write evicts after its
commit, and a 404 is never cached ([ADR-0026](docs/adr/0026-read-caching.md)).

**SMTP by configuration.** E-mail goes out through MailKit once `Email:Provider=Smtp` and an
`Email:Smtp` section point at a relay, validated at boot only while selected; until then it is logged,
not sent. Under `aspire run` the AppHost points it at Mailpit, a mail catcher with an **Inbox** tile in
the dashboard ([docs/development.md](docs/development.md#secrets)).

**A dropped `Result` fails the build.** RSLT001, from `tools/Trellis.Analyzers`, is loaded into every
project, the tests included. A discarded `Result`, `Error`, `IdentityResult` or UI `ApiResult<T>` is a
warning in Debug and an error in Release, and so is a `Task<Result>` handed on as a plain `Task`
([ADR-0030](docs/adr/0030-discarded-result-analyzer.md)).

**Each endpoint's permission, pinned.** `EndpointMetadataTests.ExpectedPermissions` lists every
endpoint with its one permission and fails on a missing, extra or stale row. A wire-level test calls
each endpoint with only that permission and with every other one, expecting its own answer and then a
403 ([Authorization](#authorization-)).

**Dependency bumps that merge themselves.** Dependabot holds a new version back for a cooldown (a
security update arrives at once), and
[`dependabot-auto-merge.yml`](.github/workflows/dependabot-auto-merge.yml) merges its patch and minor
pull requests once the required checks pass, where the repository allows auto-merge and `main` requires
status checks. A major, a .NET SDK bump and the GitHub Actions group wait for a person, and
`NuGetAudit` fails a Release build on a vulnerable package, transitive ones included
([ADR-0029](docs/adr/0029-central-package-management.md)).

[docs/overview.md](docs/overview.md) lists every capability of the solution with the doc that explains
it and the decision behind it.

## Built for AI agents 🤖

The repository is written for an agent to work in as much as for a person, and every piece of that is
plain text you can read too.

- **[AGENTS.md](AGENTS.md)**: the commands, the dependency flow and the boundaries, in the
  tool-neutral format. `CLAUDE.md` imports it, and `.github/copilot-instructions.md` points Copilot at
  it ([ADR-0014](docs/adr/0014-ai-documentation-architecture.md)).
- **Path-scoped rules** in `.claude/rules/`, one per layer plus the tests and the tools, each loaded
  when an agent touches a file its glob matches: the domain rules arrive with the first edit under
  `src/Core/Trellis.Domain/`. [`dependencies.md`](.claude/rules/dependencies.md) arrives with the
  package and SDK files: asking before a package, the license ceilings, central versions, the order to
  answer a NuGetAudit advisory that turns the build red, and an SDK bump.
- **Skills** in `.claude/skills/`, step-by-step recipes:
  [`add-feature`](.claude/skills/add-feature/SKILL.md) (a checklist that names the test behind each
  enforced box), [`remove-feature`](.claude/skills/remove-feature/SKILL.md),
  [`add-integration`](.claude/skills/add-integration/SKILL.md),
  [`add-migration`](.claude/skills/add-migration/SKILL.md), [`new-adr`](.claude/skills/new-adr/SKILL.md),
  [`run-app`](.claude/skills/run-app/SKILL.md), [`finish-change`](.claude/skills/finish-change/SKILL.md)
  (the gates CI runs, in its order, and the reviewers a change needs, before a commit; with `pr`, the
  pull request filled from its template) and
  [`upgrade-template`](.claude/skills/upgrade-template/SKILL.md) (a later template version adopted by
  regenerating and diffing; it runs only when you type it). An agent follows them, and so can you.
- **Reviewers** in `.claude/agents/`, read-only subagents, each opening with what it owns, what it
  leaves to another and how they overlap, so a change read by several hears each point once:
  [`code-reviewer`](.claude/agents/code-reviewer.md) (correctness and the layer's conventions),
  [`architecture-guard`](.claude/agents/architecture-guard.md) (projects, references, packages),
  [`test-reviewer`](.claude/agents/test-reviewer.md) (a change's tests against the testing rules, a bug
  fix's test shown to fail without the fix for the expected reason),
  [`security-reviewer`](.claude/agents/security-reviewer.md) (authentication, identity, hardening and who
  may call an endpoint, against the threat model) and, with the UI,
  [`ui-reviewer`](.claude/agents/ui-reviewer.md) (a page, a layout or a stylesheet against the design
  system, the UI rules and accessibility, in a browser through the Playwright MCP when one is running).
  `AGENTS.md` says which one a change needs; their findings are claims to check against the code before
  acting on them.
- **One index**: [docs/INDEX.md](docs/INDEX.md) lists the guides, the rules, the skills, the reviewers,
  the hooks, the MCP servers and the decisions, each with when it applies, and the link check fails
  when a rule, a skill, a reviewer, a hook, a decision or a feature doc is missing from it.
- **Decisions** in [docs/adr/](docs/adr/), indexed in [docs/INDEX.md](docs/INDEX.md): each with its
  context and consequences and, where it takes an idea from another project, a *Prior art* line naming
  it. Your own start at 0100.
- **Hooks** in `.claude/hooks/`, each backing a rule `AGENTS.md` states: the edit hook asks before a
  hand edit of a generated file (a migration, the OpenAPI snapshot) and before a new project or package;
  the commit hook asks for a Conventional Commits subject and, with gitleaks installed, refuses a staged
  secret; a Stop hook reminds the agent once to run the tests it owes. `scripts/check-hooks.sh` tests
  every decision, and CI runs it.
- **Drift gates**: the docs-drift hook (once per report) and CI's docs-drift job, the link check, the
  OpenAPI snapshot and the fitness tests turn the docs and the conventions into something a change can
  fail.
- **[.mcp.json](.mcp.json)** wires up the Microsoft Learn, Context7, Playwright and Aspire MCP servers,
  the last reading the running AppHost's logs, traces and resources under `aspire run`; the index says
  when to reach for each, and points at the optional .NET plugins of `dotnet/skills` without enabling
  any.

The release runbook adds an agent dry run before each minor version: an agent builds one fixed feature
in a freshly generated project with nothing but that repository to go on, and the existing gates judge
the result ([docs/releasing.md](docs/releasing.md)).

## FAQ ❓

[docs/faq.md](docs/faq.md) answers the first questions: whether another database can replace
PostgreSQL, why MediatR, AutoMapper and FluentAssertions are absent, how to remove the sample feature
or add your own, whether an option can change after creation, and whether a newer template version
changes a solution you created.

## Comparisons 🔭

Trellis is measured against
[amantinband/clean-architecture](https://github.com/amantinband/clean-architecture),
[xavierjohn/Trellis](https://github.com/xavierjohn/Trellis) (no relation; the shared name is a
coincidence) and [jasontaylordev/CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture).
Every idea taken from them is credited by name below, and a project we only compared against gets no
line. The largest structural difference from the mainstream templates, Infrastructure and Persistence
as two peer projects, is recorded in [ADR-0002](docs/adr/0002-infrastructure-persistence-split.md).
The site holds [a dated table of facts](https://revazi1st.github.io/Trellis/compare/) about Trellis and
other templates.

## Contribution 🤝

Found a bug, or missing a feature? Issues and pull requests are welcome, and
[CONTRIBUTING.md](CONTRIBUTING.md) says what to run before a pull request, how to test a change against
the projects the template generates, and how to write the commits. Questions, and ideas you want to try
out first, go to [GitHub Discussions](https://github.com/Revazi1st/Trellis/discussions); a
vulnerability goes through [SECURITY.md](SECURITY.md), never a public issue. Every version's changes
are in [CHANGELOG.md](CHANGELOG.md), and [docs/INDEX.md](docs/INDEX.md) indexes the docs for people and
agents alike.

<a name="credits"></a>

## Credits 💐

Trellis takes ideas from other people's work. Each line names what was taken; nothing below is a
verbatim copy, so the MIT, BSD and Apache notices stay with their projects. Templates we only compared
against are not listed (ADR-0002 names them).

**Templates and libraries we learned from**
- [clean-architecture](https://github.com/amantinband/clean-architecture) by Amichai Mantinband (MIT):
  - the per-use-case request collection and the Getting Started walkthrough, one request and body per step;
  - a configuration-gated SMTP sender and the user-secrets setup walkthrough;
  - the Domain Overview device, plain words before architecture, and the folder map that prompted our slice tree;
  - one authorization test per use case, which prompted pinning each endpoint's permission, and the permission-scoped token idiom;
  - use-case drivers, optional-parameter factories, the paired HTTP test client and the switchable current-user double (rewritten, no MediatR);
  - the one-test-suite-per-use-case discipline with test folders mirroring the Application slice;
  - the eventual-consistency walkthrough shape, the one-aggregate-per-use-case explanation, and the idea of a reference time-triggered use case;
  - the Testing section mapping each test type onto the layers it exercises, and where to invest;
  - the authorization how-to format, one snippet then who passes, and the self-or-admin idea behind our ownership recipe;
  - the show-the-code README sections;
  - the visual walkthrough-style README: an icon on each heading, a request for a star, the domain before the architecture, a request walkthrough and pictures of the folders and the tests, which also prompted the screenshots of the running app (the pictures and the words are our own).
- [Trellis](https://github.com/xavierjohn/Trellis) by Xavier John (MIT; no affiliation, the shared name is a coincidence):
  - lease-fenced outbox bookkeeping writes, and sibling event handlers run on every attempt with only the failed ones retried;
  - failing fast when a `Result` reaches JSON, a failed `Result` that names its code, and compiler-guarded `Error` access;
  - value-object errors attributed to the request field, a type-aware and tuple `Result.Combine`, and whole-request validation errors;
  - error descriptions redacted from the outcome log line, and tracing outside logging;
  - concurrency promotion at the aggregate boundary, and declared unique constraints as 409;
  - the guard against a handler invoking another handler;
  - error descriptions redacted from every 5xx `detail`;
  - upstream status mapped by endpoint contract, `Unavailable` as 503, and optional `If-Match` on transition POSTs;
  - the error-code vocabulary fitness test and the value-object constructor test;
  - the build-time analyzer for a discarded `Result`.
- [CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture) by Jason Taylor (MIT):
  - booting the generated solution's AppHost from its own test suite in template CI, and a template-CI leg for every option value;
  - a local script that instantiates, builds and tests every template leg and prints a results table (`build/test.ps1`);
  - release notes in the package metadata;
  - a persistent-lifetime database container in the AppHost, Aspire's guidance as his AppHost applies it;
  - one resource-name constants class referenced by the AppHost through an `IsAspireProjectResource="false"` reference (`Shared/Services.cs`);
  - `DefaultStartup="true"` on the AppHost in the `.slnx`;
  - logging each use case under its own request type's category (`ILogger<TRequest>`), the acting user's id on every request's log line, and a slow use case surfaced at Warning past a threshold (`LoggingBehaviour`, `PerformanceBehaviour`);
  - stamping audit columns with the user's id rather than a display name (`AuditableEntityInterceptor`);
  - opening the API's root on its Scalar reference;
  - a short, ordered deployment quick start, which Getting started points at (`docs/deployment.md`, *Deploy to one host*);
  - a section of the README that lists the versions of the template (the *Versions* section of his README; ours is *Versions and support* in `docs/upgrading.md`);
  - a documentation website as the template's home: a landing page with the install command, feature cards and a three-step quick start; a Getting started / Architecture / Features sidebar with a linear path; an installation page with update and uninstall; one page per option that says what each choice adds and what to do after creation; an architecture page led by one ring diagram and the dependency rule; the documented version in the footer; a Testing page with one table of test projects and one run command; Astro + Starlight on GitHub Pages (ideas from cleanarchitecture.jasontaylor.dev; no code or CSS copied).
- [dotnet-starter-kit](https://github.com/fullstackhero/dotnet-starter-kit) by Mukesh Murugan (MIT): Valkey in place of Redis in the starter, as an idea; the `fsh doctor` idea, a check of the SDK, Docker, Aspire and the ports before a first run (`scripts/doctor.sh`).
- [Ardalis.Specification](https://github.com/ardalis/Specification) by Steve Smith (MIT): the usage shape of `Specification<T>` (ADR-0017).
- [SSW.CleanArchitecture](https://github.com/SSWConsulting/SSW.CleanArchitecture) (MIT): the migration worker that migrates, seeds and stops while the API waits (`MigrationWorker.cs`).
- [Aspire ServiceDefaults](https://github.com/dotnet/aspire) project template by Microsoft (MIT): the shape and method names of `Trellis.ServiceDefaults/Extensions.cs`.
- [Phoenix](https://github.com/phoenixframework/phoenix) by Chris McCord (MIT): flagging security-relevant changes in the changelog of code a generator writes once and never updates, the practice of its `phx.gen.auth` (*Versions and support* in `docs/upgrading.md`).

**Agent setups we learned from**
- [Aspire](https://github.com/microsoft/aspire) by Microsoft (MIT): the *Owns* / *Out of scope* / *Overlap* lines that open each reviewer in `.claude/agents/`, and their "zero duplicate feedback" rule, from its `reviewing-aspire-architecture` agent; and a finishing skill that runs the repository's gates and fills its pull-request template, from its `create-pr` skill (`finish-change`).
- [ASP.NET Core](https://github.com/dotnet/aspnetcore) by Microsoft (MIT): the red/green proof a bug fix's test owes, that it "fails for the expected reason without the fix", from its Copilot instructions (`test-reviewer`).
- [claude-plugins-official](https://github.com/anthropics/claude-plugins-official) by Anthropic (Apache-2.0): a reviewer dedicated to a change's tests, the idea behind `test-reviewer` (`pr-test-analyzer` in its `pr-review-toolkit`), and the `security-reviewer` template in its `claude-code-setup` plugin, the starting point of ours; as ideas only, `hookify`'s `require-tests-run` Stop rule behind `stop-tests.sh` and `security-guidance`'s check at commit time behind `guard-commit.sh`.
- [Claude Code documentation](https://code.claude.com/docs/en/hooks-guide) by Anthropic: the protect-files `PreToolUse` recipe that `guard-edits.sh` grew from, the best-practices example of a hook that guards the migrations folder, and the `stop_hook_active` guard that keeps `stop-tests.sh` from blocking twice in a row.
- [gitleaks](https://github.com/gitleaks/gitleaks) by Zachary Rice (MIT): the documented pre-commit invocation (`gitleaks git --pre-commit --staged --redact`) that `guard-commit.sh` runs when gitleaks is installed.
- [dotnet/skills](https://github.com/dotnet/skills) by Microsoft (MIT): the plugins `docs/INDEX.md` points at as optional installs (`dotnet`, `dotnet-test`, `dotnet-data`); nothing is copied or enabled.
- [PlatformPlatform](https://github.com/platformplatform/PlatformPlatform) by Thomas Jespersen and contributors (MIT): the `.mcp.json` entry for the Aspire MCP server, as an idea.

**Formats and conventions**
- [Michael Nygard's ADR format](https://cognitect.com/blog/2011/11/15/documenting-architecture-decisions): Status, Context, Decision, Consequences (ADR-0001).
- [AGENTS.md](https://agents.md): the tool-neutral agent instruction file (ADR-0014).
- [Contributor Covenant 2.1](https://www.contributor-covenant.org) (CC BY 4.0): `CODE_OF_CONDUCT.md`.
- [Keep a Changelog 1.1.0](https://keepachangelog.com/en/1.1.0/) and [Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html): `CHANGELOG.md` and the release rules.
- [Conventional Commits](https://www.conventionalcommits.org): the commit style in `AGENTS.md`, which `CONTRIBUTING.md` points at and the commit hook checks.
- Martin Fowler's [subcutaneous test](https://martinfowler.com/bliki/SubcutaneousTest.html) term, used for the integration-test host.

**Services**
- [Frankfurter](https://frankfurter.dev): the free exchange-rate API the reference integration calls by default.

Packages carry their own licenses; see `Directory.Packages.props` and ADR-0011.

## License 📜

MIT — see [LICENSE](LICENSE). A generated project keeps that notice as `LICENSE`, because the MIT
terms require it to travel with substantial portions of the template, but it does not license your
product: the generated README asks you to add your own terms. The NuGet packages a generated
solution references carry their own licenses; [ADR-0011](docs/adr/0011-license-ceiling-policy.md)
explains the ceilings the build enforces.
