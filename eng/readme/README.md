# README pictures

The pictures on the repository's front page are drawn from the repository itself.
[`render-diagrams.py`](render-diagrams.py) reads the solution tree, the project references, the test
projects and a handful of names from the code, and writes each picture twice, as `<name>-light.svg` and
`<name>-dark.svg`, for GitHub's two themes. No picture is drawn by hand, so none is edited by hand:
change the code or the script, then render again.

| Picture | Shows | Read from |
|---|---|---|
| `title` | the banner, 1200 × 320: the Trellis mark, the wordmark and the tagline on a dark stage, beside a terminal with the two install commands; the tagline is the emphasised span of [`pitch.md`](pitch.md) | `.template.config/icon.svg`; [`pitch.md`](pitch.md); the package id in `eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj` and the short name in `template.json`, both checked against the install block in the root `README.md` |
| `rings` | the rings as a 160 px mark: Domain and SharedKernel at the centre, Application, Infrastructure ∥ Persistence, Presentation | the palette |
| `architecture` | a bento of ring cards, every project a card holding its real folders and files, the references between them pointing inward, the ring legend below | `src/`, `tools/`, `.template.config/template.json`, each `.csproj`, [AGENTS.md](../../AGENTS.md)'s dependency flow |
| `testing` | each test suite as a card as wide as the layers it runs through, under a rail of the layers; its needs as a pill, opt-in suites dashed, ArchitectureTests as a bracket over the rail | `tests/`, with labels condensed from the Tests table in [docs/development.md](../../docs/development.md#tests) |
| `pipeline` | one request, paying an invoice, as a rail of numbered steps from the HTTP call to the receipt e-mail, each step's real type name under it, the outbox boundary dashed | the endpoint, the `TryDecorate` calls, the domain event, the outbox, the event handlers, the e-mail port |

## Render them again

From the repository root, with Python 3.9 or later and nothing else installed:

```bash
python3 eng/readme/render-diagrams.py           # rewrites the SVGs that changed, names each one
python3 eng/readme/render-diagrams.py --check   # writes nothing; exit 1 when a committed SVG is stale
```

Render after a change that the pictures show, and commit the SVGs in the same change: a project or a test
project added, renamed or moved; a folder or a top-level file added to or removed from a project under
`src/` or `tools/` (a collapsed folder shows its name only, so a file inside one changes nothing); the
decorator order; an event handler of `InvoicePaidDomainEvent`; a template option that adds or removes a
file; the package id, the template's short name or the install block in the root `README.md`; the pitch
in [`pitch.md`](pitch.md). The output is deterministic, so a render that changes nothing writes nothing.

`--check` exits **0** when every SVG matches a fresh render, **1** when one is missing, differs (it prints
the first lines of the difference) or is a left-over `-light.svg`/`-dark.svg` the script no longer draws,
and **2** when it cannot render at all. The "README pictures are current" step in `.github/workflows/ci.yml`
runs it on every pull request (template source only), so a picture can no longer drift from the code.

## Why exit 2: what the script refuses to draw

A picture that is wrong is worse than none, so the script stops, naming the file and what it looked for,
instead of guessing:

- **A project or a test project it has not placed.** The architecture cards are laid out by
  `ARCH_PROJECTS`, the testing cards by `SUITES` on the layers of `STACK`; a new project stops the render
  until it has a place.
- **An arrow the code does not back.** Every arrow in `ARCH_ARROWS` must be a `ProjectReference`, and
  every edge of AGENTS.md's dependency-flow line must be drawn.
- **A name that moved.** The endpoint, its permission and its answer, the decorators, the handler, the
  aggregate method, the event and its outbox name, the outbox table and processor, the event handlers, the
  one handler that takes the e-mail port, the port, its SMTP adapter and the default provider (Logging,
  which the receipt step's words name), the opt-in switches
  (`RUN_UI_TESTS`, `RUN_ASPIRE_E2E`), Testcontainers and Playwright are each found in the code before
  they are drawn; the banner's two commands must be lines of the root `README.md`.
- **Text that would not read.** Each text is sized from Helvetica's advance widths plus 8 % (monospace at
  0.62 em), and the render fails when a text would leave its canvas, come closer to the edge than the
  picture's 32 px padding (every picture but the rings mark, a 160 px icon), leave the card or pill it
  is drawn in, be smaller than 12 px, overlap another, or sit on a colour it does not contrast with by at
  least 4.5:1 (WCAG 2). GitHub's README column is 896 px wide, so it shows a 1200 px picture at three
  quarters of its size and 12 px reads as 9; the rings mark alone, shown at its own 160 px, keeps its
  9 px arc labels. On the banner the colour under a text is worked out at the text's own place: the
  stage's gradient, the glow over it and a crossing of the lattice.
- **An edge that would not show.** The banner's edge must stand out from the page GitHub shows it on
  (white for the light file, `#0d1117` for the dark one) by at least 1.2:1, about GitHub's own muted
  border on its dark page, and a rim drawn round the stage must stand out from the stage as much; both
  are measured all along the edge.
- **A layout that no longer fits.** A row of cards wider than the picture, a card, chip or panel off the
  8 px grid (a node by its centre), an arrow shorter than 24 px (it would read as a tick on a card's
  edge) or too short to show its head, a step's words reaching the connector that wraps to the next row,
  a legend with no room: each stops the render with the picture and the part named.

## How the trees are cut

The architecture trees are the files a default `dotnet new trellis` generates: what is on disk, minus
`template.json`'s exclude list and every modifier whose condition holds with the options' default values
(so the multi-tenancy files are absent, the UI projects present and tagged `--include-ui`). Then one rule
for every project: its folders, then its top-level files except the `.csproj`; each folder collapsed to
its name; the Invoices slice expanded two levels as the worked
example, with the folders on the way to it; a tall tree split into columns, preferably where a folder
starts, and a column that continues a folder opens with its path.

## The visual language

The pictures share one look with the website (`site/`, whose `lattice.css` maps these tokens onto
Starlight) and future screenshots: calm and precise, hairlines instead of outlines, a dark stage for the
banner, monospace for real names, the lattice as texture and the leaf as the one accent. Nothing is copied from the templates the README credits: the ideas are credited,
the art is ours.

**Tokens.** The palette at the top of the script holds them, and every picture takes its colours from
there:

| Token | Light | Dark |
|---|---|---|
| background | `#F6F9F4` | `#0C1410` |
| surface | `#FFFFFF` | `#121D16` |
| surface-2 (the Invoices slice, pills) | `#EEF6E8` | `#172520` |
| line (hairlines; the lattice at 4 %) | `#1F5C3D` at 14 % | `#8FD16A` at 16 % |
| ink | `#14231B` | `#E6EFE8` |
| muted | `#566B5F` | `#9DB3A4` |
| accent (connectors, the slice's bar) | `#1F5C3D` | `#A9DD8A` |
| leaf | `#8FD16A` | `#8FD16A` |

The banner's stage is the same in both themes: a gradient from `#0B1510` to `#10261B`, the lattice in
white at 4 %, one radial glow of the leaf at 25 % that fades to nothing at 60 % of its radius, text in
`#F2F7F0` and `#A9BBAE`.

**Ring hues**, the same in every picture (fill and ink, light then dark); each fill is edged with a 1 px
hairline of its own ink at 35 %:

| Ring | Light fill / ink | Dark fill / ink |
|---|---|---|
| Domain and SharedKernel (gold) | `#F3E6C9` / `#4A3511` | `#3A2F1B` / `#F5E5C3` |
| Application (ochre) | `#F8EFD9` / `#5A431A` | `#2E2616` / `#F1E3C4` |
| Infrastructure (moss) | `#DFE9D6` / `#2B4022` | `#27331F` / `#DEECD5` |
| Persistence (teal) | `#D3E9E6` / `#163E39` | `#1B3431` / `#D2EDE9` |
| Presentation (slate) | `#D9E6F3` / `#1C3654` | `#1E2F45` / `#D8E6F5` |
| Shared and tools (neutral) | `#E9ECEA` / `#2A332D` | `#1C2320` / `#D7DED9` |

What lies outside the solution (a client, a browser, the database) sits on the plain surface. A
project's card is white on its ring in light mode and a deeper shade of its ring in dark mode: the two
themes are designed separately, never inverted. Every neutral carries the palette's green bias; there is
no pure grey.

**Type.** GitHub blocks web fonts inside images, so the SVGs use the system stacks
`ui-sans-serif, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif` (400, 600, 700) and
`ui-monospace, SFMono-Regular, Menlo, Consolas, monospace`, never below 12 px, which GitHub's README
column shows at 9. Real names (projects, types, files, switches) are set in monospace.

**Shape.** Radius 16 on cards, 10 on chips, panels and the terminal, fully rounded pills; borders are
1 px hairlines, never 2 px outlines. No shadows: on the dark stage the terminal gets a 1 px lighter top
edge instead, and so does the stage itself in the dark variant, all round. Gradients sit on surfaces
only, never on text.

**Motifs.** The lattice, a 45° grid of 1 px lines 48 px apart at 4 %, lies on the stage and on the
background of the architecture, testing and pipeline pictures, where it shows between the cards; the
rings mark, a transparent 160 px icon, has none. The leaf appears once, in the mark. The banner has one
glow, behind the mark.

**Composition.** At least 32 px of padding inside every picture but the 160 px rings mark, which the
render enforces for text, the banner's included; one focal object per picture; the title top left at
600 with a one-line muted subtitle; connectors are 1.25 px hairlines with small filled arrowheads;
cards, chips, panels, the terminal and the nodes' centres on the 8 px grid, which the render checks; at
most two accent hues beyond the ring hues (the terminal's traffic lights are monochrome for that
reason); no emoji. Each picture's own layout:

- `title`: the stage edge to edge, the mark with the wordmark and the tagline on the left, the terminal on
  the right. The two variants differ only at the edge: on white the stage's own edge shows, while
  GitHub's dark page is as dark as the stage's darkest part (1.06:1), so the dark variant gets a 1 px rim
  of white at 12 % (1.49:1 against that page).
- `rings`: the mark's geometry, unchanged since the first README, in the ring hues.
- `architecture`: a bento, 16 px between the cards of a row and 40 px between rows, where the
  references between rings run as arrows, all inward. Presentation spans the top; Infrastructure and
  Persistence sit side by side, the ∥ in the gutter between them, beside the lane where Presentation
  reaches Application directly; Application and Domain · SharedKernel follow, as the rings mark splits
  Core; Shared and tools close it.
- `testing`: the layers from a browser to PostgreSQL as a rail of chips, 16 px apart; each suite a card
  spanning the layers it runs through, reading its name, its needs as a pill (`nothing`, `Docker`,
  `RUN_ASPIRE_E2E=1`, `RUN_UI_TESTS=1`; beside the name, or under it on a narrow card), then what it
  proves, with the layers again as dots on a small rail along its foot; the legend goes where the
  staircase of cards leaves room.
- `pipeline`: three rows of numbered nodes joined by hairline arrows; a fainter hairline, in the rail
  colour of the testing picture, returns from each row's end to the next row's start, so the steps and
  their arrows stay the strongest lines; the last row's dashed line is the outbox boundary, the commit
  before it, the dispatch after it.

## Put a picture on a page

GitHub shows the dark file when the reader's theme is dark:

```html
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="eng/readme/architecture-dark.svg">
  <img alt="The solution as a bento of ring cards, each project a card holding its real folders" src="eng/readme/architecture-light.svg" width="100%">
</picture>
```

The paths are relative to the Markdown file that embeds them; from the repository root they start with
`eng/readme/`. `rings` is 160 px wide on purpose and sits centred at that size; the others are 1200 px wide
and fill the column with `width="100%"`.

## Look before you commit

Open an SVG in a browser, or screenshot both themes with a headless Chromium (Edge or Chrome) at the
picture's own size, and look at the PNG:

```bash
svg=eng/readme/architecture-light.svg
height=$(grep -o 'height="[0-9]*"' "$svg" | head -1 | tr -dc '0-9')
"/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge" --headless=new \
  --user-data-dir="$(mktemp -d)" --no-first-run --no-default-browser-check \
  --screenshot=/tmp/architecture-light.png --window-size=1240,$((height + 40)) \
  "file://$PWD/$svg"
```

The private profile (`--user-data-dir`) matters when Edge is already open: on the default profile the
headless browser waits for the one in use, and a batch of screenshots stalls for minutes. The window is
the SVG's `height` plus a margin; QuickLook (`qlmanage`) makes square thumbnails and crops a tall picture
at the bottom, so it is no help here. Check both themes, on GitHub's page colours if you can (white and
`#0d1117`): the light and dark palettes are designed separately and each needs its own look.

## Changing a picture

- **Colours**: the tokens at the top of `render-diagrams.py`: `RING_HUES` for the rings, `make_theme`'s
  arguments for each theme, `Stage` for the banner. The contrast check tells you when a pair stops
  reading.
- **A ring's one-line description**: `CARDS`, worded after the table of rings in
  [docs/architecture.md](../../docs/architecture.md).
- **What a suite says**: `SUITES`, condensed from the Tests table; the layers it spans are `STACK`'s,
  labelled by `LAYERS`.
- **The art is our own.** The pictures take their idea from the walkthrough-style README of
  [amantinband/clean-architecture](https://github.com/amantinband/clean-architecture), credited in the
  README; nothing of its artwork is reused, and colours, shapes, type and composition stay ours.

## The pitch

[`pitch.md`](pitch.md) holds the one sentence that says what the template is. It is one line with
exactly one `*…*` span, and that span is the short form of the pitch. The file is plain text, with no
backtick, `&`, `<`, `>`, `"`, `\` or `|`, because the sentence is copied into JSON, XML and Markdown.
Every other place that states the pitch copies it from here:

| Place | What it holds | Held by |
|---|---|---|
| the paragraph under the header of the root `README.md` | the pitch | `scripts/check-readme-sync.sh` |
| the alt text of the title picture in the root `README.md` | the short form | `scripts/check-readme-sync.sh` |
| `title-light.svg`, `title-dark.svg` | the short form, as the tagline | `render-diagrams.py --check` |
| the first paragraph of `eng/Trellis.TemplatePack/README.md` | the pitch | `scripts/check-readme-sync.sh` |
| `<Description>` in `eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj` | the pitch without the asterisks | `scripts/check-readme-sync.sh` |
| `description` in `.template.config/template.json`, which `dotnet new trellis --help` and the IDE dialogs show | the pitch without the asterisks | `scripts/check-readme-sync.sh` |
| the site's description, `description: pitch,` in `site/astro.config.mjs`, read from this file by `site/trellis.config.mjs` | the pitch, derived | `scripts/check-readme-sync.sh` |
| the hero title and the head title in the frontmatter of `site/src/content/docs/index.mdx` | each ends with the short form | `scripts/check-readme-sync.sh` |

`.template.config/consumer/README.md`, `AGENTS.md` and `docs/architecture.md` open with a description
of the generated solution, not of the template, and do not copy the pitch. To change the sentence,
edit `pitch.md`, render the pictures again, then run `./scripts/check-readme-sync.sh`: it names each
copy that no longer agrees and says which side to change.

## Screenshots

[`shots/`](shots) holds four pictures of the web UI on a first run. The root `README.md` shows them
under *What it looks like*, and the site's landing page shows them in a block of the same name.

| File | Shows |
|---|---|
| `first-run-invoices-1280-light.png`, `first-run-invoices-1280-dark.png` | the Invoices page on a laptop, 1280 px wide, with only the invoices the migration worker seeds in Development |
| `first-run-invoice-400-light.png`, `first-run-invoice-400-dark.png` | the seeded draft, "Draft & Co", on a phone, 400 px wide |

`render-diagrams.py` does not draw them. CI's Playwright job (*Playwright UI* in
`.github/workflows/ci.yml`) takes them: its step "Keep pictures of the first run" runs one browser
test on the freshly seeded database before any other browser test runs, and the job uploads the four
files with the rest of its screenshots as the `ui-screenshots` artifact. The current set comes from
run 43624238334, CI on `main` at `c9e5350`.

Nothing regenerates them, and nothing compares them: a screenshot is looked at, never diffed
(`tests/Trellis.UiTests/DesignSystemProofTests.cs` says why). Refresh them by hand from the newest
green CI run on `main` when a UI page or `src/Presentation/UI/Trellis.UI.Common/wwwroot/tokens.css`
changed, and before each minor release ([docs/releasing.md](../../docs/releasing.md), *Cutting a
release*):

```bash
run=$(gh run list --workflow ci.yml --branch main --status success --limit 1 --json databaseId --jq '.[0].databaseId')
gh run download "$run" -n ui-screenshots -D "$TMPDIR/ui-screenshots"
for f in first-run-invoices-1280-light first-run-invoices-1280-dark first-run-invoice-400-light first-run-invoice-400-dark; do
  cp "$TMPDIR/ui-screenshots/$f.png" eng/readme/shots/
done
```

No page holds a picture's height: the README sets a width only, and the site reads the width and the
height from the file when it builds, so a refresh is a copy. `scripts/check-readme-sync.sh` fails
when a picture the README names is missing, and when a file in `shots/` is used neither by the README
nor by the site.

## Why `eng/`, not `docs/`

`docs/` ships into every generated project; these files describe the template repository and must not.
`eng/` is not excluded as a whole (`eng/coverage.runsettings` ships on purpose), so this folder is
excluded by name, in both lists that decide what ships: `eng/readme/**` in `sources[0].exclude` of
[`.template.config/template.json`](../../.template.config/template.json) (folder installs) and
`..\..\eng\readme\**` in `TemplateContentExclude` of
[`eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj`](../Trellis.TemplatePack/Trellis.TemplatePack.csproj)
(the package). Template CI's consumer-files step fails if `eng/readme` appears in a generated project.
