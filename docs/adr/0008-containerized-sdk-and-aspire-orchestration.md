# ADR-0008: Containerized SDK workflow and where Aspire orchestration runs

- **Status**: accepted
- **Date**: 2026-07-15

## Context

This machine has no local .NET SDK; all `dotnet` work runs in the `mcr.microsoft.com/dotnet/sdk:10.0` container via `scripts/dotnet.sh`. That wrapper mounts the host Docker socket, so **Testcontainers**-based integration tests happily start sibling database containers on the host and reach them.

Aspire is different. Its orchestrator (DCP) runs *project* resources (api, web, migrations) as **local processes inside the SDK container** while *container* resources (postgres, pgadmin) land on the **host** as siblings. The two networks don't line up: the api process inside the container cannot reach the postgres container on the host, so `aspire run` and `Aspire.Hosting.Testing` E2E tests **hang** waiting for `api` to become healthy. This was confirmed empirically (a 40-minute hang; DCP had started postgres+pgadmin but api never connected).

## Decision

- **Build, unit tests, architecture tests, and Testcontainers integration tests** run in the SDK container — the default `scripts/dotnet.sh` path. To give Aspire's DCP a container runtime to talk to, the wrapper uses a thin local image (`scripts/sdk-image/`, tag `trellis-sdk:10.0`) that adds the `docker` CLI to the base SDK image.
- **Aspire orchestration** (`aspire run`, the AppHost F5 loop, `aspire publish/deploy`) and the **`Aspire.Hosting.Testing` E2E test** require a real host environment — a **local .NET 10 SDK** or **CI**. They are not supported from inside the SDK container.
- The E2E test (`Trellis.ApiTests.InvoiceApiFlowTests`) is **opt-in**: it runs only when `RUN_ASPIRE_E2E=1`, and otherwise skips with an explanatory message, so the containerized `dotnet test` stays green. CI sets `RUN_ASPIRE_E2E=1` (GitHub Actions ubuntu runners have both a local SDK and a host Docker daemon — the natively supported layout). `Trellis.ApiTests` also carries fast contract-serialization tests that always run, so the assembly never reports "zero tests ran".

## Consequences

- The inner loop (edit → build → unit/integration test) is fully containerized and fast.
- Running the whole app locally (`aspire run`) or the E2E suite needs a local SDK install — the one workflow the container can't cover. Documented in `AGENTS.md` and `README`.
- CI is the authoritative environment for the Aspire E2E test.
