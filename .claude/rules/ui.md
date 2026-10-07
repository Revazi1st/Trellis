---
description: Blazor conventions for the Presentation/UI layer
paths: ["src/Presentation/UI/**/*.{cs,razor}"]
---

# UI layer rules

`UI.Common` is the shared Razor Class Library for the Blazor web host. Keep it **render-mode-agnostic** — only `Microsoft.AspNetCore.Components.*` + `HttpClient`, no host-specific APIs — so it stays cleanly reusable (ADR-0015).

- **Talk to the API only through the typed clients** (`IInvoiceApiClient`, `IAuthApiClient`) over `Api.Contracts`; they return `ApiResult<T>` so components handle failures without exceptions. The typed client sends a **fresh `Idempotency-Key` per logical POST** (retry attempts share it) — never POST with a raw `HttpClient`.
- **Failures are described in one place**: `ApiResponses` (Services/) turns a failed call into `ApiResult.Error`, keyed on the problem's `code`, falling back to status and `detail`. A 400/403/409 shows the server's `detail` (a 400 with validation errors shows those errors), a 429 a `Retry-After` hint, a 5xx or an unreachable API a service-unavailable sentence. Pages display `result.Error` and never write their own sentence for a 4xx; code that needs more branches on `result.Problem.Code` / `result.StatusCode` (`Problem` is `Api.Contracts.Common.ProblemResponse`, `Errors` its field errors) and never parses `title`.
- **Session lifetime** is handled for you: `TokenAuthenticationStateProvider` (also the `IAccessTokenSource`) rotates expired access tokens through `/auth/refresh` (single-flight, 30 s before expiry), and `BearerTokenHandler` attaches the currently-valid token to every request. Only a **401** from refresh ends the session; a 429, a 5xx or no response keeps the stored pair, holds off the next attempt for 5 s (`TimeProvider`-driven) and hands back the stored token, so a protected page does not bounce to sign-in (ADR-0021). Components never touch tokens directly.
- **Protected pages** inherit `ProtectedPage` (component-level auth guard) and override `OnAuthorizedAsync`. Do **not** use endpoint `[Authorize]` — client-token Blazor Server has no server auth scheme to challenge (ADR-0007).
- **Permission-gated UI** uses `<AuthorizeView Roles="permission:name">` (the auth state provider maps permission claims to roles).
- **Platform differences** go behind seams: `ITokenStore` (host-provided), API base address (host-provided), `UiOptions` (host-configured; `ShowDevelopmentHints` comes from the host's `IsDevelopment()` and gates the seeded-admin hint on `/login`). Never bake in a host-specific detail.
- **Browser hardening lives in the host**: UI.Web's `Program.cs` sends `Content-Security-Policy` (`script-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'`), `X-Frame-Options: DENY`, `nosniff` and `Referrer-Policy: same-origin` on every response. Keep scripts in files (no inline `<script>`); `style-src` stays open because Blazor's reconnect overlay injects an inline `<style>`.
- **Tests** live in `tests/Trellis.ApiTests/Ui/` behind the `IncludeUI` guard (csproj `<!--#if (IncludeUI) -->`, files `#if (IncludeUI)`, and the folder is on template.json's `(!IncludeUI)` exclude list so `--include-ui false` output does not ship empty files): `UiHarness` runs the real `AddSharedUi` graph over a scripted `HttpMessageHandler` and a manual clock — no browser, no host.
