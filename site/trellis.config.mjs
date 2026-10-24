// One place for everything the site knows about the repository it renders.
// Read by astro.config.mjs, the docs loader, the Markdown plugins and the components.
//
// Layout: this folder is `site/` in the repository, so the repository root is `..` and the docs are
// `../docs`, read in place. TRELLIS_REPO_ROOT points somewhere else (a checkout outside the repository).

import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// This module's folder is the site folder when Node loads it (astro.config.mjs imports it first), but a
// chunk under dist/.prerender/ once the build bundles it for prerendering, in the same process. So the
// first evaluation hands the real folder down through the environment and later ones read it back.
const siteDir = process.env.TRELLIS_SITE_DIR ?? path.dirname(fileURLToPath(import.meta.url));
process.env.TRELLIS_SITE_DIR = siteDir;

export const repoRoot = path.resolve(siteDir, process.env.TRELLIS_REPO_ROOT ?? '..');
export const docsDir = path.join(repoRoot, 'docs');

export const site = 'https://revazi1st.github.io';
export const base = '/Trellis';

// Links that leave docs/ go to GitHub at this ref. The release build passes the tag (v0.1.0).
export const github = {
  owner: 'Revazi1st',
  repo: 'Trellis',
  ref: process.env.TRELLIS_SITE_REF ?? 'main',
  get url() {
    return `https://github.com/${this.owner}/${this.repo}`;
  },
};
export const githubBlob = (repoPath) => `${github.url}/blob/${github.ref}/${repoPath}`;
export const githubTree = (repoPath) => `${github.url}/tree/${github.ref}/${repoPath.replace(/\/$/, '')}`;
// Edits always land on main: that is where the gated file lives.
export const githubEdit = (repoPath) => `${github.url}/edit/main/${repoPath}`;

// docs/<file> -> site route (without base or slashes): the sidebar groups, as URLs.
// A file not listed here keeps its own path (docs/foo/bar.md -> foo/bar) and the build warns.
export const routes = {
  'architecture.md': 'understand/architecture',
  'development.md': 'develop/development',
  'upgrading.md': 'develop/upgrading',
  'releasing.md': 'develop/releasing',
  'deployment.md': 'operate/deployment',
  'threat-model.md': 'operate/threat-model',
  'features/invoices.md': 'features/invoices',
  'features/auth.md': 'features/auth',
  'features/exchange-rates.md': 'features/exchange-rates',
  // The agent entry point: reachable, searchable off, not in the sidebar.
  'INDEX.md': 'reference/docs-index',
};

/** Route for a path relative to docs/ (POSIX, with the .md extension). */
export function routeForDoc(relPath) {
  if (routes[relPath]) return routes[relPath];
  const adr = relPath.match(/^adr\/(\d{4}-[^/]+)\.md$/);
  if (adr) return `decisions/${adr[1]}`;
  return relPath.replace(/\.md$/, '');
}

// A section of a doc rendered as a page of its own, without splitting the file on disk.
// `anchor` is the GitHub slug of the section's heading; links to it (and to the headings under it)
// are rewritten to the new page, and the source page shows a pointer in its place.
export const sectionPages = [
  {
    from: 'development.md',
    anchor: 'tests',
    route: 'develop/testing',
    title: 'Testing',
    description:
      'What each test project proves about which layer, what it needs to run, and when a test of yours belongs in it.',
  },
];

// Template options, read from the template's own configuration at build time.
export function readTemplateOptions() {
  const tpl = JSON.parse(readFileSync(path.join(repoRoot, '.template.config/template.json'), 'utf8'));
  const host = JSON.parse(readFileSync(path.join(repoRoot, '.template.config/dotnetcli.host.json'), 'utf8'));
  const options = [];
  for (const [symbol, def] of Object.entries(tpl.symbols ?? {})) {
    if (def.type !== 'parameter' || def.datatype !== 'bool') continue;
    const longName = host.symbolInfo?.[symbol]?.longName;
    if (!longName) throw new Error(`dotnetcli.host.json has no longName for the bool symbol ${symbol}`);
    options.push({
      symbol,
      longName,
      flag: `--${longName}`,
      defaultValue: String(def.defaultValue).toLowerCase() === 'true',
      displayName: def.displayName ?? symbol,
      description: def.description ?? '',
      // skipRestore is a CLI convenience, not a toggle of the generated solution.
      convenience: symbol === 'skipRestore',
    });
  }
  return options;
}

// How the docs' <!--#if (EXPR) --> blocks render:
//   'label'    (default) each block a toggle aside: "Only with --use-multitenancy"
//   'defaults' evaluated against the template defaults, as `dotnet new trellis` with no flags writes them
export const conditionalsMode = process.env.TRELLIS_SITE_CONDITIONALS === 'defaults' ? 'defaults' : 'label';

// Symbols that are constants rather than options. The site renders the template's own repository,
// where TemplateSource blocks are live text (the engine strips them from a generated project).
export const constantSymbols = { TemplateSource: true };

// lastUpdated for docs/ comes from `git log` over docs/ in the repository (Starlight's own lookup
// only covers src/content/docs). Opt-in, because it needs the full history (fetch-depth: 0 in CI).
export const lastUpdatedFromGit = process.env.TRELLIS_SITE_LASTUPDATED === 'git';
