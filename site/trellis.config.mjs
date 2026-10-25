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
export const siteDir = process.env.TRELLIS_SITE_DIR ?? path.dirname(fileURLToPath(import.meta.url));
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
  'design-system.md': 'develop/design-system',
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
  // The toggles in template.json's order, the conveniences after them.
  return [...options.filter((o) => !o.convenience), ...options.filter((o) => o.convenience)];
}

// What the Options page (start/options) says about each option beyond what template.json holds. The page
// generates one section per boolean option from template.json (the description, the default, the files
// only that option brings, the reminder `dotnet new` prints) and adds, from here, what to do after creating
// a solution with it and the docs that explain it. Inline `code` and [links](development.md#anchor) to
// files under docs/ are allowed; the links validator checks them. A boolean option without an entry fails
// the build, so a new toggle gets its section in the change that adds it.
export const optionNotes = {
  IncludeUI: {
    after: 'Nothing: the AppHost starts the `web` resource beside the `api`. With `--include-ui false` the solution is API-only.',
    docs: ['adr/0015-blazor-everywhere-shared-ui.md', 'adr/0012-presentation-structure-common-libraries.md'],
  },
  UseRedis: {
    after: 'Nothing for `aspire run`: the AppHost starts the `cache` resource, with a RedisInsight tile, and the API reads its connection string. In the dev-container flow, `docker compose --profile redis up -d` starts Redis.',
    docs: ['adr/0026-read-caching.md', 'deployment.md#deploy-to-one-host'],
  },
  UseSerilog: {
    after: 'Nothing. Serilog writes the console and hands every event on to OpenTelemetry; its levels come from the `Serilog` section, which mirrors `Logging:LogLevel` but is not read from it.',
    docs: ['deployment.md#observability'],
  },
  UseMultitenancy: {
    after: 'Regenerate the shipped initial migration once, before the first run: it has no tenant columns. The two commands are in [Multi-tenancy](development.md#multi-tenancy-optional).',
    docs: ['development.md#multi-tenancy-optional', 'adr/0019-multitenancy-toggle.md', 'adr/0022-tenant-on-the-principal.md'],
  },
  IncludeUiTests: {
    after: 'Build the project once and install a browser before the first browser run; the tests skip unless `RUN_UI_TESTS=1`. See [UI tests](development.md#ui-tests-optional).',
    docs: ['development.md#ui-tests-optional', 'development.md#tests'],
  },
  skipRestore: {
    after: 'Run `dotnet restore` yourself before the first build.',
    docs: [],
  },
};

/** docs/-relative target ("development.md#tests") -> the site URL, with the base. */
export function docHref(target) {
  const [file, anchor] = target.split('#');
  const section = anchor && sectionPages.find((sp) => sp.from === file && sp.anchor === anchor);
  if (section) return `${base}/${section.route}/`;
  return `${base}/${routeForDoc(file)}/${anchor ? `#${anchor}` : ''}`;
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
