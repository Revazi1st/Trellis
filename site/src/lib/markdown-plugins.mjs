// Sätteri mdast plugins (Astro 7's default Markdown processor) that let the site render docs/ in place.
// They run before Starlight's own plugins (asides, heading links) and before starlight-links-validator,
// and they work on the Markdown AST, so anything inside a code fence is never touched.
//
// For files under docs/:
//   1. drop the leading H1 (the loader made it the page title; docs/ has no frontmatter and gains none)
//   2. a section rendered as its own page (sectionPages): on the source page the section shrinks to a
//      pointer; on the new page the section heading goes and the headings under it move up a level
//   3. template conditionals <!--#if (EXPR) --> … <!--#endif -->: a toggle aside per block, or evaluated
//      against the template defaults (conditionalsMode)
//   4. links: docs/*.md -> site routes (#anchors kept, moved anchors followed); anything else -> GitHub
//   5. ```mermaid fences -> <pre class="mermaid">, rendered in the browser; a fence without a language,
//      or a text fence, that draws with box characters (an ASCII diagram, a file tree) -> a <pre> whose
//      type fits the column
//   6. the page's route as frontmatter `slug`, so the links validator files its headings under the page
//      the reader sees (it would otherwise derive the page from the path under src/content/docs)
//   7. a lead picture above the first paragraph of a page that has one (leadFigures): the rings on
//      Architecture, which the doc cannot embed itself (they live in eng/readme/, which never ships)
//   8. a heading that names its folder in parentheses, "## Port (`src/Core/…/Abstractions/`)"
//      (whereItLives): the heading keeps the name and the id GitHub gives the whole text, which is what
//      every link to it targets; the path moves to a small line under it, a link to the folder on GitHub
// For site pages under src/content/docs (.mdx):
//   root-relative links and JSX href props get the base (/Trellis), so pages are written base-free, and
//   a {/* trellis:option-sections */} comment becomes one section per template option (options.mjs).
// And two hast plugins, for files under docs/: tables, and the lines under headings. A table whose rows
// read better as blocks (tableLayout) gets a class, each cell its column's name and the table ARIA roles;
// code in table cells and in a where-it-lives line gets break opportunities after / and . (a narrow
// column wraps a path at its seams); lattice.css lays it out.

import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  base,
  conditionalsMode,
  constantSymbols,
  docsDir,
  githubBlob,
  githubTree,
  readTemplateOptions,
  repoRoot,
  routeForDoc,
  sectionPages,
} from '../../trellis.config.mjs';
import { describe, directiveLines, evaluate, fold, identifiers, parseDirective } from './conditionals.mjs';
import { optionSections } from './options.mjs';
import { section as readSection } from './doc-text.mjs';
import GithubSlugger from 'github-slugger';

const posix = path.posix;
const toPosix = (p) => p.split(path.sep).join('/');

const options = readTemplateOptions();
const optionsBySymbol = Object.fromEntries(options.map((o) => [o.symbol, o]));
const defaults = Object.fromEntries(options.map((o) => [o.symbol, o.defaultValue]));

// Which anchors each section page takes with it, read once from the source file.
const movedSections = sectionPages.map((sp) => {
  const { anchors, depth, body } = readSection(readFileSync(path.join(docsDir, sp.from), 'utf8'), sp.anchor);
  return { ...sp, anchors: new Set(anchors), depth, text: body };
});

export const warnings = [];
const warn = (msg) => {
  if (!warnings.includes(msg)) {
    warnings.push(msg);
    console.warn(`[trellis-markdown] ${msg}`);
  }
};

/** Plugin entries for satteri's `mdastPlugins`: factories that pick a plugin per file. */
export function trellisMdastPlugins({ siteContentDir }) {
  return [
    (ctx) => {
      if (!ctx.fileURL) return null;
      const file = fileURLToPath(ctx.fileURL);
      if (isInside(file, docsDir)) return docsPlugin(toPosix(path.relative(docsDir, file)));
      if (isInside(file, siteContentDir)) return sitePagePlugin();
      return null;
    },
  ];
}

/** Plugin entries for satteri's `hastPlugins`. */
export function trellisHastPlugins() {
  return [
    (ctx) => {
      if (!ctx.fileURL) return null;
      return isInside(fileURLToPath(ctx.fileURL), docsDir) ? [tablesPlugin(), whereLinesPlugin()] : null;
    },
  ];
}

