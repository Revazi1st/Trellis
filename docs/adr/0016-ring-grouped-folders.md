# ADR-0016: Ring-grouped source folders

- **Status**: accepted
- **Date**: 2026-07-15

## Context

Clean Architecture solutions can lay projects out flat under `src/` or group them. Flat lists get noisy as projects multiply (this template has ~11 source projects across the onion); readers (and agents) need the layer of a project to be obvious at a glance.

## Decision

Group projects under `src/` by architectural ring, and mirror the layout with solution folders in the `.slnx`:

- `src/Core/` — SharedKernel, Domain, Application (the inside of the onion; pure C#).
- `src/Infrastructure/` — Infrastructure ∥ Persistence (driven adapters; peers).
- `src/Presentation/` — `Api/` (Api.Contracts, Api.Common, Api) and `UI/` (UI.Common, UI.Web).
- `src/Shared/` — ServiceDefaults and other cross-host libraries.

`tools/` holds AppHost + MigrationService; `tests/` stays flat (project names already encode their target).

## Consequences

- A path names its ring: `src/Core/...` is inside the onion, `src/Presentation/...` is an adapter.
- Matches how the template authors (and most readers) think about the rings, and keeps the `.slnx` solution tree tidy.
- Folder depth is one level deeper than a flat layout — csproj relative paths account for it.
