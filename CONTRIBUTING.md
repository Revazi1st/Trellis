# Contributing to Trellis

Thanks for helping improve Trellis. This is both a working solution and a
`dotnet new` template, so changes should keep the reference solution green and
the template instantiable.

A question, or an idea you want to try out before writing code? Start a thread in
[GitHub Discussions](https://github.com/Revazi1st/Trellis/discussions) (Q&A or Ideas); issues are for
bugs and feature requests.

## Getting started

With a local .NET 10 SDK:

```bash
dotnet build Trellis.slnx -c Release      # CI builds Release, where warnings are errors
dotnet test  Trellis.slnx                 # from the repository root: global.json selects the test platform
dotnet format Trellis.slnx
```

No local SDK? Everything except the Aspire orchestration runs through the SDK container:

```bash
docker build -t trellis-sdk:10.0 scripts/sdk-image   # one-time
./scripts/dotnet.sh build Trellis.slnx -c Release
./scripts/dotnet.sh test  Trellis.slnx
```

With a local SDK you can also `aspire run` the full app. See
[docs/development.md](docs/development.md).

## Before opening a PR

- `dotnet build Trellis.slnx -c Release` and `dotnet test Trellis.slnx` are green.
- `dotnet format --verify-no-changes` is clean.
- After an endpoint or contract change, `docs/api/openapi.v1.json` is regenerated
  (`UPDATE_OPENAPI_SNAPSHOT=1 dotnet test tests/Trellis.ApiTests`).
- New/changed features update their doc under `docs/features/` (`scripts/check-docs-drift.sh main`
  runs the CI check locally) and, if you made a load-bearing decision, add an ADR numbered below
  0100 (`docs/adr/`, see the `new-adr` skill).
- A user-visible change gets a line under `## [Unreleased]` in [CHANGELOG.md](CHANGELOG.md), written
  for the people who generate projects from the template.
- Follow the layer conventions in `.claude/rules/` and the boundaries in
  `AGENTS.md`. In particular: dependencies point inward, failures are `Result`s
  (not exceptions), migrations are generated (never hand-edited), and **no
  commercially-licensed packages** (see the ceilings in `Directory.Packages.props`).

## Testing the template

The repository builds and tests itself, but a change can still break the projects the template
generates: a conditional block, a renamed file, a toggle's off-branch. Template CI
(`.github/workflows/template-ci.yml`) instantiates every toggle leg, gates the generated content and
builds and tests the generated solutions. Before pushing a change to `.template.config/`, a
conditional block, or anything a toggle touches:

```bash
./scripts/check-template-toggles.sh                       # toggle wiring, directive placement, exclude globs
dotnet pack eng/Trellis.TemplatePack -c Release -o /tmp/trellis-nupkg
dotnet new install /tmp/trellis-nupkg/*.nupkg --debug:custom-hive /tmp/trellis-hive
dotnet new trellis -n Acme.Shop -o /tmp/acme --debug:custom-hive /tmp/trellis-hive \
    --use-redis true --use-serilog true --use-multitenancy true --include-ui-tests true
dotnet build /tmp/acme/Acme.Shop.slnx -c Release
```

The private hive (`--debug:custom-hive`) keeps the experiment out of your real template list. The
all-options leg also regenerates the initial migration before building (as `template-ci.yml` does).
The `add-toggle` skill covers adding a toggle; `docs/releasing.md` covers releasing.

## Commit style

Conventional commits (`feat:`, `fix:`, `docs:`, `chore:`, `test:`). Keep each
commit green and self-contained.

## Reporting security issues

Do not open a public issue for vulnerabilities — see [SECURITY.md](SECURITY.md).
