# README pictures

The diagrams on the repository's front page are drawn from the repository itself.
[`render-diagrams.py`](render-diagrams.py) reads the solution tree, the project references, the test
projects and a handful of names from the code, and writes each picture twice, as `<name>-light.svg` and
`<name>-dark.svg`, for GitHub's two themes. No picture is drawn by hand, so none is edited by hand:
change the code or the script, then render again.

| Picture | Shows | Read from |
|---|---|---|
| `title` | the banner: the Trellis mark beside the wordmark and the tagline | `.template.config/icon.svg` |
| `rings` | the rings as a 160 px mark: Domain and SharedKernel at the centre, Application, Infrastructure ∥ Persistence, Presentation | the palette |
| `architecture` | every project as a card in its ring, with its real folders and files, and the references between them | `src/`, `tools/`, `.template.config/template.json`, each `.csproj`, [AGENTS.md](../../AGENTS.md)'s dependency flow |
| `testing` | each test suite as a bracket around the layers it runs, opt-in suites dashed | `tests/`, with labels condensed from the Tests table in [docs/development.md](../../docs/development.md#tests) |
| `pipeline` | one request, paying an invoice, from the HTTP call to the receipt e-mail | the endpoint, the `TryDecorate` calls, the domain event, the outbox, the event handlers |

## Render them again

From the repository root, with Python 3.9 or later and nothing else installed:

```bash
python3 eng/readme/render-diagrams.py           # rewrites the SVGs that changed, names each one
python3 eng/readme/render-diagrams.py --check   # writes nothing; exit 1 when a committed SVG is stale
```

Render after a change that the pictures show, and commit the SVGs in the same change: a project or a test
project added, renamed or moved; a folder or a top-level file added to or removed from a project under
`src/` or `tools/` (a collapsed folder shows its name only, so a file inside one changes nothing); the
decorator order; an event handler of
`InvoicePaidDomainEvent`; a template option that adds or removes a file. The output is deterministic, so a
render that changes nothing writes nothing.

`--check` exits **0** when every SVG matches a fresh render, **1** when one is missing, differs (it prints
the first lines of the difference) or is a left-over `-light.svg`/`-dark.svg` the script no longer draws,
and **2** when it cannot render at all. The "README pictures are current" step in `.github/workflows/ci.yml`
runs it on every pull request (template source only), so a picture can no longer drift from the code.

## Why exit 2: what the script refuses to draw

A picture that is wrong is worse than none, so the script stops, naming the file and what it looked for,
instead of guessing:

- **A project or a test project it has not placed.** The architecture cards are laid out by
  `ARCH_PROJECTS`, the testing brackets by `SUITES`; a new project stops the render until it has a place.
- **An arrow the code does not back.** Every arrow in `ARCH_ARROWS` must be a `ProjectReference`, and
  every edge of AGENTS.md's dependency-flow line must be drawn.
- **A name that moved.** The endpoint, its permission, the decorators, the handler, the aggregate method,
  the event and its outbox name, the outbox table and processor, the event handlers, the opt-in switches
  (`RUN_UI_TESTS`, `RUN_ASPIRE_E2E`), Testcontainers and Playwright are each found in the code before
  they are drawn.
- **Text that would not read.** Each text is sized from Helvetica's advance widths plus 8 % (monospace at
  0.62 em), and the render fails when a text would leave its canvas, overlap another, or sit on a colour
  it does not contrast with by at least 4.5:1 (WCAG 2).

## How the trees are cut

The architecture trees are the files a default `dotnet new trellis` generates: what is on disk, minus
`template.json`'s exclude list and every modifier whose condition holds with the options' default values
(so the multi-tenancy files are absent, the UI projects present and tagged `--include-ui`). Then one rule
for every project: its folders, then its top-level files except the `.csproj`; each folder collapsed to
its name; the Invoices slice expanded two levels as the worked
example, with the folders on the way to it; a tall tree split into columns, preferably where a folder
starts, and a column that continues a folder opens with its path.

## Put a picture on a page

GitHub shows the dark file when the reader's theme is dark:

```html
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="eng/readme/architecture-dark.svg">
  <img alt="Trellis's rings as cards holding each project's folders and files, with every reference pointing inward" src="eng/readme/architecture-light.svg">
</picture>
```

The paths are relative to the Markdown file that embeds them; from the repository root they start with
`eng/readme/`. `rings` is 160 px wide on purpose; the others are 1200 px wide and GitHub scales them to
the page.

## Look before you commit

Open an SVG in a browser, or render it on macOS with
`qlmanage -t -s 2400 eng/readme/architecture-light.svg -o /tmp` and open the PNG it writes. QuickLook
thumbnails are square, so a tall picture is cropped at the bottom; a browser shows all of it. Check both
themes: the palette at the top of the script defines one hue per ring for each.

## Changing a picture

- **Colours**: the palette at the top of `render-diagrams.py`, one `Hue` per ring and theme; the
  contrast check tells you when a pair stops reading.
- **A ring's one-line description**: `CARDS`, worded after the table of rings in
  [docs/architecture.md](../../docs/architecture.md).
- **What a suite says**: `SUITES`, condensed from the Tests table; its brackets follow `STACK`.
- **The art is our own.** The pictures take their idea from the walkthrough-style README of
  [amantinband/clean-architecture](https://github.com/amantinband/clean-architecture), credited in the
  README; nothing of its artwork is reused, and colours, shapes, type and composition stay ours.

## Why `eng/`, not `docs/`

`docs/` ships into every generated project; these files describe the template repository and must not.
`eng/` is not excluded as a whole (`eng/coverage.runsettings` ships on purpose), so this folder is
excluded by name, in both lists that decide what ships: `eng/readme/**` in `sources[0].exclude` of
[`.template.config/template.json`](../../.template.config/template.json) (folder installs) and
`..\..\eng\readme\**` in `TemplateContentExclude` of
[`eng/Trellis.TemplatePack/Trellis.TemplatePack.csproj`](../Trellis.TemplatePack/Trellis.TemplatePack.csproj)
(the package). Template CI's consumer-files step fails if `eng/readme` appears in a generated project.
