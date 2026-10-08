# ADR-0001: Record architecture decisions

- **Status**: accepted
- **Date**: 2026-07-14
- **Amended**: 2026-10-08 — numbering: the template's ADRs are 0001–0099, a generated project's own start at 0100 (see *Amendment* below)

## Context

The solution was built as a working application and, at the same time, as the `dotnet new` template others instantiate. Its structural choices — some of which deliberately diverge from mainstream .NET templates — need durable, discoverable rationale. Short, dated, immutable decision records are also excellent context for AI agents, which read them to answer "why is it built this way?" without human steering.

## Decision

Record every load-bearing architectural decision as an ADR in `docs/adr/NNNN-title.md` using this format: Status / Date / Context / Decision / Consequences. ADRs are immutable once accepted; a change of course gets a new ADR that supersedes the old one (noted in both).

## Consequences

- `docs/adr/` is the canonical "why"; `AGENTS.md` and `.claude/rules/` restate only the rules that agents must apply, linking here.
- A load-bearing decision lands with its ADR in the same change, never afterwards.

## Amendment (2026-10-08): two number ranges

A generated project inherits the template's ADRs and records its own next to them; sequential
numbers would collide the first time a template upgrade brought a new ADR. So the template numbers
its records 0001–0099 and a generated project numbers its own from 0100 (the `new-adr` skill).
`docs/INDEX.md` lists the two ranges under separate headings, and `scripts/check-adr-links.sh` fails
on a shared number, on a record under the wrong heading, and on a number of 0100 or more in the
template's own repository. An upgrade then only ever adds or amends files below 0100
(`docs/upgrading.md`).
