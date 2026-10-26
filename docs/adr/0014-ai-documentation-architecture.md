# ADR-0014: AI documentation architecture

- **Status**: accepted
- **Date**: 2026-07-15
<!--#if (TemplateSource) -->
- **Amended**: 2026-10-25 — the one doc set is also rendered as the template's website, from `docs/` in place (see *Amendment* below)
<!--#endif -->
- **Amended**: 2026-10-26 — reviewers declare what they own, the index lists the agents, hooks and MCP servers, a rare skill may ship hidden, and the docs-sync ladder gains *ask* rungs (see *Amendment (2026-10-26)* below)

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
<!--#if (TemplateSource) -->

## Amendment (2026-10-25): the website renders the same doc set

The template's repository publishes `docs/` as its documentation website (ADR-0031). Astro and
Starlight render the files where they are, with no copy and nothing added to them, so the rule above
still holds: one doc set, which people read on GitHub and on the site and agents read in the repository.
A doc stays plain GitHub Markdown; the site's build adapts it (titles, links, the conditionals) and fails
on a link or an anchor that points nowhere. A generated project gets neither the site nor this amendment.
<!--#endif -->

## Amendment (2026-10-26): reviewers own their ground, and the index lists the whole toolkit

- **Every reviewer in `.claude/agents/` opens with three lines**: *Owns*, *Out of scope* (naming the
  reviewer that owns each item) and *Overlap*. No two reviewers own the same item, and a point another
  reviewer owns gets one line naming that reviewer, never a second finding, so a change read by several
  gets each point once. `AGENTS.md` says which reviewer a change needs: `code-reviewer`,
  `architecture-guard`, `test-reviewer`, `security-reviewer` and, with the UI, `ui-reviewer`.
- **`docs/INDEX.md` lists the agents, the hooks and the MCP servers** beside the rules and the skills,
  each with when it applies, and `scripts/check-adr-links.sh` fails when a reviewer, a hook or a feature
  doc is missing from it, or when `.claude/settings.json` runs a hook that does not exist.
- **A rare procedure may ship as a skill hidden from the model** (`disable-model-invocation: true` in its
  frontmatter): its description stays out of every session's context until someone invokes it by name.
- **The docs-sync ladder gains *ask* rungs**: above the soft `PostToolUse` reminder, a `PreToolUse` hook
  may ask the person before an edit the conventions reserve to a generator or to their judgement. A hook
  ships with a self-test, and none is the only guard: each stays backed by an `AGENTS.md` instruction
  and, where one exists, a CI check.
