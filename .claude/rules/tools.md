---
description: Conventions for the Aspire AppHost, the MigrationService and the workflow and compose YAML
paths: ["tools/**/*.{cs,csproj}", ".github/**/*.{yml,yaml}", "docker-compose.yml"]
---

# Tools rules

## AppHost (`tools/Trellis.AppHost`)

- Aspire orchestration: postgres (image major pinned — data volumes don't survive major bumps) → migrations (`WaitFor`) → api (`WaitForCompletion(migrations)`) → web. `aspire run` needs a real host, not the SDK container (ADR-0008).
- `AddDockerComposeEnvironment("compose")` backs `aspire publish`; images come from `dotnet publish /t:PublishContainer` — no Dockerfiles.

## MigrationService (`tools/Trellis.MigrationService`)

- `MigrationWorker` applies EF migrations, seeds, then `StopApplication()` — the API waits on successful exit; a failure sets `ExitCode = 1` so the wait fails loudly.
- **Non-HTTP host**: registers `SystemCurrentUser` for `ICurrentUser` and (with multi-tenancy) `SystemTenantContext` for `ITenantContext` — every new host-scoped port needs a system stand-in here or `AppDbContext` won't resolve.
- **Seed guards**: sample data (demo invoices) seeds in Development only; the worker **refuses to seed `SeedOptions.DefaultAdminPassword` outside Development** — override via the `Seed` section in real environments. `SeedOptions` is validated at boot (`ValidateDataAnnotations().ValidateOnStart()`; a failure exits 1), and roles and the admin go through `IdentitySeed` (Persistence), which throws on any failed `IdentityResult` — so a rejected password exits 1 instead of logging success.
- Permission seeding iterates `Roles.Permissions` (Application) — new permissions/roles flow through automatically; never hard-code them here.

## CI supply-chain gates

- **Central package management** (ADR-0029): versions live only in `Directory.Packages.props` (transitive pinning on); a package family that shares version floors moves in one Dependabot group (`.github/dependabot.yml`) — add a new family to its group in the same change.
- **NuGetAudit** (`all` + `low`, `Directory.Build.props`) fails Release and CI builds on any advisory, transitive included (NU1901–NU1904). Playbook in `docs/development.md` ("Vulnerable packages"): upgrade, else pin the transitive in `Directory.Packages.props` with the advisory in a comment, else `NuGetAuditSuppress` one advisory with its reason. Never lower the level or the mode to get a build through.
<!--#if (TemplateSource) -->

## Template source only (this section never ships)

`TemplateSource` is a constant that is false whenever `dotnet new` runs, so the engine drops this section from every generated project, which keeps no template directives.

- **Template toggles are `#if` blocks** (`UseRedis`, `IncludeUI`) processed at `dotnet new` time. The csproj defines `IncludeUI` in `DefineConstants` (inside an XML-comment `#if` directive on `IncludeUI`) so the template *source* compiles too — keep both sides in sync when adding a toggle (`add-toggle` skill).
- **YAML has no preprocessor.** `dotnet new` reads hash-comment directives in workflows, `docker-compose.yml` and `dependabot.yml`; in this repository every one of them is an ordinary comment.
  - **A plain `#if (X)` … `#else` … `#endif` block is live in BOTH branches here**: CI runs every line of each. Use it only when that is harmless (the `ui-tests` job in `ci.yml`, which ships only with `--include-ui-tests`).
  - **A branch that must stay inert here uses the doubled-hash form** `##if (X)` / `##else` / `##endif`: every line of the inert branch commented with one `#` in column 0, every other hash in the block doubled. The engine strips each single hash in the branch it keeps and turns `##` into `#`, so the commented lines come alive in generated output. Nothing nests inside such a block. Worked example: the Redis steps in `ci.yml`.
  - **`#if (TemplateSource)` marks source-only lines**: the block runs here and never ships (the `ci.yml` step that runs `scripts/check-template-toggles.sh`; this section, in the XML-comment form).
- **A directive token only opens its own line**, condition in parentheses, symbol declared in `template.json`. The engine matches the token anywhere in a line and truncates the shipped file from there, so never quote one inside a `run:` script or a YAML comment; the same holds for the XML-comment form in shipped `.md` and for `#if` in `.sh`. `scripts/check-template-toggles.sh` enforces all of this; files `template.json` excludes outright (`template-ci.yml`, `release.yml`) are never processed and not checked.
<!--#endif -->
