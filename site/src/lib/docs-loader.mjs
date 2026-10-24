// The docs collection: two glob() loaders into one store, plus the section pages.
//   - site-only pages: src/content/docs/**/*.mdx (the landing, Install and create)
//   - docs/**/*.md read IN PLACE from the repository, no copy: the page title comes from the H1,
//     the edit link points at the gated file on GitHub, the route comes from trellis.config.mjs
//   - sectionPages: one section of a doc rendered as a page of its own (Testing)
//
// glob() deletes every store entry it did not touch on this run, so each glob gets a view of the store
// that only lists the entries under its own base directory.

import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { glob } from 'astro/loaders';
import {
  docsDir,
  githubEdit,
  lastUpdatedFromGit,
  repoRoot,
  routeForDoc,
  routes,
  sectionPages,
} from '../../trellis.config.mjs';
import { description, section, title } from './doc-text.mjs';

const toPosix = (p) => p.split(path.sep).join('/');

export function trellisDocsLoader() {
  return {
    name: 'trellis-docs-loader',
    load: async (context) => {
      const root = fileURLToPath(context.config.root);
      const docsFromRoot = toPosix(path.relative(root, docsDir));
      const dates = lastUpdatedFromGit ? gitDates(context.logger) : new Map();

      // 1. Site-only pages.
      await glob({ base: './src/content/docs', pattern: '**/[^_]*.{md,mdx}' }).load({
        ...context,
        store: scopedStore(context.store, 'src/content/docs/'),
      });

      // 2. docs/, in place.
      const unmapped = [];
      await glob({
        base: docsDir,
        pattern: '**/*.md',
        generateId: ({ entry }) => {
          if (!routes[entry] && !/^adr\/\d{4}-/.test(entry)) unmapped.push(entry);
          return routeForDoc(entry);
        },
      }).load({
        ...context,
        store: scopedStore(context.store, `${docsFromRoot}/`),
        parseData: async (props) =>
          context.parseData({ ...props, data: { ...derivedData(props.filePath, dates), ...props.data } }),
      });
      if (unmapped.length) context.logger.warn(`docs without a route in trellis.config.mjs (kept their path): ${unmapped.join(', ')}`);

      // 3. Sections rendered as pages of their own.
      for (const sp of sectionPages) {
        const source = path.join(docsDir, sp.from);
        const { body } = section(readFileSync(source, 'utf8'), sp.anchor);
        // The frontmatter tells the Markdown plugins which page this compile is (renderMarkdown parses it).
        const content = `---\ntrellisSection: ${sp.anchor}\n---\n\n${body}\n`;
        const rendered = await context.renderMarkdown(content, { fileURL: pathToFileURL(source) });
        const rel = toPosix(path.relative(docsDir, source));
        const data = await context.parseData({
          id: sp.route,
          filePath: source,
          data: {
            title: sp.title,
            description: sp.description,
            editUrl: githubEdit(`docs/${rel}`),
            ...(dates.has(`docs/${rel}`) ? { lastUpdated: dates.get(`docs/${rel}`) } : {}),
          },
        });
        context.store.set({
          id: sp.route,
          data,
          body,
          filePath: toPosix(path.relative(root, source)),
          digest: context.generateDigest(content),
          rendered,
        });
      }
    },
  };
}

/** What a docs/ file would have put in frontmatter, derived from the file instead. */
function derivedData(filePath, dates) {
  const rel = toPosix(path.relative(docsDir, filePath));
  const text = readFileSync(filePath, 'utf8');
  const pageTitle = title(text);
  if (!pageTitle) throw new Error(`docs/${rel} has no H1 to take the page title from`);
  const data = {
    title: pageTitle,
    editUrl: githubEdit(`docs/${rel}`),
    sidebar: { label: sidebarLabel(rel, pageTitle) },
  };
  const desc = description(text);
  if (desc) data.description = desc;
  if (dates.has(`docs/${rel}`)) data.lastUpdated = dates.get(`docs/${rel}`);
  if (rel === 'INDEX.md') {
    // The agents' entry point: a page you can reach, not one search should offer.
    data.pagefind = false;
    data.sidebar.hidden = true;
  }
  return data;
}

/** "ADR-0018: Transactional outbox …" -> "0018 · Transactional outbox …"; "Feature: Auth" -> "Auth". */
export function sidebarLabel(rel, pageTitle) {
  const adr = pageTitle.match(/^ADR-(\d{4}):\s*(.*)$/);
  if (adr) return `${adr[1]} · ${adr[2]}`;
  return pageTitle.replace(/^Feature:\s*/, '').replace(/\s*\(.*\)$/, '');
}

/** A view of the store whose keys() lists only the entries whose file lives under `prefix`. */
function scopedStore(store, prefix) {
  return new Proxy(store, {
    get(target, prop) {
      if (prop === 'keys') {
        return () => [...target.keys()].filter((id) => target.get(id)?.filePath?.startsWith(prefix));
      }
      const value = Reflect.get(target, prop);
      return typeof value === 'function' ? value.bind(target) : value;
    },
  });
}

/**
 * Newest commit date per file under docs/, in one `git log` (what Starlight does for src/content/docs).
 * Opt-in (TRELLIS_SITE_LASTUPDATED=git): it needs the full history, `fetch-depth: 0` in CI.
 */
function gitDates(logger) {
  const dates = new Map();
  try {
    const out = execFileSync('git', ['log', '--format=t:%ct', '--name-only', '--', 'docs'], {
      cwd: repoRoot,
      encoding: 'utf8',
      maxBuffer: 64 * 1024 * 1024,
    });
    let time = 0;
    for (const line of out.split('\n')) {
      if (line.startsWith('t:')) time = Number(line.slice(2)) * 1000;
      else if (line && !dates.has(line)) dates.set(line, new Date(time));
    }
  } catch (error) {
    logger.warn(`lastUpdated: git log failed (${error.message}); pages show no date`);
  }
  return dates;
}
