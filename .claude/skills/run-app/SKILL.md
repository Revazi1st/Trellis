---
name: run-app
description: Run the Trellis app to see it working. Use when asked to start, run, or manually verify the app end-to-end.
---

# Run the app

**Full stack (`aspire run`) needs a local .NET 10 SDK** — it cannot run in the SDK container (ADR-0008).

Prerequisites (docs/development.md, "Prerequisites"): the .NET 10 SDK, Docker, and the **Aspire CLI 13.5 or later**, a separate install from the SDK — `dotnet tool install -g Aspire.Cli`, check with `aspire --version`. If `aspire` is not on PATH, use the CLI-free command below rather than installing tools on the user's machine unasked. `pwsh` is needed only for the Playwright UI tests. With a local SDK:

```bash
aspire run                                   # postgres + migrations + api + web, with the Aspire dashboard
dotnet run --project tools/Trellis.AppHost   # the same app without the Aspire CLI
```

Then open the dashboard URL it prints; the API exposes Scalar at `/scalar/v1` (its root redirects there in Development), the web app serves the UI. Seeded admin: `admin@example.com` / `Admin123!`.

Once the app is up under `aspire run`, the `aspire` MCP server (`.mcp.json`, started as `aspire agent mcp`) is how you read it: the resources and their state, each resource's console and structured logs, the traces, and a resource's start, stop or restart, instead of scraping the terminal or guessing from the dashboard. It finds the running AppHost under the working directory; an interactive session asks before using it, and without the Aspire CLI on PATH it does not start (the dashboard's URL is then the way in).

## Containerized smoke (no local SDK)
Aspire can't orchestrate from the container, but you can run the pieces as sibling containers on one network to verify a change end-to-end:
1. `postgres:18`, the image the AppHost and `docker-compose.yml` run (POSTGRES_PASSWORD=postgres, POSTGRES_DB=trellis).
2. Migration worker: `./scripts/dotnet.sh run --project tools/Trellis.MigrationService -- --ConnectionStrings:trellis-db="Host=<pg>;Database=trellis;Username=postgres;Password=postgres"` (applies migrations + seeds).
3. API: same connection-string arg; hit `/api/v1/...` and `/scalar/v1`.
4. Web: pass `services__api__http__0=http://<api-host>:8080` so service discovery resolves the API.

Verify: register/login, permission-scoped 200/403, the invoice lifecycle, and (for UI) the login page renders and anonymous routes redirect.
