# ADR-0013: Api.Contracts as the API's public surface

- **Status**: accepted
- **Date**: 2026-07-15

## Context

The API host, the Blazor web client, and the E2E tests all need to agree on request/response shapes. Left to each side, the DTOs drift.

## Decision

`XTemplate.Api.Contracts` holds the API's public wire contracts — pure request/response records, nothing else. It has **no project references** and no logic (arch-test enforced). It lives under `Presentation/Api` (not the Infrastructure group): contracts between the API and its clients are presentation surface, not driven adapters. The core's own "contracts" are the Application ports.

Consumers: `Api` binds them in endpoints and maps them to/from Application commands/queries; `UI.Common` uses them in its typed API client; `ApiTests` uses them for typed E2E calls.

## Consequences

- One definition of the wire shape, referenced everywhere — no drift between server and clients.
- Contracts stay free of domain types and behavior; mapping Application responses → contracts is explicit in the endpoints (`ContractMappings`).
- A future public client SDK can ship this assembly as-is.
- No versioning machinery in v1 (single `/api/v1` group); when needed, versioned contract namespaces are an additive change.
