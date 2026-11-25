# ADR-0012: Presentation split into Api and UI, each with a Common library

- **Status**: accepted
- **Date**: 2026-07-14
- **Amended**: 2026-11-21 — a test project may reference a host (see *Amendment* below)

## Context

The template authors wanted a Presentation layer with `Api/` and `UI/` folders, each with a `Common` class library, so that if a second API (or UI) is added, shared plumbing has a home rather than being duplicated or dumped into one host.

## Decision

`src/Presentation/` has two families:

- **`Api/`** — `Api.Contracts` (pure wire DTOs, ADR-0013), `Api.Common` (reusable host plumbing: `IEndpoint`+`MapEndpoints`, `CustomResults`/ProblemDetails, JwtBearer + permission authorization, OpenAPI+Scalar, typed-ID JSON), and `Api` (the host: composition root + feature endpoint slices).
- **`UI/`** — `UI.Common` (a render-mode-agnostic Razor Class Library: pages, components, typed API client, auth state) and `UI.Web` (the Blazor Web App host).

`Api.Common` and `UI.Common` are deliberate **template seams**: a second consumer (a second API host, a WASM/MAUI UI) drops in without refactoring. In the shipped template each has a single consumer — the seam is prepared, not yet exercised.

## Consequences

- A second host is additive, not a refactor.
- Small upfront cost: one extra project per family with a single consumer today; the arrangement is the point of the template.
- Architecture tests enforce the reference direction (`Api → Api.Common`, `UI.Web → UI.Common`); `Api.Contracts` stays reference-free.

## Amendment (2026-11-21): a test project may reference a host

The reference direction above is about the projects under `src/`. A test project references what it
exercises, a host included:

- `ApiTests` references the `Api` host, because `WebApplicationFactory<Program>` needs its entry point,
  and the AppHost, for the Aspire end-to-end test and the tests that read its app model. Both declare a
  top-level `Program`, so the `Api` reference carries an alias (`Aliases="apihost"`) and a test names the
  one it boots (`extern alias apihost;`, `WebApplicationFactory<apihost::Program>`).
<!--#if (IncludeUI) -->
- `ApiTests` also references `UI.Common`, for the client and page tests in `Ui/`, and the `UI.Web` host,
  which `Ui/WebHostTests` boots in Production mode to prove what only the host answers (`/Error`). It is
  a third `Program`, so it carries an alias of its own (`Aliases="webhost"`, `extern alias webhost;`).
<!--#endif -->
<!--#if (IncludeUiTests && IncludeUI) -->
- `UiTests` references the AppHost and ServiceDefaults: its fixture starts the application and waits for
  its resources by their `ServiceNames`.
<!--#endif -->

An alias is test-only plumbing and changes nothing in what ships. A reviewer reads a test project's
reference to a host as allowed when a test in it boots that host or starts it through the AppHost.
<!--#if (IncludeUI) -->

Weighed against a test project of its own for the `UI.Web` host, which would need no alias: that is one
more project for one test class. This decision already counts an extra project as a cost worth paying
where a second consumer is expected, and such a test project has none. So the reference stays.
<!--#endif -->
