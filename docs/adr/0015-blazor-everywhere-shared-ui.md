# ADR-0015: Shared render-mode-agnostic UI component library

- **Status**: accepted
- **Date**: 2026-07-15

## Context

The Presentation layer needs a web UI. Blazor was chosen partly so the UI could be a reusable component library rather than markup welded to one host — keeping the door open to additional heads (e.g. a .NET MAUI Blazor Hybrid mobile app) without reimplementing screens.

## Decision

`XTemplate.UI.Common` is a Razor Class Library holding all pages, components, the typed API client (over `Api.Contracts`), the client-side auth state, and shared styling. It is consumed by **`UI.Web`**, a Blazor Web App (InteractiveServer).

To stay reusable, `UI.Common` is kept **render-mode-agnostic**:

- Uses **no host-specific APIs** — only `Microsoft.AspNetCore.Components.*` and `HttpClient`.
- Puts host differences behind seams: **`ITokenStore`** (UI.Web = per-circuit in-memory) and the API base address, both supplied by the host.
- Guards protected pages at the **component level** (`ProtectedPage` base + `AuthenticationStateProvider`), not with endpoint `[Authorize]` — endpoint authorization would try to challenge a server auth scheme that client-token auth doesn't have (ADR-0007).

A MAUI mobile head is **not** part of the template (it needs the `maui` workload + platform SDKs, and can't build in the containerized flow — ADR-0008). The render-mode-agnostic discipline is cheap insurance that keeps it addable later.

## Consequences

- Screens are written once in a reusable library; the web host is thin.
- The render-mode-agnostic constraint is a real discipline (a candidate arch/analyzer check for host-only namespaces in `UI.Common`).
- Blazor Server auth here is demo-grade (per-circuit token); production guidance is BFF/cookies (ADR-0007).
