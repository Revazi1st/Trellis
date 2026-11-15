// Facts the landing and Install pages print, read from the repository at build time so they cannot
// drift from it. Anything that needs a test run (a count of passing tests) is deliberately absent.
import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { evaluate, identifiers, parseExpression } from '../lib/conditionals.mjs';
import { constantSymbols, github, pitch, pitchShort, readTemplateOptions, repoRoot } from '../../trellis.config.mjs';

const read = (p) => readFileSync(path.join(repoRoot, p), 'utf8');
const json = (p) => JSON.parse(read(p));

function find(dir, test, out = []) {
  for (const name of readdirSync(path.join(repoRoot, dir))) {
    const rel = path.posix.join(dir, name);
    if (statSync(path.join(repoRoot, rel)).isDirectory()) find(rel, test, out);
    else if (test(name)) out.push(rel);
  }
  return out;
}

const template = json('.template.config/template.json');

export const templateShortName = template.shortName; // trellis
export const packageId = template.symbols.templatePackageId.parameters.value; // Trellis.Templates
export const options = readTemplateOptions();
export const toggles = options.filter((o) => !o.convenience);
export { pitch, pitchShort };

/** A template.json glob (`**`, `*`) as a regular expression over a repository path. */
function globRegex(glob) {
  const literal = (s) => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&');
  const segment = (s) => s.split('**').map((p) => p.split('*').map(literal).join('[^/]*')).join('.*');
  return new RegExp(`^${glob.split('**/').map(segment).join('(?:.*/)?')}$`);
}

// Whether a repository file can reach a generated project: template.json's first source minus its
// unconditional excludes (the maintainer's files: the site, its ADR, the release workflow).
const excluded = (template.sources?.[0]?.exclude ?? []).map(globRegex);
const ships = (repoPath) => !excluded.some((re) => re.test(repoPath));

// Whether a file is in a solution created with no options: what ships, less the conditional excludes
// (template.json's modifiers) whose condition holds for the default values, such as the browser tests,
// which only --include-ui-tests brings. A modifier without a condition applies always, as the engine has
// it. A condition may name the boolean parameters and the constant symbols, which are false once the
// engine runs (TemplateSource); any other symbol (a computed one) would evaluate as false without a word
// and miscount, so it stops the build until this code learns its value.
const defaults = Object.fromEntries(options.map((o) => [o.symbol, o.defaultValue]));
const knownSymbols = new Set([...Object.keys(defaults), ...Object.keys(constantSymbols)]);
const excludedByDefault = (template.sources?.[0]?.modifiers ?? []).flatMap((m, i) => {
  if (m.condition === undefined) return (m.exclude ?? []).map(globRegex);
  if (typeof m.condition !== 'string') throw new Error(`.template.config/template.json: modifier ${i} has a condition that is not a string`);
  const condition = parseExpression(m.condition);
  const unknown = [...identifiers(condition)].filter((name) => !knownSymbols.has(name));
  if (unknown.length) {
    throw new Error(`.template.config/template.json: modifier ${i} (${m.condition}) names ${unknown.join(', ')}, which is no boolean parameter; teach shipsByDefault in site/src/data/repo.mjs its value in a default solution`);
  }
  return evaluate(condition, defaults) ? (m.exclude ?? []).map(globRegex) : [];
});
const shipsByDefault = (repoPath) => ships(repoPath) && !excludedByDefault.some((re) => re.test(repoPath));

const globalJson = json('global.json');
const openapi = json('docs/api/openapi.v1.json');

export const sdkVersion = globalJson.sdk.version; // the repository's pinned SDK, rolled forward

// The oldest SDK `dotnet new` runs the template on: template.json's sdk constraint, "[10.0.100,)".
export const sdkTemplateFloor = (() => {
  const args = template.constraints?.sdk?.args;
  const floor = typeof args === 'string' ? args.match(/^\[([^,\]]+),\)$/)?.[1] : undefined;
  if (!floor) throw new Error(`.template.config/template.json: constraints.sdk.args is ${JSON.stringify(args)}, not of the shape [X,)`);
  return floor;
})();

// The Aspire CLI the docs ask for: the last cell, a bare version, of the row of docs/development.md's
// prerequisites table that starts "| **Aspire CLI**".
export const aspireCliFloor = (() => {
  const row = read('docs/development.md').split(/\r?\n/).find((line) => line.startsWith('| **Aspire CLI**'));
  const cell = row?.replace(/\|\s*$/, '').split('|').at(-1).trim();
  if (!cell || !/^\d+(\.\d+)*$/.test(cell)) throw new Error(`docs/development.md: no "| **Aspire CLI**" row whose last cell is a bare version (found ${JSON.stringify(row ?? null)})`);
  return cell;
})();

// The administrator the migration worker seeds in Development, from its options' defaults.
export const seededAdmin = (() => {
  const file = 'tools/Trellis.MigrationService/SeedOptions.cs';
  const source = read(file);
  const value = (re, what) => {
    const m = source.match(re);
    if (!m) throw new Error(`${file}: no ${what} (format changed?)`);
    return m[1];
  };
  // The password is AdminPassword's default, which is the DefaultAdminPassword constant: both are read, so
  // a default that stops being the constant fails here instead of printing the constant.
  if (!/AdminPassword \{ get; init; \} = DefaultAdminPassword;/.test(source)) {
    throw new Error(`${file}: AdminPassword's default is not DefaultAdminPassword (format changed?); read the seeded password from where it now comes`);
  }
  return {
    email: value(/AdminEmail \{ get; init; \} = "([^"]+)"/, 'AdminEmail default'),
    password: value(/DefaultAdminPassword = "([^"]+)"/, 'DefaultAdminPassword'),
  };
})();

