# The documentation site

The Trellis website: [Astro](https://astro.build) with [Starlight](https://starlight.astro.build),
rendering the repository's own `docs/` **in place**. There is no copy of a doc in this folder and no
sync step: the pages are the files under `docs/`, read where they are at build time, each with an edit
link back to the gated file. This folder holds only what the site adds around them: the landing page,
the Install and create page, the sidebar, the look, and the code that adapts `docs/` to Starlight.

The site, its workflow and its decision record are maintainer material: `template.json` and the pack
project exclude this folder, so a generated project never contains it.

## Run it locally

You need Node 22.23.4 or later (`.nvmrc` names the exact build CI uses; the lockfile's packages set the floor, and `npm ci` refuses an older Node). A newer lockfile that raises the floor moves `.nvmrc` and the `engines` field in `package.json` with it.

```bash
cd site
npm ci            # the exact versions in package-lock.json
npm run dev       # http://localhost:4321/Trellis/, reloads as you edit docs/ or site/
npm run build     # what CI runs: every page, the search index, and the link check
npm run preview   # serves the built dist/ at http://localhost:4321/Trellis/
```

`npm run build` fails when a link or an `#anchor` in `docs/` or on a site page points nowhere
(starlight-links-validator): the build is the check, and pull requests that touch the docs run it.

## What the build does to `docs/`

`src/lib/docs-loader.mjs` reads `docs/**/*.md` into Starlight's `docs` collection with Astro's `glob()`
loader, next to the site's own pages in `src/content/docs/`. Nothing is added to the files themselves;
the loader and the Markdown plugins (`src/lib/markdown-plugins.mjs`, run by Astro's Sätteri processor)
derive what frontmatter would have held and adapt the rest:

- **Title and description**: the H1 is the page title (and is dropped from the body), the first
  paragraph the description.
- **Routes**: `trellis.config.mjs` maps each doc to its place in the sidebar (`architecture.md` is
  `understand/architecture`, an ADR is `decisions/NNNN-…`). A new doc without an entry keeps its own
  path, and the build warns; a new ADR joins the Decisions group by itself.
- **Links**: a relative link to another doc becomes that page's route, `#anchor` kept; a link that
  leaves `docs/` (a rule, a skill, a workflow) goes to the file on GitHub at the ref the site is built
  from.
- **Template conditionals**: a conditional block (the template engine's `#if (UseRedis)` … `#endif`,
  written as HTML comments in Markdown) is shown as an aside labelled with the option that keeps it
  ("Only with `--use-redis`"). The plugin reads a directive only when the comment is alone on its line,
  which `scripts/check-template-toggles.sh` enforces for every shipped Markdown file, and the build fails
  on a directive line in `docs/` it did not read as one. A directive between two bullets ends the list
  in Markdown; where the block resolves without an aside (a `TemplateSource` block, or any block in the
  defaults build), the two lists are joined again, as the engine writes them.
- **Testing**: the `## Tests` section of `development.md` is rendered as a page of its own, and links
  to its anchors follow it there.
- **Tables** keep their Markdown; the site picks a layout from the length of their cells
  (`tableLayout` in `src/lib/markdown-plugins.mjs`). A table with prose in several columns (the
  test-project map) reads as one block per row at every width, the first cell the title and every other
  cell under its column's name; any other table of three columns or more with long rows does so on a
  phone only, where its last columns would sit off-screen.
- **Architecture** opens with the rings picture, both themes, read from `eng/readme/rings-*.svg` at build
  time (`docs/architecture.md` cannot embed it: `eng/readme/` never ships).
- **ASCII diagrams**: a fence without a language that draws with box characters (the dependency flow on
  Architecture, the release workflow) is set in a type size that fits its widest line to the column,
  down to 0.65rem; a wider one scrolls sideways at that size. Keep such a diagram within about 97
  columns and it fits a 1280 px screen whole. Geist Mono draws the box characters, and a monospace font
  as wide on each platform draws ◄ ►; a symbol none of them has (∥) comes from a proportional font, off
  the grid, so write it in words.
- **Mermaid**: a `mermaid` fence is drawn as a diagram in the browser, by mermaid from this build: a
  chunk fetched only by a page that has a diagram. A diagram shrinks to fit the column down to 80 % of
  its own size and scrolls sideways in its frame past that, so its labels stay readable; a sequence
  diagram's long messages wrap.

The site makes no request to a third party: mermaid, the Geist and Geist Mono fonts and the search index
are all served from the site itself (the fonts' licence is at `/Trellis/fonts-license.txt`).

## The site's own pages

`src/content/docs/` holds the pages that are not in `docs/`: the landing page, Install and create, and
Options. The Options page is generated from `.template.config/`: one section per boolean option with its
default, its description, the files only it adds and the reminder `dotnet new` prints, plus what to do
after creating a solution with it and the docs that explain it, from `optionNotes` in
`trellis.config.mjs`. A new toggle fails the build until it has an entry there. The sidebar is
`astro.config.mjs`'s; `docs/INDEX.md` is reachable but in neither the sidebar nor the search.

## Settings

Environment variables the build reads (all optional):

| Variable | Default | What it changes |
|---|---|---|
| `TRELLIS_SITE_REF` | `main` | What the site is built from. A release tag (`v1.2.3`) makes a release build: the footer of every page names the version it documents. Any other ref makes a preview: a banner on every page says which ref and commit it shows, and that it is not the released documentation (or, before the first release, that the package is not on nuget.org yet). The GitHub links to files outside `docs/` point at this ref too. |
| `TRELLIS_SITE_CONDITIONALS` | `label` | `defaults` renders the docs the way `dotnet new trellis` with no options writes them: each conditional block evaluated against the defaults, with no asides. |
| `TRELLIS_SITE_LASTUPDATED` | unset | `git` shows "Last updated" on every doc: the date its last change reached the branch being built (`git log --first-parent`, so a merge counts by its own date). It needs the full history (`fetch-depth: 0` in CI). Starlight dates the site's own pages either way. |
| `TRELLIS_REPO_ROOT` | `..` | Where the repository is, for a checkout of the site outside it. |

For example, `TRELLIS_SITE_CONDITIONALS=defaults npm run build` checks that the docs read well without
the asides.

## Deploy and preview

`.github/workflows/site.yml` builds and publishes the site on GitHub Pages, at
<https://revazi1st.github.io/Trellis/>:

- **Pull requests** that touch `docs/`, `site/`, `README.md`, `.template.config/`, `global.json`,
  `eng/readme/` or a source file a site page quotes get a build-and-validate job, never a deploy.
- **A release** deploys the site from its tag: `release.yml` calls `site.yml` once the package is
  published (never on a dry run), with `TRELLIS_SITE_REF` set to the tag.
- **A preview** deploys any ref on demand: `gh workflow run site.yml -f ref=main`. It replaces the
  released site until the next deploy and says so in a banner on every page;
  `gh workflow run site.yml -f ref=vX.Y.Z` puts the release back, and `-f site_ref=main` beside it
  deploys a fix to this folder for that release.

`docs/releasing.md` has the one-time Pages settings (the source, and the tag rule the `github-pages`
environment needs) and what to do when a deploy fails.

## Updating the dependencies

`package.json` pins every dependency exactly. Starlight is 0.x and a few things here lean on its
internals and on Astro's Markdown processor (the two loaders in one collection, the Decisions group built
in `astro.config.mjs`, the dates the loader supplies, the page route the plugins hand to the links
validator), so read the release notes of every Astro or Starlight bump and run `npm run build` before
merging one.

mermaid 12 lays diagrams out with ELK by default. ELK (elkjs) is EPL-2.0, and everything the site serves
is MIT, ISC, BSD, Apache-2.0 or OFL, so the diagrams are drawn with dagre, elkjs is aliased to a stub
(`src/lib/no-elk.mjs`), and the build fails if ELK code reaches `dist/` by another import path.
