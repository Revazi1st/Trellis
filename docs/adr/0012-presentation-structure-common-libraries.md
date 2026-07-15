# ADR-0012: Presentation split into Api and UI, each with a Common library

- **Status**: accepted
- **Date**: 2026-07-14

## Context

The user wants a Presentation layer with `Api/` and `UI/` folders, each with a `Common` class library, so that if a second API (or UI) is added, shared plumbing has a home rather than being duplicated or dumped into one host.

## Decision

`src/Presentation/` has two families:

- **`Api/`** — `Api.Contracts` (pure wire DTOs, ADR-0013), `Api.Common` (reusable host plumbing: `IEndpoint`+`MapEndpoints`, `CustomResults`/ProblemDetails, JwtBearer + permission authorization, OpenAPI+Scalar, typed-ID JSON), and `Api` (the host: composition root + feature endpoint slices).
- **`UI/`** — `UI.Common` (a render-mode-agnostic Razor Class Library: pages, components, typed API client, auth state) and `UI.Web` (the Blazor Web App host).

`Api.Common` and `UI.Common` are deliberate **template seams**: a second consumer (a second API host, a WASM/MAUI UI) drops in without refactoring. In the shipped template each has a single consumer — the seam is prepared, not yet exercised.

## Consequences

- A second host is additive, not a refactor.
- Small upfront cost: one extra project per family with a single consumer today; the arrangement is the point of the template.
- Architecture tests enforce the reference direction (`Api → Api.Common`, `UI.Web → UI.Common`); `Api.Contracts` stays reference-free.
