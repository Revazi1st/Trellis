---
name: add-toggle
description: Wire a new template toggle (boolean parameter symbol) through template config, slnx/csproj conditionals, C# #if blocks, CI and docs. Maintainer task — every site must use the same symbol name.
disable-model-invocation: false
---

# Add a template toggle

One PascalCase symbol (`UseThing`) everywhere; the kebab-case CLI flag (`--use-thing`) is declared in exactly one place. `UseMultitenancy` is the complete worked example — grep it when unsure; ADR-0019 shows the decision write-up.

## The coordinated places

1. **`.template.config/template.json`** — the symbol under `symbols`: `"type": "parameter"`, `"datatype": "bool"`, `defaultValue`, `displayName`, `description`. If whole files drop out when the toggle is off, add a `sources.modifiers` entry — `"condition": "(!UseThing)"` + `exclude` globs (see the `UseMultitenancy` and `IncludeUI` modifiers).
2. **`.template.config/dotnetcli.host.json`** — a `symbolInfo` entry mapping the symbol to its kebab-case `longName` flag.
3. **`.template.config/ide.host.json`** — a `symbolInfo` array entry (`id`, `name.text`, `isVisible: true`) so VS/Rider render the checkbox.
4. **`Trellis.slnx`** — wrap conditional `<Project>`/`<Folder>` entries in `<!--#if (UseThing) --> … <!--#endif -->` (ADR-0010). The slnx condition must match the `sources.modifiers` condition exactly — an `IncludeUiTests` mismatch once shipped solutions that could not restore.
5. **csproj files** — the same XML-comment conditionals around `PackageReference`/`ProjectReference`/`PropertyGroup` blocks (`tools/Trellis.AppHost/Trellis.AppHost.csproj` shows all three).
6. **C# `#if (UseThing)` blocks** — same symbol name, parenthesized (template-engine condition syntax; also valid C#). The template source defines no such compiler symbol, so a bare `#if` compiles the **off** branch in this repo:
   - default-off toggle: fine — the on branch is compiled only by the template-CI leg that enables it.
   - default-on toggle (or code that must compile here): add the DefineConstants trick — a `<!--#if (UseThing) -->`-guarded `<DefineConstants>$(DefineConstants);UseThing</DefineConstants>` PropertyGroup, as in the AppHost and ArchitectureTests csproj. Removed at `dotnet new` time when the toggle is off; harmless when on.
7. **`.github/workflows/template-ci.yml`** — add the flag to the `all-options` matrix leg; give a risky combination its own leg, with `test: true` if a failure could be runtime-only (a green build once hid the `ITenantContext` DI crash).
8. **The READMEs** — the toggle table in `README.md` and the toggle line in `eng/Trellis.TemplatePack/README.md` (that one ships to nuget.org).
9. **Shipped YAML** (`.github/workflows/ci.yml`, `docker-compose.yml`, `.github/dependabot.yml`) — only if the toggle changes them; read the next section first.

## WARNING: YAML conditionals are live in both branches here

YAML has no preprocessor. `dotnet new` reads hash-comment directives in YAML, but in this repository every directive is an ordinary comment, so **both branches of a plain `#if (UseThing)` … `#else` … `#endif` block run** in the template's own CI. Choose the form by what must happen here:

- **Both branches harmless side by side here** (or one is empty): the plain form. Generated output keeps only the selected branch. Example: the `ui-tests` job in `ci.yml`.
- **One branch must stay inert here** (say, two steps that start the same service): the doubled-hash form `##if (UseThing)` / `##else` / `##endif`. Comment every line of the inert branch with one `#` in column 0 and double every other hash inside the block. The engine strips each single hash in the branch it keeps and turns `##` into `#`, so the commented lines come alive in generated output. Nothing may nest inside such a block. Example: the Redis steps in `ci.yml`.
- **Source-only** (a step or comment that must never ship): `#if (TemplateSource)` … `#endif`. `TemplateSource` is a generated constant that is false whenever `dotnet new` runs, so the block runs here and disappears from every generated project. Examples: the `ci.yml` step that runs `check-template-toggles.sh`; in a shipped Markdown file the same condition in the XML-comment form hides maintainer-only prose (the last section of `.claude/rules/tools.md`).
- **A directive token only ever opens its own line**, with the condition in parentheses and a symbol declared in `template.json`. The engine matches a token anywhere in a line and truncates the shipped file from that point, so never quote one inside a `run:` script, a YAML comment, a shell line or Markdown prose (Markdown's token is the XML-comment form). Files `template.json` excludes unconditionally (`template-ci.yml`, `release.yml`, this skill) are never processed.

## WARNING: no MSBuild `Condition` containing item syntax

`dotnet new` **evaluates** `Condition` attributes that contain item/metadata syntax (`@(...)`, `%(...)`) and mangles them in generated output — a condition that works in this repo comes out broken for consumers. That is why templating uses XML-comment conditionals (ADR-0010), and why the `EnforceLicenseCeilings` target in `Directory.Build.props` is contorted into a double-`Exclude` intersection plus a property-only `Error` condition (see its comment). Property-only conditions (`'$(Prop)' == '…'`) are safe.

## Verify

- `./scripts/dotnet.sh build Trellis.slnx && ./scripts/dotnet.sh test Trellis.slnx` — the repo itself must stay green.
- `scripts/check-template-toggles.sh` — both host files expose the symbol, every `#if` symbol is declared (C#, project, solution, JSON and Razor files, the consumer stubs), YAML, Markdown and shell directive tokens sit alone on their lines, YAML blocks balance, doubled-hash blocks keep their hashes legal.
- Template CI is the real gate: every matrix leg instantiates and builds the generated solution (and runs its tests on `test: true` legs). If the toggle's failure modes are visible only at run time, make sure some leg executes them.
