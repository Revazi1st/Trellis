# ADR-0032: A design system for the UI, held by tests

- **Status**: accepted
- **Date**: 2026-10-26

## Context

The Blazor UI had one 56-line stylesheet with eight colour tokens on `:root`, a light set and a dark
set under `prefers-color-scheme`, literal sizes and radii, and no written account of what a page may
use. Nothing stopped a page from writing a colour of its own, a new class from arriving without a
rule, or a dark value from drifting out of step with its light twin, and the contrast ratios lived in
a comment that nothing checked. An agent asked to add a page had no list of components to build from
and no test to tell it when it had left the system.

The template's own visual language already had the construction this needed: tokens named by role,
every colour set in both themes, a light theme that keeps the dark one's construction, text checked on
the ground it sits on, and one source of values. The brand devices stay with the template; a generated
project is someone else's product. What carries over is the construction and a starting palette.

## Decision

- **Two files.** `UI.Common/wwwroot/tokens.css` holds every value and is the one re-skin point;
  `app.css` holds the components and writes no colour: every colour is a `var(--…)` or a `color-mix()`
  of two tokens. A component may alias a token for a variant (`--tone` on a callout).
- **Role names.** `--bg --surface --surface-2 --line --ink --muted --accent --on-accent` and the status
  families `--danger --success --warning`, each with its `--on-*`. The 0.1.x names `--fg` and `--border`
  became `--ink` and `--line`. There is no `--link`: links are the accent, which already differs by
  scheme. Type, spacing, radii and the devices (hairline, focus ring, measures) are tokens too.
- **One pair per colour, `light-dark()`.** `:root { color-scheme: light dark }` and every colour written
  once as `light-dark(#light, #dark)` in opaque six-digit hex; `:root[data-theme="light"]` and
  `[data-theme="dark"]` set only `color-scheme`, so an attribute on `<html>` can hold one theme and a
  switch has one thing to write. Tokens are opaque so a ratio can be checked once per ground.
- **The existing classes are the components.** No class was renamed (the browser tests and the markup
  tests pin them); each one has a subsection in `docs/design-system.md` with its markup, states,
  accessibility and behaviour on a narrow screen; the system names one breakpoint, 50 rem, and nothing
  shipped changes shape at it yet. A new visual element is a new subsection.
- **The doc is the source of names, and tests hold it both ways.** `DesignSystemTests`
  (ArchitectureTests, compiled with the UI) proves that only the token file writes a colour, that every
  colour token is one `light-dark()` pair, that text passes WCAG AA on its grounds and every `--on-*`
  on its fill (4.5:1; 3:1 for the accent as a ring or a border), that every class a page uses has a
  rule and every rule has its subsection, and that the doc's token tables and `tokens.css` name the
  same set. Each fact has a positive control. The WCAG arithmetic is the one the repository's picture
  renderer uses.
- **The starting palette** is the template's own: a green ink and accent on near-white in light, a
  lime accent on near-black in dark, with `--on-accent` white in light and dark green in dark, because
  the lime fails as text on a light ground. The status colours were chosen by the contrast test. A
  project changes the values and keeps the rules.

## Consequences

- A project that customised the 0.1.x stylesheet carries two renames (`--fg` to `--ink`, `--border` to
  `--line`) and moves its values into `light-dark()` pairs; `docs/upgrading.md` and the changelog say
  so.
- The contrast fact refuses values: a palette change that drops a pair below its floor fails the build
  naming the pair, the scheme and the ratio. That is the point, and it is also the cost: a re-skin is a
  short loop with the test, not a paste.
- `light-dark()` is Baseline 2024 (Chrome 123, Firefox 120, Safari 17.5). Blazor Server already asks
  for an evergreen browser; an older one sees the light values.
- Every UI-only file outside `src/Presentation/UI/` needs its own exclude in the template (the design
  doc, this record, the test file), because the engine does not process conditionals in `.css`.
- The visible change for a generated project is the palette and the type scale; the markup did not
  change.

## Alternatives / Revisit when

- A component library (Fluent UI Blazor, MudBlazor, Radzen) or a CSS framework: a package whose tokens
  would become the system, against ADR-0003's hand-roll rule and the ask-first policy on packages; the
  app is a hundred lines of CSS. Tailwind or any CSS build step: a toolchain for the same hundred lines.
- CSS isolation (`.razor.css`) as the organising principle: scoped files hide the system from the
  test and from the next page; a shared stylesheet stays readable as a whole.
- Three theme blocks (`:root`, `prefers-color-scheme`, `[data-theme]`) instead of `light-dark()`: more
  lines, values written twice; revisit if a browser the project must support lacks `light-dark()`.
- Revisit when a second head (a MAUI app) needs the tokens in C#, when a designer needs a rendered
  gallery page, or when an accessibility scanner (axe-core) is wanted beside the contrast arithmetic.
