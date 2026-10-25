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
//   5. ```mermaid fences -> <pre class="mermaid">, rendered in the browser; a fence without a language
//      that draws with box characters (an ASCII diagram) -> a <pre> whose type fits the column
//   6. the page's route as frontmatter `slug`, so the links validator files its headings under the page
//      the reader sees (it would otherwise derive the page from the path under src/content/docs)
//   7. a lead picture above the first paragraph of a page that has one (leadFigures): the rings on
//      Architecture, which the doc cannot embed itself (they live in eng/readme/, which never ships)
//   8. code in table cells gets break opportunities after / and . (narrow columns wrap at the seams)
// For site pages under src/content/docs (.mdx):
//   root-relative links and JSX href props get the base (/Trellis), so pages are written base-free, and
//   a {/* trellis:option-sections */} comment becomes one section per template option (options.mjs).
// And one hast plugin, for files under docs/: a table whose rows read better as blocks (tableLayout) gets
// a class, each cell its column's name and the table ARIA roles; lattice.css lays it out.

import { readFileSync } from 'node:fs';
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
      return isInside(fileURLToPath(ctx.fileURL), docsDir) ? tablesPlugin() : null;
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
      if (sectionMode) {
        const first = kids.findIndex((n) => n.type === 'heading');
        if (first !== -1) kids.splice(first, 1);
        for (const n of kids) {
          if (n.type === 'heading' && n.depth > sectionMode.depth) ctx.setProperty(n, 'depth', n.depth - 1);
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

      // 7. The lead picture, above the first paragraph (the H1 is gone by now).
      const lead = !sectionMode && leadFigures[rel];
      if (lead) {
        const first = kids.findIndex((n) => n.type === 'paragraph');
        kids.splice(first === -1 ? 0 : first, 0, { type: 'html', value: lead() });
        changed = true;
      }

      // 3. Template conditionals, at any depth (all of today's sit at the top level).
      const resolved = resolveConditionals(kids, ctx, rel);
      if (resolved !== kids) {
        kids = resolved;
        changed = true;
      }

      // 8. Code in table cells breaks after a slash or a dot first (`/api/v1/<wbr>invoices`), so a narrow
      //    column wraps a path or a type name at its seams rather than mid-word. The first column holds
      //    identifiers and stays whole (lattice.css).
      for (const table of kids.filter((n) => n.type === 'table')) {
        for (const row of table.children) row.children.forEach((cell, i) => i > 0 && breakCodeIn(cell, ctx));
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
      if (!node.lang && BOX_DRAWING.test(node.value)) {
        // An ASCII diagram, not code to copy: its widest line, in characters, sizes its type (lattice.css)
        // so all of it fits the column, down to a floor below which it scrolls sideways.
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

/**
 * How a docs table is laid out, from the length of its cells' text:
 *   'stack'        prose in several columns (four columns or more, two of them after the first averaging
 *                  90 characters or more): each row a block at every width, its first cell the title and
 *                  every other cell under its column's name. Side by side in the 45rem column, each of
 *                  those columns would be a few words wide (the Testing table).
 *   'stack-narrow' a wide table (three columns or more, rows of 60 characters or more on average): the
 *                  same blocks below 50rem, a table from there up. On a phone the columns after the first
 *                  two would otherwise sit off-screen behind a sideways scroll.
 *   null           a table at every width: two columns, or short rows, fit a phone.
 */
export function tableLayout(header, rows) {
  if (header.length < 3 || rows.length === 0) return null;
  const average = header.map((_, i) => rows.reduce((sum, row) => sum + (row[i]?.length ?? 0), 0) / rows.length);
  const prose = average.slice(1).filter((a) => a >= 90).length;
  if (header.length >= 4 && prose >= 2) return 'stack';
  return average.reduce((sum, a) => sum + a, 0) >= 60 ? 'stack-narrow' : null;
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
        const layout = tableLayout(header, body.map((cells) => cells.map((td) => ctx.textContent(td).trim())));
        if (!layout) return;
        ctx.setProperty(table, 'className', [...(table.properties?.className ?? []), `trellis-${layout}`]);
        // A table laid out as blocks loses its table semantics in some browsers: the roles keep them.
        ctx.setProperty(table, 'role', 'table');
        for (const g of groups) ctx.setProperty(g, 'role', 'rowgroup');
        for (const r of rows) ctx.setProperty(r, 'role', 'row');
        for (const th of elements(head)) ctx.setProperty(th, 'role', 'columnheader');
        for (const cells of body) {
          cells.forEach((td, i) => {
            ctx.setProperty(td, 'role', 'cell');
            if (header[i]) ctx.setProperty(td, 'dataTh', header[i]);
          });
        }
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

/** Replaces the inline code under a node by the same code with <wbr> after each / and each . before a letter. */
function breakCodeIn(node, ctx) {
  if (!Array.isArray(node.children) || !node.children.some((c) => c.type === 'inlineCode' || Array.isArray(c.children))) return;
  let changed = false;
  const next = node.children.map((c) => {
    if (c.type !== 'inlineCode' || !/[/.]/.test(c.value)) {
      breakCodeIn(c, ctx);
      return c;
    }
    changed = true;
    return { type: 'html', value: `<code>${escapeHtml(c.value).replace(/\//g, '/<wbr>').replace(/\.(?=[A-Za-z])/g, '.<wbr>')}</code>` };
  });
  if (changed) ctx.setProperty(node, 'children', next);
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
