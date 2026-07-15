---
description: Blazor conventions for the Presentation/UI layer
paths: ["src/Presentation/UI/**/*.{cs,razor}"]
---

# UI layer rules

`UI.Common` is the shared Razor Class Library for the Blazor web host. Keep it **render-mode-agnostic** — only `Microsoft.AspNetCore.Components.*` + `HttpClient`, no host-specific APIs — so it stays cleanly reusable (ADR-0015).

- **Talk to the API only through the typed clients** (`IInvoiceApiClient`, `IAuthApiClient`) over `Api.Contracts`; they return `ApiResult<T>` so components handle failures without exceptions.
- **Protected pages** inherit `ProtectedPage` (component-level auth guard) and override `OnAuthorizedAsync`. Do **not** use endpoint `[Authorize]` — client-token Blazor Server has no server auth scheme to challenge (ADR-0007).
- **Permission-gated UI** uses `<AuthorizeView Roles="permission:name">` (the auth state provider maps permission claims to roles).
- **Platform differences** go behind seams: `ITokenStore` (host-provided), API base address (host-provided). Never bake in a host-specific detail.
