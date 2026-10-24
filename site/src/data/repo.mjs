// Facts the landing and Install pages print, read from the repository at build time so they cannot
// drift from it. Anything that needs a test run (a count of passing tests) is deliberately absent.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { readTemplateOptions, repoRoot } from '../../trellis.config.mjs';

const read = (p) => readFileSync(path.join(repoRoot, p), 'utf8');
const json = (p) => JSON.parse(read(p));

function find(dir, test, out = []) {
  for (const name of readdirSync(path.join(repoRoot, dir))) {
    const rel = path.join(dir, name);
    if (statSync(path.join(repoRoot, rel)).isDirectory()) find(rel, test, out);
    else if (test(name)) out.push(rel);
  }
  return out;
}

const template = json('.template.config/template.json');
const globalJson = json('global.json');
const openapi = json('docs/api/openapi.v1.json');

export const templateShortName = template.shortName; // trellis
export const packageId = template.symbols.templatePackageId.parameters.value; // Trellis.Templates
export const sdkVersion = globalJson.sdk.version; // the repository's pinned SDK, rolled forward
export const options = readTemplateOptions();
export const toggles = options.filter((o) => !o.convenience);

export const facts = [
  { value: find('src', (n) => n.endsWith('.csproj')).length, label: 'source projects in four rings' },
  { value: find('tests', (n) => n.endsWith('.csproj')).length, label: 'test projects, unit to browser' },
  {
    value: Object.values(openapi.paths).flatMap((p) => Object.keys(p).filter((m) => ['get', 'post', 'put', 'patch', 'delete'].includes(m))).length,
    label: 'operations in the committed OpenAPI contract',
  },
  { value: readdirSync(path.join(repoRoot, 'docs/adr')).filter((f) => /^\d{4}-.*\.md$/.test(f)).length, label: 'decision records with their why' },
  { value: toggles.length, label: 'toggles; scaffold only what you need' },
];

// The quick-start strings, shared by the landing page and Install and create (forward slashes, so every
// line runs unchanged in bash, zsh and PowerShell).
export const quickstart = {
  install: `dotnet new install ${packageId}`,
  create: `dotnet new ${templateShortName} -n Acme.Shop -o Acme.Shop`,
  createWithToggles: `dotnet new ${templateShortName} -n Acme.Tenants -o Acme.Tenants --use-multitenancy --use-redis --use-serilog`,
  run: ['cd Acme.Shop', 'dotnet run --project tools/Acme.Shop.AppHost'],
  update: [`dotnet new update --check-only`, `dotnet new update`],
  uninstall: `dotnet new uninstall ${packageId}`,
  fromClone: ['git clone https://github.com/Revazi1st/Trellis.git', 'dotnet new install ./Trellis', `dotnet new ${templateShortName} -n Acme.Shop -o Acme.Shop`],
};
