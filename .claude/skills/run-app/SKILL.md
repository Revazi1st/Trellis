---
name: run-app
description: Run the Trellis app to see it working. Use when asked to start, run, or manually verify the app end-to-end.
---

# Run the app

**Full stack (`aspire run`) needs a local .NET 10 SDK** — it cannot run in the SDK container (ADR-0008). With a local SDK:

```bash
aspire run          # postgres + migrations + api + web, with the Aspire dashboard
```

Then open the dashboard URL it prints; the API exposes Scalar at `/scalar/v1`, the web app serves the UI. Seeded admin: `admin@example.com` / `Admin123!`.

## Containerized smoke (no local SDK)
Aspire can't orchestrate from the container, but you can run the pieces as sibling containers on one network to verify a change end-to-end:
1. `postgres:18-alpine` (POSTGRES_PASSWORD=postgres, POSTGRES_DB=trellis).
2. Migration worker: `./scripts/dotnet.sh run --project tools/Trellis.MigrationService -- --ConnectionStrings:trellis-db="Host=<pg>;Database=trellis;Username=postgres;Password=postgres"` (applies migrations + seeds).
3. API: same connection-string arg; hit `/api/v1/...` and `/scalar/v1`.
4. Web: pass `services__api__http__0=http://<api-host>:8080` so service discovery resolves the API.

Verify: register/login, permission-scoped 200/403, the invoice lifecycle, and (for UI) the login page renders and anonymous routes redirect.
