---
name: new-adr
description: Record a new Architecture Decision Record. Use when making a load-bearing architectural or convention decision.
---

# New ADR

ADRs are short, dated, immutable records of load-bearing decisions (ADR-0001). One decision per file.

1. Find the next number: `ls docs/adr` (highest `NNNN` + 1).
2. Create `docs/adr/NNNN-kebab-title.md` with:

```markdown
# ADR-NNNN: <title>

- **Status**: accepted
- **Date**: <YYYY-MM-DD>

## Context
<the forces: what problem, what constraints>

## Decision
<what we chose, concretely>

## Consequences
<what this makes easy/hard; follow-ups; who must know>
```

3. Add a one-line pointer to `docs/INDEX.md`.
4. If the decision creates a rule agents must follow, restate the rule (not the rationale) in the relevant `.claude/rules/*.md` and link the ADR.

Supersede rather than edit: a reversal is a new ADR that references the old one.
