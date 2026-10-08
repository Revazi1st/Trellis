---
description: Conventions for the Aspire AppHost, the MigrationService and template conditionals in YAML
paths: ["tools/**/*.{cs,csproj}", ".github/**/*.{yml,yaml}", "docker-compose.yml"]
---

# Tools rules

## AppHost (`tools/Trellis.AppHost`)

- Aspire orchestration: postgres (image major pinned — data volumes don't survive major bumps) → migrations (`WaitFor`) → api (`WaitForCompletion(migrations)`) → web. `aspire run` needs a real host, not the SDK container (ADR-0008).
- **Template toggles are `#if` blocks** (`UseRedis`, `IncludeUI`) processed at `dotnet new` time. The csproj defines `IncludeUI` in `DefineConstants` (inside an XML-comment `#if` directive on `IncludeUI`) so the template *source* compiles too — keep both sides in sync when adding a toggle.
- `AddDockerComposeEnvironment("compose")` backs `aspire publish`; images come from `dotnet publish /t:PublishContainer` — no Dockerfiles.

## MigrationService (`tools/Trellis.MigrationService`)

- `MigrationWorker` applies EF migrations, seeds, then `StopApplication()` — the API waits on successful exit; a failure sets `ExitCode = 1` so the wait fails loudly.
- **Non-HTTP host**: registers `SystemCurrentUser` for `ICurrentUser` and (with multi-tenancy) `SystemTenantContext` for `ITenantContext` — every new host-scoped port needs a system stand-in here or `AppDbContext` won't resolve.
- **Seed guards**: sample data (demo invoices) seeds in Development only; the worker **refuses to seed `SeedOptions.DefaultAdminPassword` outside Development** — override via the `Seed` section in real environments.
- Permission seeding iterates `Roles.Permissions` (Application) — new permissions/roles flow through automatically; never hard-code them here.

## Template conditionals in YAML (workflows, compose, dependabot)

Template repository only (generated projects keep no directives). YAML has no preprocessor: `dotnet new` reads hash-comment directives, but here every directive is an ordinary comment.

- **A plain `#if (X)` … `#else` … `#endif` block is live in BOTH branches here** — CI runs every line of each. Use it when that is harmless (the `ui-tests` job in `ci.yml`, which ships only with `--include-ui-tests`).
- **A branch that must stay inert here uses the doubled-hash form**: `##if (X)` / `##else` / `##endif`, every line of the inert branch commented with one `#` in column 0, every other hash in the block doubled. The engine strips each single hash in the branch it keeps and turns `##` into `#`, so the commented lines come alive in generated output. Nothing nests inside such a block. Worked example: the Redis steps in `ci.yml`.
- **`#if (TemplateSource)` marks source-only lines**: `TemplateSource` is a constant that is false whenever `dotnet new` runs, so the block runs here and never ships (the `ci.yml` step that runs `scripts/check-template-toggles.sh`).
- **A directive token only opens its own line**, condition in parentheses, symbol declared in `template.json`. The engine matches the token anywhere in a line and truncates the shipped file from there, so never quote one inside a `run:` script or a comment (the same holds for the XML-comment form in shipped `.md` and for `#if` in `.sh`). In the template repository `scripts/check-template-toggles.sh` enforces all of this; files `template.json` excludes (`template-ci.yml`, `release.yml`) are never processed and not checked.
