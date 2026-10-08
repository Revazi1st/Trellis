# ADR-0014: AI documentation architecture

- **Status**: accepted
- **Date**: 2026-07-15

## Context

The repo must be first-class for AI coding agents (Claude Code especially) without maintaining a parallel, drift-prone copy of the docs. Claude Code does not read `AGENTS.md` natively; other tools (Copilot, Cursor, etc.) do.

## Decision

- **`AGENTS.md` is the canonical, tool-neutral instruction file** — architecture, exact commands, dependency-flow rules, three-tier boundaries (always / ask-first / never). Kept lean (~1 screen).
- **`CLAUDE.md` is a thin wrapper** whose first line is `@AGENTS.md` (imports it at session start) plus a short Claude-specific note. `.github/copilot-instructions.md` points at `AGENTS.md`.
- **Layer conventions live in `.claude/rules/*.md` with `paths:` frontmatter** so they load only when an agent touches that layer — no permanent context cost.
- **Skills** (`.claude/skills/`) encode procedures (add-feature, remove-feature, add-integration, add-migration, new-adr, run-app; add-toggle stays in the template's repository); **subagents** (`.claude/agents/`) provide focused reviewers.
- **One doc set, not two**: humans and agents read the same `docs/`. The "AI layer" is indexes + scoped rules, never parallel prose. `docs/INDEX.md` is the agent entry point; `docs/adr/` supplies the "why".
- **Docs-sync is layered by strength**: a soft `PostToolUse` hook reminder → an `AGENTS.md` instruction → a **hard CI check** (`scripts/check-docs-drift.sh` maps every changed path under a feature's folders to that feature's `docs/features/<feature>.md` and fails a PR that changes one without the other; auth, identity and hardening code likewise need `docs/threat-model.md`). Only the CI check guarantees sync.
- **`.mcp.json`** ships Microsoft Learn, Context7, and Playwright servers (project-scoped; consumers approve on first use).

## Consequences

- Instructions work across Claude Code and other agents from one source.
- Feature docs can't silently drift from feature code (CI gate; override via the `docs-not-needed` label).
- Adding a convention means adding or editing a rule file, not enlarging a monolith.
