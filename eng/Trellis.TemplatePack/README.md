# Trellis.Templates

Trellis is a dotnet new template for .NET 10 that generates a rich-DDD solution with Clean Architecture the tests enforce.

```bash
dotnet new install Trellis.Templates
dotnet new trellis -n Your.Project
```

Documentation: <https://revazi1st.github.io/Trellis/>

Name the project with dot-separated parts made of letters and digits, each starting with a letter
(`Acme.Shop`): hyphens, spaces and underscores turn into underscores in namespaces, which analyzer rule
CA1707 rejects.

Options are flags of `dotnet new`, and options in the Visual Studio and Rider new-solution dialogs.

| Option | Default | What it does |
|---|---|---|
| [`--include-ui`](https://revazi1st.github.io/Trellis/start/options/#--include-ui) | on | Include the Blazor web UI (UI.Common + UI.Web) and wire it into the Aspire AppHost. |
| [`--use-redis`](https://revazi1st.github.io/Trellis/start/options/#--use-redis) | off | Add a Redis-protocol distributed cache (HybridCache L2), Valkey by default, and wire it into the Aspire AppHost. |
| [`--use-serilog`](https://revazi1st.github.io/Trellis/start/options/#--use-serilog) | off | Use Serilog for structured logging (console sink) in the API and web hosts, alongside OpenTelemetry. |
| [`--use-multitenancy`](https://revazi1st.github.io/Trellis/start/options/#--use-multitenancy) | off | Add a tenant seam: a tenant claim on the principal (ITenantContext), a TenantId shadow column and a "Tenant" EF query filter on every aggregate root. Regenerate the initial migration after enabling. |
| [`--include-ui-tests`](https://revazi1st.github.io/Trellis/start/options/#--include-ui-tests) | off | Include a browser-driven UI test project (Playwright). Tests are opt-in at run time via RUN_UI_TESTS=1. |
| [`--skip-restore`](https://revazi1st.github.io/Trellis/start/options/#--skip-restore) | off | Skip the automatic 'dotnet restore' after the project is created. |

What a generated solution contains: <https://github.com/Revazi1st/Trellis/blob/main/docs/overview.md>

The generated project gets its own README and an empty CHANGELOG, plus `.trellis-template.json`
recording this package's version and the options you chose; its `docs/upgrading.md` uses that file to
adopt a later version. Release notes: <https://github.com/Revazi1st/Trellis/blob/main/CHANGELOG.md>.
Credits for the ideas the template borrows: <https://github.com/Revazi1st/Trellis#credits>.
