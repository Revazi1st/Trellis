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
- A change that copies or adapts someone else's work (an idea, a pattern, a layout, text or code)
  adds a line to the README's [Credits](README.md#credits) in the same PR: project, author, license
  and what was taken. Projects we only compared against get no line. Verbatim MIT or BSD material
  also brings its copyright and permission notice into a `THIRD-PARTY-NOTICES.md`, listed beside
  `LICENSE` under `copyOnly` in `.template.config/template.json` so the rename never rewrites it.
- Follow the layer conventions in `.claude/rules/` and the boundaries in
  `AGENTS.md`. In particular: dependencies point inward, failures are `Result`s
  (not exceptions), migrations are generated (never hand-edited), and **no
  commercially-licensed packages** (see the ceilings in `Directory.Packages.props`).

## Testing the template

The repository builds and tests itself, but a change can still break the projects the template
generates: a conditional block, a renamed file, a toggle's off-branch. Template CI
(`.github/workflows/template-ci.yml`) instantiates every toggle leg, gates the generated content,
builds the generated solutions and tests them (the Aspire E2E that boots each generated AppHost
included), and repeats the default leg on Windows (build and the tests that need no Docker). Before
pushing a change to `.template.config/`, a conditional block, or anything a toggle touches, run the
same legs on your machine (a local .NET SDK and a running Docker):

```bash
./scripts/test-template.sh --list                # the legs, read from template-ci.yml's matrix
./scripts/test-template.sh no-ui all-options     # the legs your change touches
./scripts/test-template.sh                       # every leg (a few minutes each)
```

It runs `scripts/check-template-toggles.sh` (toggle wiring, directive placement, exclude globs),
packs the template once (the working tree as it is, untracked files included), installs the package
into a private hive (`--debug:custom-hive`, so your real template list stays untouched), and for each
leg instantiates it with the leg's flags, regenerates the initial migration where the leg does,
builds Release and runs the tests on the legs Template CI tests. It prints a table of results and
exits 1 on any failure, keeping the logs and the generated solutions in the folder it names.
`RUN_ASPIRE_E2E=0` skips the Aspire E2E. The content gates (leftovers of the template's name, the
consumer files, actionlint over the generated workflows) and the Windows leg run only in Template CI.
The `add-toggle` skill covers adding a toggle; `docs/releasing.md` covers releasing.

## Commit style

Conventional commits (`feat:`, `fix:`, `docs:`, `chore:`, `test:`, `ci:`, `build:`; Dependabot's
are `build(deps):`, set in `.github/dependabot.yml`). Keep each commit green and self-contained.

## Reporting security issues

Do not open a public issue for vulnerabilities — see [SECURITY.md](SECURITY.md).