const isInside = (file, dir) => {
  const rel = path.relative(dir, file);
  return rel && !rel.startsWith('..') && !path.isAbsolute(rel);
};

function docsPlugin(rel) {
  let sectionMode = null; // the section page being rendered, when this compile is one
  return {
    name: 'trellis-docs',
    before(root, ctx) {
      const astro = ctx.data.astro;
      const fm = astro?.frontmatter ?? {};
      sectionMode = fm.trellisSection ? movedSections.find((s) => s.from === rel && s.anchor === fm.trellisSection) : null;
      if (astro) astro.frontmatter = { ...fm, slug: sectionMode ? sectionMode.route : routeForDoc(rel) };

      // Every directive line of the source (outside fences) must be a directive node of its own, or the
      // block it opens would show on every page. check-template-toggles.sh keeps them alone on their
      // lines; this catches what the parser still reads differently (a directive indented as code, or
      // inside a larger HTML block).
      const source = sectionMode ? sectionMode.text : readFileSync(path.join(docsDir, rel), 'utf8');
      const expected = directiveLines(source).length;
      const found = countDirectives(root);
      if (found !== expected) {
        throw new Error(
          `docs/${rel}${sectionMode ? ` (#${sectionMode.anchor})` : ''}: ${expected} template directive line(s), but ${found} read as directives. ` +
            'Keep each directive alone on its line, outside indented code and HTML blocks.',
        );
      }

      let kids = [...root.children];
      let changed = false;

      // 1. The title is the H1: drop it.
      const h1 = kids.findIndex((n) => n.type === 'heading' && n.depth === 1);
      if (h1 !== -1 && kids.slice(0, h1).every((n) => n.type === 'yaml' || n.type === 'html')) {
        kids.splice(h1, 1);
        changed = true;
      }

      // 2. Sections that are pages of their own.
      const depthOf = (n) => (sectionMode && n.depth > sectionMode.depth ? n.depth - 1 : n.depth);
      if (sectionMode) {
        const first = kids.findIndex((n) => n.type === 'heading');
        if (first !== -1) kids.splice(first, 1);
        for (const n of kids) {
          if (n.type === 'heading' && depthOf(n) !== n.depth) ctx.setProperty(n, 'depth', depthOf(n));
        }
        changed = true;
      } else {
        for (const sp of movedSections.filter((s) => s.from === rel)) {
          const start = kids.findIndex((n) => n.type === 'heading' && n.depth === sp.depth && slugOf(ctx.textContent(n)) === sp.anchor);
          if (start === -1) {
            warn(`${rel}: no section #${sp.anchor} to move to ${sp.route}`);
            continue;
          }
          let end = kids.findIndex((n, i) => i > start && n.type === 'heading' && n.depth <= sp.depth);
          if (end === -1) end = kids.length;
          kids.splice(start + 1, end - start - 1, pointerTo(sp));
          changed = true;
        }
      }

      // 7. The lead picture, above the first paragraph (the H1 is gone by now; the paths under headings
      // are not yet, so the first paragraph is the doc's own).
      const lead = !sectionMode && leadFigures[rel];
      if (lead) {
        const first = kids.findIndex((n) => n.type === 'paragraph');
        kids.splice(first === -1 ? 0 : first, 0, { type: 'html', value: lead() });
        changed = true;
      }

      // 8. A heading that names its folder: the name stays the heading, the path goes on a line under it.
      // The ids come from one slugger over the page's headings in order, as Astro assigns them, so a
      // heading keeps the id it had when it held the path (a repeated text its -n suffix included). This
      // runs before the conditionals resolve, while a heading inside a block is still a top-level node.
      const ids = headingIds(kids, ctx);
      for (let i = kids.length - 1; i >= 0; i--) {
        const n = kids[i];
        if (n.type !== 'heading') continue;
        const split = whereItLives(n, ids.get(n), depthOf(n));
        if (!split) continue;
        kids.splice(i, 1, ...split);
        changed = true;
      }

      // 3. Template conditionals, at any depth (all of today's sit at the top level).
      const resolved = resolveConditionals(kids, ctx, rel);
      if (resolved !== kids) {
        kids = resolved;
        changed = true;
      }

      if (changed) ctx.setProperty(root, 'children', kids);
    },
    link(node, ctx) {
      const url = rewriteUrl(node.url, rel, sectionMode);
      if (url !== node.url) ctx.setProperty(node, 'url', url);
    },
    definition(node, ctx) {
      const url = rewriteUrl(node.url, rel, sectionMode);
      if (url !== node.url) ctx.setProperty(node, 'url', url);
    },
    code(node) {
      if (node.lang === 'mermaid') {
        // The source stays in the page (readable without JavaScript); the client script draws it.
        return {
          type: 'html',
          value: `<div class="trellis-mermaid not-content" data-pagefind-ignore><pre class="mermaid">${escapeHtml(node.value)}</pre></div>`,
        };
      }
      if (PLAIN_FENCE.has(node.lang ?? '') && BOX_DRAWING.test(node.value)) {
        // An ASCII diagram or a file tree, not code to copy: its widest line, in characters, sizes its type
        // (lattice.css) so all of it fits the column, down to a floor below which it scrolls sideways. The
        // highlighter would set it at the prose line height, where a run of │ breaks into dashes.
        const columns = Math.max(...node.value.split('\n').map((line) => [...line].length));
        return {
          type: 'html',
          value: `<div class="trellis-ascii not-content" style="--columns: ${columns}"><pre>${escapeHtml(node.value)}</pre></div>`,
        };
      }
    },
  };
}

function sitePagePlugin() {
  const withBase = (url) => (typeof url === 'string' && url.startsWith('/') && !url.startsWith('//') && !url.startsWith(`${base}/`) ? `${base}${url}` : url);
  const jsx = (node, ctx) => {
    const attrs = node.attributes ?? [];
    let changed = false;
    const next = attrs.map((a) => {
      if (a.type === 'mdxJsxAttribute' && a.name === 'href' && typeof a.value === 'string' && withBase(a.value) !== a.value) {
        changed = true;
        return { ...a, value: withBase(a.value) };
      }
      return a;
    });
    // Sätteri does not let a plugin set `attributes` on a JSX node, so the node is rebuilt around its children.
    if (changed) ctx.replaceNode(node, { type: node.type, name: node.name, attributes: next, children: [...(node.children ?? [])] });
  };
  return {
    name: 'trellis-site-pages',
    before(root, ctx) {
      const at = root.children.findIndex((n) => n.type === 'mdxFlowExpression' && n.value.trim() === '/* trellis:option-sections */');
      if (at === -1) return;
      ctx.setProperty(root, 'children', [...root.children.slice(0, at), ...optionSections(), ...root.children.slice(at + 1)]);
    },
    link(node, ctx) {
      const url = withBase(node.url);
      if (url !== node.url) ctx.setProperty(node, 'url', url);
    },
    mdxJsxFlowElement: jsx,
    mdxJsxTextElement: jsx,
  };
}

// ---- tables ---------------------------------------------------------------------------------------

// A plain table in the content column of a 1280px window (632px; the column grows to 45rem on wider
// screens), measured in characters of its text: 14px Geist averages 6.2px a character in these tables,
// code (Geist Mono, 7.7px) a quarter more.
const COLUMN = 102; // characters across the column
const PADDING = 4.6; // a cell's side padding (1.8rem) in characters
const CODE = 1.25; // a character of code, in characters of text
const LINE = 25; // what a line of prose needs to read
const WRAPS = 40; // a column whose cells average this many characters wraps
const FACT = 30; // a column whose cells are all this short is a fact (lattice.css, trellis-fact)
const SEAMS = /(?<=\/)|(?<=\.)(?=[A-Za-z])/; // where code breaks first: after a slash, after a dot before a letter

const longest = (parts) => Math.max(0, ...parts.map((p) => p.length));
const EMPTY_CELL = { text: '', prose: '', codes: [] };

/**
 * How a docs table is laid out, from its cells ({ text, prose, codes }: a cell's whole text, its text
 * outside code and its code tokens):
 *   'stack'        a table of four columns or more that does not fit the column as a table (the
 *                  test-project map, the two exchange-rates tables, the metrics): each row a block at
 *                  every width, its first cell the title and every other cell under its column's name.
 *                  What its columns need to read adds up to more than the column holds — the first
 *                  column's longest code, which a plain table keeps on one line; elsewhere a code token's
 *                  longest run between its seams; the longest word; and a line of 25 characters for a
 *                  column of prose — so side by side, some column would be a few characters wide, or
 *                  break its tokens mid-word.
 *   'stack-narrow' a wide table (three columns or more, rows of 60 characters or more on average): the
 *                  same blocks below 50rem, a table from there up. On a phone the columns after the first
 *                  two would otherwise sit off-screen behind a sideways scroll.
 *   null           a table at every width: two columns, or short rows, fit a phone.
 */
export function tableLayout(header, rows) {
  if (header.length < 3 || rows.length === 0) return null;
  const columns = header.map((_, i) => rows.map((row) => row[i] ?? EMPTY_CELL));
  const average = columns.map((cells) => cells.reduce((sum, c) => sum + c.text.length, 0) / rows.length);
  if (header.length >= 4) {
    const needs = columns.reduce((sum, cells, i) => sum + Math.max(floor(cells, i === 0), i > 0 && average[i] >= WRAPS ? LINE : 0), 0);
    if (needs > COLUMN - PADDING * header.length) return 'stack';
  }
  return average.reduce((sum, a) => sum + a, 0) >= 60 ? 'stack-narrow' : null;
}

/** The narrowest a column reads at in a plain table, in characters of text. */
function floor(cells, first) {
  let width = 0;
  for (const { prose, codes } of cells) {
    width = Math.max(width, longest(prose.split(/\s+/)));
    for (const code of codes) {
      const run = first ? code.length : longest(code.split(/\s+/).flatMap((w) => w.split(SEAMS)));
      width = Math.max(width, CODE * run);
    }
  }
  return width;
}

function tablesPlugin() {
  const elements = (node) => (node.children ?? []).filter((c) => c.type === 'element');
  return {
    name: 'trellis-tables',
    element: {
      filter: ['table'],
      visit(table, ctx) {
        const groups = elements(table).filter((g) => g.tagName === 'thead' || g.tagName === 'tbody');
        const rows = groups.flatMap((g) => elements(g).filter((r) => r.tagName === 'tr'));
        const head = rows.find((r) => elements(r).some((c) => c.tagName === 'th'));
        if (!head) return;
        const header = elements(head).map((th) => ctx.textContent(th).trim());
        const body = rows.filter((r) => r !== head).map((r) => elements(r).filter((c) => c.tagName === 'td'));
        const cells = body.map((tds) => tds.map((td) => cellOf(td, ctx)));
        const layout = tableLayout(header, cells);
        // Code in a cell breaks after a slash or a dot first (`/api/v1/<wbr>invoices`), so a narrow column
        // wraps a path or a type name at its seams rather than mid-word. The first column holds identifiers:
        // a plain table keeps them on one line (lattice.css); as the title of a block they wrap at their
        // seams like the rest.
        for (const tds of body) tds.forEach((td, i) => (i > 0 || layout === 'stack') && breakCodeIn(td, ctx));
        if (!layout) return;
        ctx.setProperty(table, 'className', [...(table.properties?.className ?? []), `trellis-${layout}`]);
        if (layout === 'stack') ctx.setProperty(table, 'style', `--fields: ${header.length - 1}`); // the rows of a block's grid
        // A table laid out as blocks loses its table semantics in some browsers: the roles keep them.
        ctx.setProperty(table, 'role', 'table');
        for (const g of groups) ctx.setProperty(g, 'role', 'rowgroup');
        for (const r of rows) ctx.setProperty(r, 'role', 'row');
        for (const th of elements(head)) ctx.setProperty(th, 'role', 'columnheader');
        // A column whose cells are all short (a status, a count, a name) is a fact: in a block it sits beside
        // the next fact rather than under it.
        const facts = header.map((_, i) => i > 0 && cells.every((row) => (row[i] ?? EMPTY_CELL).text.length <= FACT));
        for (const tds of body) {
          tds.forEach((td, i) => {
            ctx.setProperty(td, 'role', 'cell');
            if (header[i]) ctx.setProperty(td, 'dataTh', header[i]);
            if (facts[i]) ctx.setProperty(td, 'className', [...(td.properties?.className ?? []), 'trellis-fact']);
          });
        }
      },
    },
  };
}

/** A cell as tableLayout reads it: its text, the text outside code, and its code tokens. */
function cellOf(td, ctx) {
  const codes = [];
  let prose = '';
  const visit = (node) => {
    for (const child of node.children ?? []) {
      if (child.type === 'text') prose += child.value;
      else if (child.type === 'element' && child.tagName === 'code') codes.push(ctx.textContent(child));
      else visit(child);
    }
  };
  visit(td);
  return { text: ctx.textContent(td).trim(), prose, codes };
}

/** A <wbr> at each seam of the code under a node. */
function breakCodeIn(node, ctx) {
  for (const child of node.children ?? []) {
    if (child.type !== 'element') continue;
    if (child.tagName !== 'code') {
      breakCodeIn(child, ctx);
      continue;
    }
    if (!(child.children ?? []).some((c) => c.type === 'text' && /[/.]/.test(c.value))) continue;
    const wbr = () => ({ type: 'element', tagName: 'wbr', properties: {}, children: [] });
    ctx.setProperty(
      child,
      'children',
      child.children.flatMap((c) => (c.type === 'text' ? c.value.split(SEAMS).flatMap((part, i) => (i ? [wbr(), { type: 'text', value: part }] : [{ type: 'text', value: part }])) : [c])),
    );
  }
}

// ---- where it lives -------------------------------------------------------------------------------

/**
 * A heading of the shape "Name (`src/Core/…/Abstractions/`)" — a text name, then in parentheses one code
 * span with a slash in it, and nothing else — split in two: the heading with the name alone, its id set
 * to the slug of the whole text (GitHub's id for it, and the one every link to it targets; Astro keeps an
 * id a heading already has), and under it a line with the path, a link to the folder or file on GitHub at
 * the ref the site is built from when the repository has it. Any other heading is left as it is: a code
 * span mid-sentence ("Dependency flow (enforced by `Trellis.ArchitectureTests`)"), a parenthesis that
 * says more than the path ("Layer rules (`.claude/rules/`, loaded when …)"), or a code span with no
 * slash, which names a type rather than a place. Returns the two nodes, or null.
 */
function whereItLives(heading, id, depth) {
  if (heading.children.length !== 3) return null;
  const [open, code, close] = heading.children;
  if (open.type !== 'text' || code.type !== 'inlineCode' || close.type !== 'text') return null;
  const name = open.value.match(/^(.*\S)\s*\($/)?.[1];
  if (!name || !code.value.includes('/') || close.value !== ')') return null;
  const target = code.value;
  const inRepo = existsSync(path.join(repoRoot, target));
  const url = inRepo ? (target.endsWith('/') ? githubTree(target) : githubBlob(target)) : null;
  const pathNode = { type: 'inlineCode', value: target };
  return [
    { type: 'heading', depth, data: { hProperties: { id } }, children: [{ type: 'text', value: name }] },
    { type: 'paragraph', data: { hProperties: { className: ['trellis-where'] } }, children: [url ? { type: 'link', url, children: [pathNode] } : pathNode] },
  ];
}

/** The id Astro gives each heading under `nodes`: one GitHub slugger over them in document order. */
function headingIds(nodes, ctx) {
  const slugger = new GithubSlugger();
  const ids = new Map();
  const walk = (list) => {
    for (const n of list) {
      if (n.type === 'heading') ids.set(n, slugger.slug(ctx.textContent(n)));
      else if (Array.isArray(n.children) && n.type !== 'paragraph') walk(n.children);
    }
  };
  walk(nodes);
  return ids;
}

/** The path under a heading breaks at its seams, as code in a table cell does. */
function whereLinesPlugin() {
  return {
    name: 'trellis-where-lines',
    element: {
      filter: ['p'],
      visit(p, ctx) {
        if ((p.properties?.className ?? []).includes('trellis-where')) breakCodeIn(p, ctx);
      },
    },
  };
}

// ---- lead pictures --------------------------------------------------------------------------------

// docs/<file> -> the HTML of the picture its page opens with.
const leadFigures = { 'architecture.md': ringsFigure };

/**
 * The rings mark from eng/readme/ (render-diagrams.py draws it), both themes, as data URIs read at build
 * time: Starlight's data-theme shows one and hides the other (lattice.css), and each SVG keeps its own
 * ids, which two inline copies on one page would not.
 */
function ringsFigure() {
  const svg = (theme) => readFileSync(path.join(repoRoot, `eng/readme/rings-${theme}.svg`), 'utf8');
  const [light, dark] = [svg('light'), svg('dark')];
  const alt = escapeHtml(light.match(/<desc[^>]*>([^<]*)<\/desc>/)?.[1] ?? 'The rings of the solution').replace(/"/g, '&quot;');
  const img = (theme, source) =>
    `<img class="rings-${theme}" src="data:image/svg+xml;base64,${Buffer.from(source).toString('base64')}" width="240" height="240" alt="${alt}">`;
  return [
    '<figure class="trellis-rings not-content">',
    img('light', light),
    img('dark', dark),
    '<figcaption>Dependencies point inward. Infrastructure and Persistence are peers that never reference each other.</figcaption>',
    '</figure>',
  ].join('\n');
}

// ---- conditionals ---------------------------------------------------------------------------------

function resolveConditionals(children, ctx, rel) {
  if (!children.some((n) => n.type === 'html' && parseDirective(n.value))) {
    // Look one level down (lists, quotes) without rebuilding anything that has no directive.
    for (const n of children) {
      if (Array.isArray(n.children) && n.type !== 'paragraph' && n.type !== 'heading') {
        const copy = [...n.children];
        const inner = resolveConditionals(copy, ctx, rel);
        if (inner !== copy) ctx.setProperty(n, 'children', inner);
      }
    }
    return children;
  }
  const out = [];
  let joinable = false; // the next node follows a directive this pass removed
  const add = (n) => {
    const prev = out.at(-1);
    if (joinable && prev?.type === 'list' && n.type === 'list' && prev.ordered === n.ordered) {
      // A directive between two bullets ends the Markdown list (it sits in column 0), so a conditional
      // bullet is a list of its own, with a gap above it: ADR-0014's TemplateSource "Amended" line under
      // Status and Date. Where the block resolves (a constant, or the defaults), the lists are one
      // again, as the engine writes them.
      out[out.length - 1] = { type: 'list', ordered: prev.ordered, start: prev.start, spread: prev.spread || n.spread, children: [...prev.children, ...n.children] };
    } else {
      out.push(n);
    }
    joinable = false;
  };
  for (let i = 0; i < children.length; i++) {
    const n = children[i];
    const d = n.type === 'html' ? parseDirective(n.value) : null;
    if (!d) {
      add(n);
      continue;
    }
    if (d.kind !== 'if') {
      warn(`${rel}: <!--#${d.kind} --> without a matching #if`);
      continue;
    }
    // Collect the branches up to the matching #endif (nesting aware).
    const branches = [{ expr: d.expr, source: d.source, nodes: [] }];
    let depth = 0;
    let j = i + 1;
    for (; j < children.length; j++) {
      const m = children[j];
      const dj = m.type === 'html' ? parseDirective(m.value) : null;
      if (dj?.kind === 'if') depth++;
      if (dj && depth === 0 && dj.kind !== 'if') {
        if (dj.kind === 'endif') break;
        branches.push({ expr: dj.kind === 'else' ? null : dj.expr, source: dj.source, nodes: [] });
        continue;
      }
      if (dj?.kind === 'endif') depth--;
      branches.at(-1).nodes.push(m);
    }
    if (j >= children.length) warn(`${rel}: #if (${d.source}) has no #endif`);
    i = j;
    joinable = true;
    for (const r of renderBranches(branches, ctx, rel)) add(r);
    joinable = true;
  }
  return out;
}

function renderBranches(branches, ctx, rel) {
  const out = [];
  let previous = null; // the conditions already taken, for #elseif / #else
  for (const b of branches) {
    let cond = b.expr ?? { type: 'literal', value: true };
    if (previous) cond = { type: 'and', left: { type: 'not', arg: previous }, right: cond };
    previous = previous ? { type: 'or', left: previous, right: b.expr ?? { type: 'literal', value: true } } : b.expr;
    for (const id of identifiers(cond)) {
      if (!(id in optionsBySymbol) && !(id in constantSymbols)) warn(`${rel}: unknown template symbol "${id}"`);
    }
    const inner = resolveConditionals(b.nodes, ctx, rel); // nested blocks
    const folded = fold(cond, constantSymbols);
    if (conditionalsMode === 'defaults' || folded.type === 'literal') {
      if (evaluate(folded, { ...defaults, ...constantSymbols })) out.push(...inner);
      continue;
    }
    if (inner.length === 0) continue;
    out.push({
      type: 'containerDirective',
      name: 'note',
      attributes: { icon: 'puzzle' },
      children: [{ type: 'paragraph', data: { directiveLabel: true }, children: describe(folded, optionsBySymbol, b.source) }, ...inner],
    });
  }
  return out;
}

/** The directive HTML nodes anywhere in a tree. */
function countDirectives(node) {
  let n = node.type === 'html' && parseDirective(node.value) ? 1 : 0;
  for (const child of node.children ?? []) n += countDirectives(child);
  return n;
}

// ---- links ----------------------------------------------------------------------------------------

function rewriteUrl(url, fromRel, sectionMode) {
  if (!url || /^[a-z][a-z0-9+.-]*:/i.test(url) || url.startsWith('//')) return url; // http:, mailto:, …
  const hashAt = url.indexOf('#');
  const pathPart = hashAt === -1 ? url : url.slice(0, hashAt);
  const hash = hashAt === -1 ? '' : url.slice(hashAt + 1);

  if (pathPart === '') {
    // A same-page anchor. It may point across a section split.
    if (sectionMode && !sectionMode.anchors.has(hash)) return `${base}/${routeForDoc(sectionMode.from)}/#${hash}`;
    if (!sectionMode) {
      const sp = movedSections.find((s) => s.from === fromRel && s.anchors.has(hash) && s.anchor !== hash);
      if (sp) return `${base}/${sp.route}/#${hash}`;
    }
    return url;
  }
  if (pathPart.startsWith('/')) return url;

  const inDocs = posix.normalize(posix.join(posix.dirname(fromRel), pathPart));
  const isDir = pathPart.endsWith('/');
  const withHash = (u) => (hash ? `${u}#${hash}` : u);
  if (inDocs.startsWith('../') || inDocs === '..') {
    // Leaves docs/: the file is in the repository, not on the site.
    const repoPath = posix.normalize(posix.join('docs', inDocs));
    return withHash(isDir ? githubTree(repoPath) : githubBlob(repoPath));
  }
  if (inDocs.endsWith('.md')) {
    if (!safeExists(path.join(docsDir, inDocs))) warn(`${fromRel}: link to a missing doc ${pathPart}`);
    const sp = movedSections.find((s) => s.from === inDocs && hash && s.anchors.has(hash));
    if (sp) return `${base}/${sp.route}/${hash === sp.anchor ? '' : `#${hash}`}`;
    return withHash(`${base}/${routeForDoc(inDocs)}/`);
  }
  // Anything else inside docs/ (the OpenAPI JSON, the adr/ folder) is browsed on GitHub.
  return withHash(isDir ? githubTree(posix.join('docs', inDocs)) : githubBlob(posix.join('docs', inDocs)));
}

function pointerTo(sp) {
  return {
    type: 'paragraph',
    children: [
      { type: 'text', value: 'This section has a page of its own on the site: ' },
      { type: 'link', url: `${base}/${sp.route}/`, children: [{ type: 'strong', children: [{ type: 'text', value: sp.title }] }] },
      { type: 'text', value: `. ${sp.description}` },
    ],
  };
}

// ---- helpers --------------------------------------------------------------------------------------

const slugOf = (text) => new GithubSlugger().slug(text);
const BOX_DRAWING = /[─-╿]/; // ─ │ ┌ ┐ └ ┘ ├ ┤ ┬ ┴ ┼ and the rest of the block
const PLAIN_FENCE = new Set(['', 'text', 'txt']); // a fence that asks for no highlighting

function safeExists(p) {
  try {
    readFileSync(p);
    return true;
  } catch {
    return false;
  }
}

function escapeHtml(s) {
  return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}
