# Security Policy

## Reporting a vulnerability

Please **do not** open a public issue for security vulnerabilities. Instead, use
[GitHub's private vulnerability reporting](https://github.com/Revazi1st/trellis/security/advisories/new)
(Security → Report a vulnerability), or email the maintainers.

We aim to acknowledge reports within a few business days and to ship a fix or
mitigation promptly, crediting reporters who wish to be named.

## Scope & hardening notes

Trellis ships auth and infrastructure defaults intended as a **starting point**;
review these before production:

- **JWT signing key** — `Authentication:SigningKey` must be a strong secret supplied
  per environment (never the sample dev value). Rotate regularly.
- **Seeded admin** — the `Seed:Admin*` credentials are dev-only; override or disable
  seeding in production.
- **Blazor Server auth** is demo-grade (per-circuit token); use a BFF / cookie flow
  for production (see ADR-0007).
- **Health endpoints** return only overall status; if you add detailed checks, secure
  the endpoints.
- **Dependency hygiene** — NuGet audit runs on the full graph; the license-ceiling
  policy (ADR-0011) blocks known-encumbered packages.
