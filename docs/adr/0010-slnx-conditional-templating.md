# ADR-0010: Conditional projects in the .slnx template

- **Status**: accepted
- **Date**: 2026-07-15

## Context

The template must conditionally exclude projects (e.g. the Blazor UI when `IncludeUI=false`). The `dotnet new` engine excludes source *files/folders* via `sources.modifiers`, but the solution file still lists the excluded projects unless it is itself conditionally processed. Prior art warned that conditional processing inside the new XML `.slnx` format was unproven (dotnet/templating #8784), suggesting a classic `.sln` with `#if` blocks as a fallback.

## Decision

Ship a **`.slnx`** and use **XML-comment conditionals** inside it:

```xml
<!--#if (IncludeUI) -->
<Folder Name="/src/Presentation/UI/">
  <Project Path="src/Presentation/UI/Trellis.UI.Web/Trellis.UI.Web.csproj" />
</Folder>
<!--#endif -->
```

Verified empirically on the .NET 10 SDK template engine: `IncludeUI=false` strips the block (no UI projects, no leftover markers); `IncludeUI=true` keeps it clean. The content between the comment markers is active XML, so the `.slnx` remains a valid, buildable solution in the template repo itself.

Inline C# conditionals (AppHost web wiring) use `#if (IncludeUI)`. Because the raw template source has no such preprocessor symbol, the AppHost project defines `IncludeUI` (via a `<!--#if-->`-guarded `DefineConstants`) so the repo compiles the "on" branch during development; the engine resolves the real condition at instantiation.

## Consequences

- No `.sln` fallback needed — one modern `.slnx`, and the repo stays a runnable solution.
- The exclusion pattern is uniform: `sources.modifiers` removes the folder, the `.slnx` conditional removes the references, and `#if` removes inline wiring.
- Re-verify on major SDK bumps (the behavior is empirical, not documented); the `template-ci.yml` instantiate-and-build matrix guards it.
