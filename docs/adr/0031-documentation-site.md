# ADR-0031: A documentation website that renders docs/ in place

- **Status**: accepted
- **Date**: 2026-10-25
- **Prior art**: [cleanarchitecture.jasontaylor.dev](https://cleanarchitecture.jasontaylor.dev), the documentation website of [CleanArchitecture](https://github.com/jasontaylordev/CleanArchitecture) by Jason Taylor (MIT): a website as the template's home, with the install command and a short quick start on its landing page, a linear path through getting started, an installation page that also updates and uninstalls, a page per option, an architecture page led by one diagram, and the documented version in the footer. The ideas only: no code or CSS was copied, and the look is the Lattice language of `eng/readme/`.

## Context

The comparisons in the README found the product behind on one point: the templates it is measured
against have a home on the web, and Trellis had a README and the files under `docs/` on GitHub, with no
navigation across them, no search and no page to land on before cloning anything.

Two constraints shape any answer. ADR-0014 keeps one doc set: humans and agents read the same `docs/`,
so a site with its own copy of the docs would drift from them. And `docs/` ships into every generated
project as plain GitHub Markdown, with template conditionals in HTML comments (`#if (UseRedis)` …
`#endif`) that GitHub hides and that mark what only some generated solutions have. A feasibility spike
(2026-10-18) rendered `docs/` in place with Astro 7 and Starlight, links and anchors validated.

## Decision

- **Astro and Starlight in `site/`.** Astro 7.4, Starlight 0.42 and starlight-links-validator, pinned
  exactly with a committed lockfile, on the Node build `site/.nvmrc` names (an exact 22.x patch, the floor the lockfile's packages set; `npm ci` refuses an older Node).
  `site/README.md` describes it.
- **`docs/` rendered in place.** A custom loader puts `docs/**/*.md` into Starlight's collection where
  the files are: no copy, no sync step, no frontmatter added. The H1 becomes the title, the first
  paragraph the description, the edit link points at the gated file, and one table in
  `site/trellis.config.mjs` places each doc in the sidebar. A doc stays plain GitHub Markdown; nothing
  in `docs/` depends on the site.
- **Conditionals as labelled asides.** Each conditional block is shown with the option that keeps it
  ("Only with `--use-redis`"), because the site documents the template and its readers are choosing
  options; `TRELLIS_SITE_CONDITIONALS=defaults` renders the docs the way `dotnet new trellis` with no
  options writes them instead. The site reads a directive only when its comment is alone on its line,
  which `scripts/check-template-toggles.sh` now requires of every directive (the engine drops the whole
  line anyway), and the build fails on a directive line it did not read as one.
- **Links rewritten, every one validated.** A link to another doc becomes that page's route with its
  `#anchor`; a link that leaves `docs/` goes to GitHub at the ref the site is built from. The links
  validator fails the build on any link or anchor that points nowhere, and `site.yml` builds the site on
  every pull request that touches what it renders.
- **No third-party request.** mermaid is a dependency, bundled as a chunk that only a page with a
  diagram fetches; Geist and Geist Mono come from the `@fontsource-variable` packages, with their licence
  served beside them. The site makes no request to any third party: no CDN, no font service, no
  analytics. mermaid 12 lays diagrams out with ELK by default; ELK (elkjs) is EPL-2.0, so the diagrams use
  dagre, elkjs is aliased to a stub and the build fails if ELK code reaches the output.
- **Deployed from the release tag.** `release.yml` calls `site.yml` with the tag once the package is
  published, never on a dry run, and every page's footer names the version it documents. GitHub Pages
  holds one copy: a manual preview (`workflow_dispatch`, any ref, `main` by default) replaces it and says
  so in a banner on every page; `site_ref` deploys a fix to `site/` for a release without a new release.
- **Maintainer-only.** `site/`, `site.yml` and this record are in both exclude lists and in Template CI's
  must-not-ship loop. Dependabot watches `/site` from a template-source block, and every npm update waits
  for a person (`npm_and_yarn` in `dependabot-auto-merge.yml`'s manual list): the simplest rule that
  keeps Astro and Starlight bumps in a person's hands.

## Consequences

- A Node toolchain beside .NET: about 3,300 lines in `site/` (1,000 of them CSS), a 240 KB lockfile,
  per CI run an `npm ci` of seconds from the npm cache (about a minute cold) and 5 to 15 seconds of
  build, and an 8 MB artifact, most of it mermaid's chunks, which only diagram pages fetch.
- The build leans on internals a minor release can change: `glob()` deleting entries it did not touch
  (each loader sees a scoped store), Starlight assuming `src/content/docs` (the Decisions group is built
  in `astro.config.mjs`, the loader supplies the dates, the page route goes to the links validator as a
  `slug`), Sätteri's plugin semantics, and how mermaid imports elkjs. So every Astro, Starlight or
  mermaid bump is read and built by hand, and the build-and-validate job is the gate.
- Licences. The direct dependencies are MIT or ISC, the fonts OFL-1.1. Two build-time transitive
  dependencies are not permissive: sharp's libvips (LGPL-3.0-or-later; sharp is Astro's optional image
  processor, and the site has no raster images) and lightningcss (MPL-2.0, Vite's CSS minifier). Both
  are acceptable under ADR-0011's amendment: nothing of either reaches a reader or a generated project.
  elkjs (EPL-2.0) is installed with mermaid and kept out of the output. What a reader downloads is MIT,
  ISC, BSD or Apache-2.0 (dompurify under its Apache-2.0 option; robust-predicates, inside d3, is
  Unlicense) and the OFL fonts.
- A preview replaces the released site until the next deploy from a tag; `docs/releasing.md` says how
  to put the release back and which two settings only the repository's owner can make (the Pages
  source, and a `v*` tag rule on the `github-pages` environment).

## Alternatives / Revisit when

- DocFX: built for API reference, further from GitHub's Markdown, and it would need the same adapter for
  conditionals and links. MkDocs Material: a Python toolchain and the same adapter, with `docs/`
  copied or linked into its own folder. GitHub Pages straight from the Markdown (Jekyll): no build to
  check the links and anchors, and the conditionals vanish unlabelled. A separate site repository: a
  second doc set, which ADR-0014 rules out.
- Revisit when Starlight supports content outside `src/content/docs` (most of the workarounds go), or
  when pages must work without JavaScript (mermaid rendered at build time, with a headless browser in
  the build).

## Amendment (2026-10-26): why not Blazor

The question a reader of this template asks first is why its website is not Blazor, when the template
recommends Blazor for the application. The two jobs differ. The application UI is interactive, signed
in, backed by an API and a database, and Blazor is the right tool for it. The website is a few dozen
pages of Markdown that must paint on the first request, read without JavaScript, be indexed by search
engines, carry a table of contents, a sidebar, search and link validation, and host as static files on
GitHub Pages for nothing. Blazor WebAssembly would download a runtime of several megabytes before the
first page of text and brings none of the Markdown tooling; Blazor Server needs a server the site does
not have; the .NET static-site generators were young when this was decided and would still need the
adapter for conditionals and links that this ADR describes. Starlight does the job in a few hundred
lines of configuration, which is also what the prior art above uses. The cost accepted was a Node
toolchain in the repository, kept maintainer-only so a generated project never needs it. Revisit if a
.NET static documentation generator reaches Starlight's level of Markdown support and link checking.
