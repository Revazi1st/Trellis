---
description: The documentation site (site/): the Lattice visual language, the rules the owner set for its two themes, and how a site change is proven before it merges
paths: ["site/**"]
---

# Site rules

The site is maintainer-only (ADR-0031): Astro + Starlight rendering `docs/` in place, built on the Node
version in `site/.nvmrc`, deployed to GitHub Pages from a release tag. `site/README.md` says what the
build does to a doc; this file says how the site may change.

## The language is Lattice, and it is written down

- **The design system** lives at <https://claude.ai/artifact/T4GLnKLBUJ5Z47H8Aq4oFw>: the tokens of both
  themes with their usage and contrast, the brand book, and a live preview with a guideline for every
  component of the landing page, the docs pages and the chrome. Read its README before changing a token,
  a component or a page layout; build a new element from its tokens and components, never from a new
  colour or a new face.
- **One source of values.** The tokens are the custom properties of `site/src/styles/lattice.css` (the
  design system names them without the dashes: `--lt-ink` is `lt-ink`); the picture palette is
  `eng/readme/render-diagrams.py`; the diagram palette is `site/src/components/MarkdownContent.astro`.
  A change to one of them is a change to the design system: say so in the pull request, so the
  maintainer republishes it from the same values.

## The owner's rules for the two themes

- **Dark is final.** The dark theme and the dark landing stage are the look the owner chose. A change
  the owner did not ask for is reverted, not argued. Light-only changes go under
  `:root[data-theme='light']`.
- **Light keeps the dark construction; only colour values change.** The same elements, translucency,
  hairlines, blur, radii, spacing and type in both themes; a surface that is a tint over the stage in
  dark is a tint over the stage in light (the terminal, the glass).
- **A dark-ground device has the hairline as its light counterpart.** A one-pixel lighter top edge
  lifts a pane off a dark stage; on a light stage it reads as a white gap, so there the edge is the
  hairline.
- **Every text passes WCAG AA on its real composited ground**, in both themes and at every width
  (4.5:1; 3:1 for the headline and for focus rings). The lime `--lt-leaf` is a dark-stage accent: as
  text on a light ground it fails, so the light stage uses `--lt-brand`.
- **Code before pictures.** Measure in the browser (computed styles, bounding boxes, pixel samples)
  before deciding; a screenshot is the proof, not the design.

## How a site change is proven

- `cd site && npm ci && TRELLIS_SITE_REF=v<released> npm run build` passes: it validates every link and
  anchor. A doc change keeps every heading id (the site keeps a renamed heading's anchor; a removed one
  breaks links).
- Screenshots of the touched pages at 400, 1280 and 2000 px in both themes, before and after, in the
  pull request. A change meant for the light theme proves the dark pages unchanged with a pixel diff
  of zero; a structural fix that applies to both themes says so and shows both.
- The live site documents the release tag. A fix to the site's own code reaches it without a release:
  `gh workflow run site.yml --ref main -f ref=v<released> -f site_ref=<branch or main>`. A change to a
  doc's words reaches it with the next release only.
