# The documentation site

The Trellis website: [Astro](https://astro.build) with [Starlight](https://starlight.astro.build),
rendering the repository's own `docs/` **in place**. There is no copy of a doc in this folder and no
sync step: the pages are the files under `docs/`, read where they are at build time, each with an edit
link back to the gated file. This folder holds only what the site adds around them: the landing page,
the Install and create page, the sidebar, the look, and the code that adapts `docs/` to Starlight.

The site, its workflow and its decision record are maintainer material: `template.json` and the pack
project exclude this folder, so a generated project never contains it.

## Run it locally

You need Node 22 (`.nvmrc` here; Astro 7.4 needs 22.22 or later, and `npm ci` refuses an older one).

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
  on a directive line in `docs/` it did not read as one.
- **Testing**: the `## Tests` section of `development.md` is rendered as a page of its own, and links
  to its anchors follow it there.
- **Mermaid**: a `mermaid` fence is drawn as a diagram in the browser.

## Settings

Environment variables the build reads (all optional):

| Variable | Default | What it changes |
|---|---|---|
| `TRELLIS_SITE_REF` | `main` | The ref the GitHub links point at (files outside `docs/`, excerpts). |
| `TRELLIS_SITE_CONDITIONALS` | `label` | `defaults` renders the docs the way `dotnet new trellis` with no options writes them: each conditional block evaluated against the defaults, with no asides. |
| `TRELLIS_SITE_LASTUPDATED` | unset | `git` shows each page's last commit date, read from `git log`; it needs the full history (`fetch-depth: 0` in CI). |
| `TRELLIS_REPO_ROOT` | `..` | Where the repository is, for a checkout of the site outside it. |

For example, `TRELLIS_SITE_CONDITIONALS=defaults npm run build` checks that the docs read well without
the asides.

## Updating Astro and Starlight

`package.json` pins every dependency exactly. Starlight is 0.x and a few things here lean on its
internals and on Astro's Markdown processor (the two loaders in one collection, the Decisions group built
in `astro.config.mjs`, the dates the loader supplies, the page route the plugins hand to the links
validator), so read the release notes of every Astro or Starlight bump and run `npm run build` before
merging one.