// Counted in a solution created with no options, so a maintainer-only file (ADR-0031) is not counted, and
// neither is one only an option brings.
const csproj = (n) => n.endsWith('.csproj');
export const facts = [
  { value: find('src', csproj).filter(shipsByDefault).length, label: 'source projects in a default solution' },
  { value: find('tests', csproj).filter(shipsByDefault).length, label: 'test projects in a default solution' },
  {
    value: Object.values(openapi.paths).flatMap((p) => Object.keys(p).filter((m) => ['get', 'post', 'put', 'patch', 'delete'].includes(m))).length,
    label: 'operations in the committed OpenAPI contract',
  },
  { value: find('docs/adr', (n) => /^\d{4}-.*\.md$/.test(n)).filter(shipsByDefault).length, label: 'decision records in a default solution' },
  { value: toggles.length, label: 'toggles; scaffold only what you need' },
];

// What a default solution gives an agent (docs/INDEX.md lists each piece): the rules, skills, reviewers and
// hooks under .claude/, and the MCP servers of .mcp.json. The landing page tables them.
export const agentToolkit = (() => {
  const under = (dir, pick) =>
    readdirSync(path.join(repoRoot, dir))
      .map((name) => pick(name))
      .filter((entry) => entry && shipsByDefault(entry.file))
      .map((entry) => entry.name)
      .sort();
  const fileIn = (dir, ext) => (name) => (name.endsWith(ext) ? { name: name.slice(0, -ext.length), file: `${dir}/${name}` } : null);
  const kinds = [
    { kind: 'Rules', folder: '.claude/rules/', names: under('.claude/rules', fileIn('.claude/rules', '.md')) },
    {
      kind: 'Skills',
      folder: '.claude/skills/',
      names: under('.claude/skills', (name) => {
        const file = `.claude/skills/${name}/SKILL.md`;
        return statSync(path.join(repoRoot, '.claude/skills', name)).isDirectory() && existsSync(path.join(repoRoot, file)) ? { name, file } : null;
      }),
    },
    { kind: 'Reviewers', folder: '.claude/agents/', names: under('.claude/agents', fileIn('.claude/agents', '.md')) },
    { kind: 'Hooks', folder: '.claude/hooks/', names: under('.claude/hooks', fileIn('.claude/hooks', '.sh')).filter((name) => name !== 'common') },
    { kind: 'MCP servers', folder: '.mcp.json', names: Object.keys(json('.mcp.json').mcpServers ?? {}) },
  ];
  for (const k of kinds) if (k.names.length === 0) throw new Error(`agentToolkit: read no ${k.kind.toLowerCase()} from ${k.folder} (format changed?)`);
  return kinds;
})();

// The quick-start strings, shared by the landing page and Install and create (forward slashes, so every
// line runs unchanged in bash, zsh and PowerShell). scripts/check-readme-sync.sh reads the lines between
// the two marker comments and holds the README's commands to them: keep each key on one line, each string a
// backtick or single-quote literal, and no substitution but ${packageId} and ${templateShortName}.
// readme-sync:quickstart
export const quickstart = {
  install: `dotnet new install ${packageId}`,
  create: `dotnet new ${templateShortName} -n Acme.Shop -o Acme.Shop`,
  createWithToggles: `dotnet new ${templateShortName} -n Acme.Tenants -o Acme.Tenants --use-multitenancy --use-redis --use-serilog`,
  run: ['cd Acme.Shop', 'dotnet run --project tools/Acme.Shop.AppHost'],
  update: [`dotnet new update --check-only`, `dotnet new update`],
  uninstall: `dotnet new uninstall ${packageId}`,
  fromClone: ['git clone https://github.com/Revazi1st/Trellis.git', 'dotnet new install ./Trellis', `dotnet new ${templateShortName} -n Acme.Shop -o Acme.Shop`],
};
// readme-sync:end

// What this build is. A release build's ref is a release tag (v1.2.3, the tag release.yml published and
// passes on); any other ref (a branch, a SHA) builds a preview, and every page says so in a banner. git
// answers which releases exist (CI checks out the full history, tags included) and which commit this is;
// without git the site still builds, as a preview that knows of no release.
const RELEASE_TAG = /^v(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)$/;
const git = (...args) => {
  try {
    return execFileSync('git', args, { cwd: repoRoot, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch {
    return '';
  }
};
const releases = git('tag', '--list', 'v*', '--sort=-v:refname').split('\n').filter((tag) => RELEASE_TAG.test(tag));

export const build = {
  /** The ref the site is built from (TRELLIS_SITE_REF, default main). */
  ref: github.ref,
  /** The commit, abbreviated, when git can tell. */
  commit: git('rev-parse', '--short=7', 'HEAD') || null,
  /** The template version this build documents: set for a release build only. */
  version: github.ref.match(RELEASE_TAG)?.[1] ?? null,
  /** The newest release tag in the history, a final version before a pre-release. */
  latestRelease: releases.find((tag) => !tag.includes('-')) ?? releases[0] ?? null,
};
/** Whether the package is on nuget.org: this build is a release, or an earlier one exists. */
export const packagePublished = Boolean(build.version || build.latestRelease);
export const nugetUrl = `https://www.nuget.org/packages/${packageId}`;
export const releaseNotesUrl = (tag) => `${github.url}/releases/tag/${tag}`;
