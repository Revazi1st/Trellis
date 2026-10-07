---
description: Conventions for the Aspire AppHost and the MigrationService
paths: ["tools/**/*.{cs,csproj}"]
---

# Tools rules

## AppHost (`tools/Trellis.AppHost`)

- Aspire orchestration: postgres (image major pinned — data volumes don't survive major bumps) → migrations (`WaitFor`) → api (`WaitForCompletion(migrations)`) → web. `aspire run` needs a real host, not the SDK container (ADR-0008).
- **Template toggles are `#if` blocks** (`UseRedis`, `IncludeUI`) processed at `dotnet new` time. The csproj defines `IncludeUI` in `DefineConstants` (inside an XML-comment `#if` directive on `IncludeUI`) so the template *source* compiles too — keep both sides in sync when adding a toggle. In shipped `.md`, `.sh` and `.yml` files a directive token may appear only on a line of its own: the engine matches it anywhere in a line and drops the rest of the generated file (`scripts/check-template-toggles.sh` fails on such lines).
- `AddDockerComposeEnvironment("compose")` backs `aspire publish`; images come from `dotnet publish /t:PublishContainer` — no Dockerfiles.

## MigrationService (`tools/Trellis.MigrationService`)

- `MigrationWorker` applies EF migrations, seeds, then `StopApplication()` — the API waits on successful exit; a failure sets `ExitCode = 1` so the wait fails loudly.
- **Non-HTTP host**: registers `SystemCurrentUser` for `ICurrentUser` and (with multi-tenancy) `SystemTenantContext` for `ITenantContext` — every new host-scoped port needs a system stand-in here or `AppDbContext` won't resolve.
- **Seed guards**: sample data (demo invoices) seeds in Development only; the worker **refuses to seed `SeedOptions.DefaultAdminPassword` outside Development** — override via the `Seed` section in real environments.
- Permission seeding iterates `Roles.Permissions` (Application) — new permissions/roles flow through automatically; never hard-code them here.
