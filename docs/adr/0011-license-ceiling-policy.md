# ADR-0011: License-ceiling policy for encumbered packages

- **Status**: accepted
- **Date**: 2026-07-14
- **Amended**: 2026-10-08 — scope: what ships, and what build-time analyzers and the template's own notice are; 2026-11-08 — more ids, banned by family, a version ceiling the build cannot enforce, and the container images the AppHost and the compose files pull (see the *Amendment* sections below)

## Context

The 2025–26 .NET OSS wave moved several defaults to commercial licenses. A redistributable `dotnet new` template must not bake in a package that forces every consumer through a paid/license-key flow — even the "free tier" ones. This must be enforceable against both humans and AI agents (which "helpfully" upgrade packages).

## Decision

Maintain a **LICENSE CEILINGS** block at the top of `Directory.Packages.props` naming the packages never to add or upgrade past their last-free version:

| Package | Ceiling | Reason | This template instead |
|---|---|---|---|
| MediatR | ≥ 13.0.0 | Lucky Penny, RPL-1.5/commercial | hand-rolled CQRS (ADR-0003) |
| AutoMapper | ≥ 15.0.0 | Lucky Penny, RPL-1.5/commercial | manual mapping |
| FluentAssertions | ≥ 8.0.0 | Xceed, proprietary | Shouldly |
| MassTransit | ≥ 9.0.0 | Massient, commercial | Wolverine/Rebus if messaging needed |
| Moq | any version | MIT, but 4.20.0 shipped SponsorLink, which read the developer's git e-mail at build time and sent a hash of it off the machine — a trust break, not a license one | NSubstitute |

The whole shipped stack is free-for-commercial. `AGENTS.md` and the `Ask first: adding a NuGet package` boundary route package additions through this file. The ceiling is enforced, not advisory: `Directory.Build.props` fails the build on a forbidden direct `PackageReference` (`EnforceLicenseCeilings`, before restore) and on a forbidden id anywhere in a project's resolved package graph, transitive dependencies included (`EnforceLicenseCeilingsOnResolvedGraph`, after `ResolvePackageAssets`); both read one list, `LicenseCeilingPackageIds`. Dependabot's ignore list blocks the bot-upgrade path.

## Consequences

- Consumers inherit no commercial license obligation.
- The ceiling comment is the single source of truth agents consult before adding packages.
- Re-verify ceilings periodically; licensing terms and thresholds change.

## Amendment (2026-10-08): what the policy covers

The ceilings govern what reaches a consumer: a package that ships in a generated product's output, or
one that forces a license flow on everyone who builds it. Two things sit outside them and are stated
here so nobody has to guess:

- **Build-time analyzers** (`GlobalPackageReference` in `Directory.Packages.props`: SonarAnalyzer.CSharp,
  Meziantou.Analyzer) run inside the compiler and ship in no output; their terms bind the build only.
  A new one is still an ask-first package addition (`AGENTS.md`), and its license is read before it is
  added like any other's.
- **The template's own notice.** A generated project keeps the template's MIT `LICENSE` (copied
  verbatim, never renamed with the project), because the MIT terms require it to travel with
  substantial portions of the template. It does not license the product: the generated README asks
  the project to add its own terms.

## Amendment (2026-11-08): more ids, banned by family, and the container images

The 2026 wave reached further than the five packages above, and three more never were free for a
product to ship. `LicenseCeilingPackageIds` gains them, each with its reason in the LICENSE CEILINGS
comment:

| Package | Ceiling | Reason | This template instead |
|---|---|---|---|
| Hangfire.Core, Hangfire.Pro | any version | Hangfire OÜ: LGPL-3.0 or commercial; Pro is commercial only | a `BackgroundService` (the outbox processor, the retention janitor) |
| TickerQ | ≥ 10.5.0 | Arcenox: the source-available "TickerQ Software License Agreement", effective 2026-10-13; 10.4.x was MIT OR Apache-2.0 | a `BackgroundService` |
| NBomber | any version | NBomber LLC, license v3.2: "not free for organizational use" | no load-test tool ships |
| Duende.IdentityServer | any version | Duende: commercial; the Community Edition is capped by revenue | the API's own JWT endpoints (ADR-0007) |
| NServiceBus | any version | Particular: RPL-1.5 or commercial | the outbox (ADR-0018); Wolverine or Rebus if a bus is needed |
| MySql.EntityFrameworkCore | any version | Oracle: GPL-2.0 with the Universal FOSS Exception | PostgreSQL through Npgsql |

**A family is banned through its root.** Both targets match whole package ids, never a prefix, so
the list names the package every member of a family depends on, and the resolved-graph target finds
it under any member: `Hangfire.Core` under `Hangfire.AspNetCore` and the storage packages,
`NServiceBus` under every `NServiceBus.*` package, `Duende.IdentityServer` under its EF Core and
ASP.NET Core Identity packages. Two families have a root the table does not name, and the list
carries it too: `TickerQ.Utilities`, which `TickerQ.EntityFrameworkCore` and `TickerQ.Dashboard`
depend on instead of `TickerQ` and which ships under the same agreement, and `MySql.Data`, the
ADO.NET driver under `MySql.EntityFrameworkCore`, under the same license. Dependabot's ignore list
names every id in the list.

**A version ceiling the build cannot enforce.** `Verify` from 33.0.0 ships an "Open Source
Maintenance Fee" EULA (`OsmfEula.txt`) and a `SponsorCheck` build task in its package; 32.0.1 was the
last plain MIT one. The id stays allowed below the ceiling, so it cannot go into
`LicenseCeilingPackageIds`: the LICENSE CEILINGS comment names it, and a project that adds snapshot
tests pins it below 33.0.0. Nothing here references it today.

**The ceiling covers container images too.** A package is not the only thing a generated solution
makes everyone pull: the AppHost starts containers in run mode and writes them into the compose file
`aspire publish` produces, the root `docker-compose.yml` starts the development dependencies, and the
workflows can run containers of their own. The `--use-redis` cache was the case that showed it: the
AppHost's `AddRedis` pulled the Redis 8 server, licensed RSALv2, SSPLv1 or AGPLv3 since Redis 8.0
(Redis 7.2 and earlier were BSD-3-Clause), and `WithRedisInsight` its browser, under the SSPL. Every
image the AppHost declares, and every image the workflows and the root compose file name, is now on an
allowlist held by a test, `ContainerImageTests` in ArchitectureTests, each entry with the license it
ships under: PostgreSQL and pgAdmin (PostgreSQL License), Valkey (BSD-3-Clause) and the Aspire
dashboard of the compose output (MIT). An image off the list fails the test, and so does a resource
whose image the scan cannot tell. The cache became Valkey, a fork of Redis 7.2 that speaks its
protocol, so the API keeps its Redis client (`Aspire.StackExchange.Redis.DistributedCaching`, MIT)
and `--use-redis` keeps its name; it has no browser tile. Adding an image is a license decision like
adding a package: read the license, then add the row with it.
