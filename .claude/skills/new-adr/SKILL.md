---
name: new-adr
description: Record a new Architecture Decision Record. Use when making a load-bearing architectural or convention decision.
---

# New ADR

ADRs are short, dated, immutable records of load-bearing decisions (ADR-0001). One decision per file.

1. **Find the next number.** The numbers are split so a template upgrade never collides with your
   own records:
   - **0001–0099 belong to the template.** They arrive with the generated project and with later
     template versions (`docs/upgrading.md`); never number one of yours in this range.
   - **0100 and up belong to the project.** The first ADR you record is `0100`, the next the highest
     number at or above 0100, plus one.
   - Only in the template's own repository (the one with `.template.config/`) does a new record take
     the next number below 0100.

   `scripts/check-adr-links.sh` fails when two files share a number, when a record sits in the
   wrong `docs/INDEX.md` section for its range, and, in the template's repository, on a number of
   0100 or more.
2. Create `docs/adr/NNNN-kebab-title.md` with:

```markdown
# ADR-NNNN: <title>

- **Status**: accepted
- **Date**: <YYYY-MM-DD>
- **Prior art**: <optional: the project, author and license this decision takes an idea from, and what>

## Context
<the forces: what problem, what constraints>

## Decision
<what we chose, concretely>

## Consequences
<what this makes easy/hard; follow-ups; who must know>

## Alternatives / Revisit when
<optional: each alternative you rejected and why, one line each; the concrete, checkable condition that would reopen the decision>
```

   Keep the **Prior art** line only when the decision adopts an idea, pattern or text from someone
   else's project, and delete it otherwise; a verbatim copy also needs the notice its license
   requires. In the template's own repository the same line goes into the README's Credits.

   Keep **Alternatives / Revisit when** short, three to five lines, or delete it. Name what was rejected
   and the reason, so the next reader does not reopen it from scratch. Write a revisit trigger as a fact
   someone can check ("the package is relicensed under MIT", "a second process has to dispatch the same
   events"), never "if this stops fitting": a vague trigger reads as permission to undo the decision.
3. Add a one-line pointer to `docs/INDEX.md`: under **Project decisions** for 0100 and up, under
   **Template decisions** for the template's own.
4. If the decision creates a rule agents must follow, restate the rule (not the rationale) in the
   relevant `.claude/rules/*.md` and link the ADR.

Supersede rather than edit: a reversal is a new ADR that references the old one, and the old one's
status line says what superseded it. A correction that leaves the decision standing is a dated
`## Amendment (YYYY-MM-DD)` section at the end, with a `- **Amended**:` line under the date.
