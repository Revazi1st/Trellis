# What you get

The capabilities this solution has, each with the doc that explains it and the decision behind it.

## Domain and use cases

| Capability | Explained in | Decision |
|---|---|---|
| Rich aggregates with factories and private EF constructors, value objects as EF complex types, hand-rolled strongly-typed ids and specifications | [architecture.md](architecture.md#key-patterns) | [ADR-0005](adr/0005-hand-rolled-strongly-typed-ids.md), [ADR-0006](adr/0006-ef-complex-types-and-interceptors.md), [ADR-0017](adr/0017-hand-rolled-specifications.md) |
| CQRS without a mediator: an endpoint injects its handler, and a decorator pipeline (tracing, logging, validation, unit of work) wraps every handler | [architecture.md](architecture.md#a-request-stage-by-stage) | [ADR-0003](adr/0003-hand-rolled-cqrs.md) |
| `Result` instead of exceptions, mapped at one seam to RFC 9457 problem details that carry a stable `code` | [architecture.md](architecture.md#key-patterns) | [ADR-0004](adr/0004-result-over-exceptions.md), [ADR-0024](adr/0024-problem-details-v2.md) |
| A discarded `Result` fails the Release build (RSLT001) | [development.md](development.md#when-the-build-fails) | [ADR-0030](adr/0030-discarded-result-analyzer.md) |
| The reference slice, the shape to copy for a feature of your own | [features/invoices.md](features/invoices.md) | none |
| Scheduled use cases: a hosted service that sends one command per due aggregate through the whole pipeline | [architecture.md](architecture.md#scheduled-use-cases) | none |

## API

| Capability | Explained in | Decision |
|---|---|---|
| One endpoint class per use case on the versioned `/api/v1` group, mapping a `Result` to a response | [features/invoices.md](features/invoices.md) | [ADR-0003](adr/0003-hand-rolled-cqrs.md) |
| The OpenAPI document, one per API version, committed and compared with the served one on every test run | [architecture.md](architecture.md#key-patterns), [api/openapi.v1.json](api/openapi.v1.json) | [ADR-0025](adr/0025-openapi-truth.md) |
| Idempotency keys: a write retried with the same key is applied once, and the stored response is replayed | [deployment.md](deployment.md#idempotency-keys) | [ADR-0018](adr/0018-transactional-outbox-and-idempotency.md) |
| ETags and `If-Match`: an unchanged read answers 304, and a write with a stale tag answers 412 | [features/invoices.md](features/invoices.md) | [ADR-0025](adr/0025-openapi-truth.md) |
| Read caching with HybridCache, cache-aside, evicted by each write that succeeds | [features/invoices.md](features/invoices.md) | [ADR-0026](adr/0026-read-caching.md) |
| `Api.Contracts`, the API's public surface, which a project may publish as a client package | [architecture.md](architecture.md#shipping-apicontracts-as-a-client-package) | [ADR-0013](adr/0013-api-contracts-public-surface.md) |

## Persistence and events

| Capability | Explained in | Decision |
|---|---|---|
| EF Core on PostgreSQL, in a Persistence project that is a peer of Infrastructure: the two never reference each other | [architecture.md](architecture.md#the-rings) | [ADR-0002](adr/0002-infrastructure-persistence-split.md), [ADR-0006](adr/0006-ef-complex-types-and-interceptors.md) |
| A one-shot migration job that applies the migrations, seeds and exits, which the API waits for | [deployment.md](deployment.md#migrations-a-one-shot-pre-deploy-job) | none |
| The transactional outbox: a domain event is written in the same transaction as the aggregate, then dispatched after the commit, at least once, with leases and a checkpoint per handler | [architecture.md](architecture.md#domain-events-and-integration-events), [deployment.md](deployment.md#outbox-operations) | [ADR-0018](adr/0018-transactional-outbox-and-idempotency.md), [ADR-0023](adr/0023-outbox-lease-and-checkpoints.md) |
| Data retention for the bookkeeping tables: a janitor with validated windows | [deployment.md](deployment.md#required-configuration) | [ADR-0027](adr/0027-data-retention.md) |

## Security

| Capability | Explained in | Decision |
|---|---|---|
| ASP.NET Core Identity with JWT access tokens and single-use, rotating refresh tokens | [features/auth.md](features/auth.md) | [ADR-0007](adr/0007-identity-in-persistence-custom-jwt-endpoints.md) |
| Deny by default: every endpoint of the versioned group needs a signed-in caller unless it is on the anonymous allowlist, and an endpoint names the one permission it needs | [features/auth.md](features/auth.md#protecting-an-endpoint) | [ADR-0021](adr/0021-api-hardening-defaults.md) |
| Hardening defaults: rate limits, forwarded-header trust, security headers, request limits and timeouts | [threat-model.md](threat-model.md) | [ADR-0021](adr/0021-api-hardening-defaults.md) |
| The threat model: what is defended today, the accepted risks and the gaps to decide before production | [threat-model.md](threat-model.md) | none |

## Operations

| Capability | Explained in | Decision |
|---|---|---|
| The Aspire AppHost: PostgreSQL, the migration worker and the API under the Aspire dashboard | [development.md](development.md#running-the-app) | [ADR-0008](adr/0008-containerized-sdk-and-aspire-orchestration.md) |
| A deployment to one host: the images, `aspire publish`, the `.env` and `docker compose up` | [deployment.md](deployment.md#deploy-to-one-host) | none |
| Container images published by the SDK, without Dockerfiles | [deployment.md](deployment.md#container-images--no-dockerfiles-by-design) | none |
| Health probes: `/health` for readiness and `/alive` for liveness | [deployment.md](deployment.md#health-probes) | none |
| Telemetry over OTLP, with a reference of every name that a test holds to the code | [deployment.md](deployment.md#observability), [deployment.md](deployment.md#telemetry-reference) | [ADR-0028](adr/0028-use-case-telemetry.md) |
| E-mail by configuration: logged and not sent until an SMTP relay is configured, and caught by Mailpit under `aspire run` | [development.md](development.md#secrets) | none |
| The reference outbound integration: a typed client behind a port, with resilience per client and options validated at boot | [features/exchange-rates.md](features/exchange-rates.md) | [ADR-0020](adr/0020-outbound-http-integrations.md) |

## Quality gates

| Capability | Explained in | Decision |
|---|---|---|
| The test projects: what each proves, what it needs and when a test of yours belongs in it | [development.md](development.md#tests) | [ADR-0009](adr/0009-test-stack.md) |
| The gates beyond the tests: the OpenAPI snapshot, docs drift, the link check, ADR history and a coverage floor per ring | [development.md](development.md#gates-beyond-the-tests) | none |
| License ceilings for packages and for container images, held by tests | [development.md](development.md#tests) | [ADR-0011](adr/0011-license-ceiling-policy.md) |
| Central package versions, NuGetAudit over the whole graph, and Dependabot | [development.md](development.md#vulnerable-packages-nugetaudit), [development.md](development.md#bumping-the-sdk) | [ADR-0029](adr/0029-central-package-management.md) |

## Working with an agent

| Capability | Explained in | Decision |
|---|---|---|
| `AGENTS.md`, path-scoped rules, skills, read-only reviewers, hooks and MCP servers, each listed with when it applies | [INDEX.md](INDEX.md) | [ADR-0014](adr/0014-ai-documentation-architecture.md) |
| The hooks: what each decides, how to add one and how to turn them off | [development.md](development.md#agent-hooks) | [ADR-0014](adr/0014-ai-documentation-architecture.md) |

## With an option

An option chosen when the solution was created adds a capability. Each one this solution has is
listed here with its doc, and the marker at the repository root, .trellis\-template.json, records the
options.

<!--#if (IncludeUI) -->
- `--include-ui`: the Blazor UI and its design system ([design-system.md](design-system.md);
  [ADR-0015](adr/0015-blazor-everywhere-shared-ui.md), [ADR-0032](adr/0032-ui-design-system.md)).
<!--#endif -->
<!--#if (UseRedis) -->
- `--use-redis`: Valkey as the shared level of the read cache
  ([ADR-0026](adr/0026-read-caching.md)).
<!--#endif -->
<!--#if (UseSerilog) -->
- `--use-serilog`: Serilog's console sink beside OpenTelemetry
  ([deployment.md](deployment.md#observability)).
<!--#endif -->
<!--#if (UseMultitenancy) -->
- `--use-multitenancy`: a tenant on the principal and a `"Tenant"` query filter on every aggregate
  root ([development.md](development.md#multi-tenancy-optional);
  [ADR-0019](adr/0019-multitenancy-toggle.md), [ADR-0022](adr/0022-tenant-on-the-principal.md)).
<!--#endif -->
<!--#if (IncludeUiTests && IncludeUI) -->
- `--include-ui-tests`: Playwright browser tests, opt-in at run time
  ([development.md](development.md#ui-tests-optional)).
<!--#endif -->

To change an option later, generate the solution again with the changed option and merge the
difference, as steps 3 to 5 of [upgrading.md](upgrading.md) describe.

## Make it yours

- Add a feature of your own: the [`add-feature`](../.claude/skills/add-feature/SKILL.md) skill copies
  the reference slice ([features/invoices.md](features/invoices.md)) layer by layer.
- Remove the sample once your own feature exists: the
  [`remove-feature`](../.claude/skills/remove-feature/SKILL.md) skill.
- Call a third-party API: the [`add-integration`](../.claude/skills/add-integration/SKILL.md) skill,
  which copies the reference integration.
- Record a decision of your own: the [`new-adr`](../.claude/skills/new-adr/SKILL.md) skill numbers
  it from 0100.
- Adopt a later template version: [upgrading.md](upgrading.md), with the
  [`upgrade-template`](../.claude/skills/upgrade-template/SKILL.md) skill.
