# ADR-0015: Blazor everywhere — one component library for web and mobile

- **Status**: accepted
- **Date**: 2026-07-15

## Context

The Presentation layer needs a web UI now and a mobile app later. The user chose Blazor specifically so the *same* Razor components serve both a web host and a .NET MAUI Blazor Hybrid mobile host, rather than reimplementing screens per platform.

## Decision

`XTemplate.UI.Common` is a Razor Class Library holding all pages, components, the typed API client (over `Api.Contracts`), the client-side auth state, and shared styling. It is consumed by:

- **`UI.Web`** — a Blazor Web App (InteractiveServer) — built now.
- **`UI.Mobile`** — a MAUI Blazor Hybrid host (`BlazorWebView`) — **deferred** (ADR-0008: MAUI can't build in the Linux SDK container; it needs a local SDK + `maui` workload).

To stay host-agnostic, `UI.Common`:

- Uses **no web-only or MAUI-only APIs** — only `Microsoft.AspNetCore.Components.*` and `HttpClient`.
- Puts platform differences behind seams: **`ITokenStore`** (UI.Web = per-circuit in-memory; UI.Mobile = `SecureStorage`) and the API base address supplied by the host (UI.Web = Aspire service discovery; UI.Mobile = per-platform, e.g. Android emulator `10.0.2.2`).
- Guards protected pages at the **component level** (`ProtectedPage` base + `AuthenticationStateProvider`), not with endpoint `[Authorize]` — endpoint authorization would try to challenge a server auth scheme that client-token auth doesn't have (ADR-0007). This model works identically in both hosts.

## Consequences

- Screens are written once; the mobile head is mostly a host project + `ITokenStore` implementation when it lands.
- The render-mode-agnostic constraint is a real discipline (an arch/analyzer check for web/MAUI-only namespaces in UI.Common is a candidate for M9).
- Blazor Server auth here is demo-grade (per-circuit token); production guidance is BFF/cookies (ADR-0007).
