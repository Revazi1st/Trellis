# ADR-0008: Containerized SDK workflow and where Aspire orchestration runs

- **Status**: accepted
- **Date**: 2026-07-15
<!--#if (IncludeUiTests && IncludeUI) -->
- **Amended**: 2026-11-21 — the browser tests start the AppHost too (see *Amendment* below)
<!--#endif -->

## Context

The template authors' development machine had no local .NET SDK when this was decided, and the template must stay usable that way: all `dotnet` work can run in the `mcr.microsoft.com/dotnet/sdk:10.0` container via `scripts/dotnet.sh`. That wrapper mounts the host Docker socket, so **Testcontainers**-based integration tests happily start sibling database containers on the host and reach them.

Aspire is different. Its orchestrator (DCP) runs *project* resources (api, web, migrations) as **local processes inside the SDK container** while *container* resources (postgres, pgadmin) land on the **host** as siblings. The two networks don't line up: the api process inside the container cannot reach the postgres container on the host, so `aspire run` and `Aspire.Hosting.Testing` E2E tests **hang** waiting for `api` to become healthy. This was confirmed empirically (a 40-minute hang; DCP had started postgres+pgadmin but api never connected).

## Decision

- **Build, unit tests, architecture tests, and Testcontainers integration tests** run in the SDK container — the default `scripts/dotnet.sh` path. To give Aspire's DCP a container runtime to talk to, the wrapper uses a thin local image (`scripts/sdk-image/`, tag `trellis-sdk:10.0`) that adds the `docker` CLI to the base SDK image.
- **Aspire orchestration** (`aspire run`, the AppHost F5 loop, `aspire publish/deploy`) and the **`Aspire.Hosting.Testing` E2E test** require a real host environment — a **local .NET 10 SDK** or **CI**. They are not supported from inside the SDK container.
- The E2E test (`Trellis.ApiTests.InvoiceApiFlowTests`) is **opt-in**: it runs only when `RUN_ASPIRE_E2E=1`, and otherwise skips with an explanatory message, so the containerized `dotnet test` stays green. CI sets `RUN_ASPIRE_E2E=1` (GitHub Actions ubuntu runners have both a local SDK and a host Docker daemon — the natively supported layout). Everything else in `Trellis.ApiTests` runs on every `dotnet test`, the SDK container included: the in-process suites (`ApiTests/InProcess`, the real HTTP pipeline through `WebApplicationFactory` against a Testcontainers PostgreSQL), the outbound-integration and service-defaults tests, the contract-serialization guards and, with the UI, `ApiTests/Ui`.

## Consequences

- The inner loop (edit → build → unit/integration test) is fully containerized and fast.
- Running the whole app locally (`aspire run`) or the E2E suite needs a local SDK install — the one workflow the container can't cover. Documented in `AGENTS.md` and `README`.
- CI is the authoritative environment for the Aspire E2E test.
<!--#if (IncludeUiTests && IncludeUI) -->

## Amendment (2026-11-21): the browser tests start the AppHost too

The decision named one test that needs Aspire's orchestrator, the E2E. The browser tests
(`tests/Trellis.UiTests`) are a second: with `RUN_UI_TESTS=1` their fixture (`AppHostFixture`) starts the
AppHost through `Aspire.Hosting.Testing`, as the E2E does, and the tests drive the web host it starts.
Before, they drove an application someone else had started: a developer by hand, and CI's `ui-tests` job
in three steps of shell (a compose database, the migration job, the two hosts), which nothing held to
what the AppHost declares.

- **The same place and the same kind of switch.** They need a local .NET SDK or CI, and Docker. Through
  `scripts/dotnet.sh` they skip: it does not pass `RUN_UI_TESTS` into the SDK container, and the fixture
  starts nothing until a test is past that guard.
- **In the dev container nothing is started either.** A terminal there does pass the variable on, and
  the AppHost cannot orchestrate from inside a container (*Context* above). The fixture sees the
  container (`DOTNET_RUNNING_IN_CONTAINER`, which .NET's images set) and every browser test fails at
  once with one sentence: run the web host from its launch profile and name it,
  `UI_TESTS_BASE_URL=http://localhost:5170`.
- **One application per test run**, on a database server of the run's own (`UseThrowawayDatabase`, as
  the E2E): never the container or the volume `aspire run` keeps. The step refuses an app model in
  which any other resource keeps a volume, a writable bind mount or a persistent lifetime, so a
  resource added later cannot make a test share state with `aspire run` unnoticed.
- **`UI_TESTS_BASE_URL` still names an application that is already running**, and then nothing is
  started, in a container or outside one.
- **The project, not the solution**: `dotnet test tests/Trellis.UiTests`. In a run of the whole solution
  with the variable set, the AppHost's processes and Testcontainers would race for host ports, the
  reason the E2E is a CI step of its own.
- **A failed start is read from the run**: a resource the application needs (the web host and
  everything it waits for) that fails to start or exits with a code other than 0 ends the start at once,
  since Aspire's own start goes on waiting for a dependency that has exited; the failing tests' message
  names that resource, each resource's state, and the first error lines and the last lines of each of
  the application's processes and of the AppHost; and the fixture writes what every resource printed to
  a file per resource, which CI prints and keeps.

Considered: one application per test class (four starts and four sets of containers at once, for an
isolation only the first-run pictures need, which get it from a test run of their own); the hosts in
process through `WebApplicationFactory` on real listeners (two hosts, the migration job and a database
wired together by hand in the test, which is what the removed shell steps did and is not what
`aspire run` starts).
<!--#endif -->
