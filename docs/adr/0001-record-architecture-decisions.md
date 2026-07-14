# ADR-0001: Record architecture decisions

- **Status**: accepted
- **Date**: 2026-07-14

## Context

XTemplate is both a working solution and a template others will instantiate. Its structural choices — some of which deliberately diverge from mainstream .NET templates — need durable, discoverable rationale. Short, dated, immutable decision records are also excellent context for AI agents, which read them to answer "why is it built this way?" without human steering.

## Decision

Record every load-bearing architectural decision as an ADR in `docs/adr/NNNN-title.md` using this format: Status / Date / Context / Decision / Consequences. ADRs are immutable once accepted; a change of course gets a new ADR that supersedes the old one (noted in both).

## Consequences

- `docs/adr/` is the canonical "why"; `AGENTS.md` and `.claude/rules/` restate only the rules that agents must apply, linking here.
- New significant decisions during any milestone must land with an ADR in the same change.
