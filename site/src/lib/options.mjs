// The Options page's sections, one per boolean option, generated as Markdown (mdast) at build time so
// their headings join the page's table of contents and their links go through the links validator.
// Everything but `after`, `docs` and `replaces` (trellis.config.mjs, optionNotes) is read from
// .template.config/:
//   - the default, the description and the flag (template.json, dotnetcli.host.json)
//   - the option's label in the Visual Studio and Rider dialogs (ide.host.json, the Visual Studio host file)
//   - the files only the option brings (template.json's conditional excludes)
//   - the folder the option replaces (a conditional source laid over a folder that a modifier of the first
//     source excludes under the same condition); what the replacement holds is optionNotes' `replaces`
//   - the reminder `dotnet new` prints after creating a solution with it (template.json's post-actions)

import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import GithubSlugger from 'github-slugger';
import { docHref, optionNotes, readTemplateOptions, repoRoot, sectionPages } from '../../trellis.config.mjs';
import { describe, identifiers, parseExpression } from './conditionals.mjs';
import { title as docTitle } from './doc-text.mjs';

const json = (p) => JSON.parse(readFileSync(path.join(repoRoot, p), 'utf8'));
/** A template.json condition, parsed: `(X)` and `X` are the same condition. */
const condition = (c) => parseExpression(c.replace(/^\((.*)\)$/, '$1'));
const sameCondition = (a, b) => JSON.stringify(a) === JSON.stringify(b);
/** A source's target as a folder of the solution: '' for the root, else with one trailing slash. */
function folderOf(target) {
  const folder = (target ?? './').replace(/^\.(?:\/|$)/, '');
  return folder && !folder.endsWith('/') ? `${folder}/` : folder;
}

/** The anchor of an option's section on the Options page (the heading is the flag). */
export const optionAnchor = (flag) => new GithubSlugger().slug(flag);

/** `!(A || !B)` -> `!A && B`, so a "files are excluded when" condition reads as "files are present when". */
function negate(ast) {
  switch (ast.type) {
    case 'not':
      return ast.arg;
    case 'and':
      return { type: 'or', left: negate(ast.left), right: negate(ast.right) };
    case 'or':
      return { type: 'and', left: negate(ast.left), right: negate(ast.right) };
    case 'literal':
      return { type: 'literal', value: !ast.value };
    default:
      return { type: 'not', arg: ast };
  }
}

const text = (value) => ({ type: 'text', value });
const code = (value) => ({ type: 'inlineCode', value });
const strong = (value) => ({ type: 'strong', children: [text(value)] });
const paragraph = (children) => ({ type: 'paragraph', children });
const item = (children) => ({ type: 'listItem', spread: false, children: [paragraph(children)] });
const link = (url, children) => ({ type: 'link', url, children });

/** A note's inline Markdown: `code` and [text](docs-target) only. */
function inline(md) {
  const out = [];
  let last = 0;
  for (const m of md.matchAll(/`([^`]+)`|\[([^\]]+)\]\(([^)\s]+)\)/g)) {
    if (m.index > last) out.push(text(md.slice(last, m.index)));
    out.push(m[1] !== undefined ? code(m[1]) : link(docHref(m[3]), [text(m[2])]));
    last = m.index + m[0].length;
  }
  if (last < md.length) out.push(text(md.slice(last)));
  return out;
}

/** A docs/ target's page title, for the "Read more" links: the H1, or the section's own heading. */
function docLabel(target) {
  const [file, anchor] = target.split('#');
  const section = anchor && sectionPages.find((sp) => sp.from === file && sp.anchor === anchor);
  if (section) return section.title;
  const source = readFileSync(path.join(repoRoot, 'docs', file), 'utf8');
  if (anchor) {
    const heading = source.split('\n').find((l) => /^#{2,6}\s/.test(l) && optionAnchor(l.replace(/^#+\s+/, '').replace(/`/g, '')) === anchor);
    if (!heading) throw new Error(`optionNotes: docs/${file} has no heading #${anchor}`);
    return heading.replace(/^#+\s+/, '').replace(/`/g, '');
  }
  return docTitle(source) ?? file;
}

export function optionSections() {
  const tpl = json('.template.config/template.json');
  const ide = json('.template.config/ide.host.json');
  const options = readTemplateOptions();
  const bySymbol = Object.fromEntries(options.map((o) => [o.symbol, o]));
  const modifiers = (tpl.sources?.[0]?.modifiers ?? []).map((m) => {
    const excludedWhen = condition(m.condition);
    return { condition: m.condition, excludedWhen, presentWhen: negate(excludedWhen), paths: m.exclude ?? [], replaced: [] };
  });
  // A source after the first, under a condition, laid over a folder of the solution, and a modifier of the
  // first source that keeps that folder out under the same condition: together a replacement (the
  // multi-tenant initial migration), not files the option adds or files it takes away. The page shows a
  // conditional source only that way: under the one option that is its condition, one folder per option
  // (optionNotes' `replaces` describes one), with a modifier that excludes nothing else. Any other shape
  // fails the build, since it would leave the page without a word or with a false one.
  const overlays = (tpl.sources ?? []).slice(1).filter((s) => s.condition).map((s) => ({ ...s, when: condition(s.condition), folder: folderOf(s.target) }));
  for (const s of overlays) {
    const pairs = s.folder ? modifiers.filter((m) => sameCondition(m.excludedWhen, s.when) && m.paths.includes(`${s.folder}**`)) : [];
    if (pairs.length === 0) {
      const laid = `template.json: the source ${s.source} is laid over ${s.folder || "the solution's root"} under ${s.condition}`;
      if (!s.folder) throw new Error(`${laid}; the Options page shows a conditional source only as the replacement of one folder, so teach site/src/lib/options.mjs to show what it adds or overwrites`);
      if (existsSync(path.join(repoRoot, s.folder))) throw new Error(`${laid}, a folder the repository has, and no modifier of the first source excludes ${s.folder}** under that condition: unless the first source's own exclude list leaves the folder out, both would ship`);
      throw new Error(`${laid}, a folder the repository does not have: it only adds files, and the Options page shows a conditional source only as a replacement; teach site/src/lib/options.mjs to list them`);
    }
    // A replacement is shown under the one option whose condition it is; any other condition would vanish
    // from the page without a word.
    if (s.when.type !== 'var' || !bySymbol[s.when.name]) throw new Error(`template.json: the source ${s.source} replaces ${s.folder} under ${s.condition}, which is not a single option being on; teach site/src/lib/options.mjs to show it`);
    for (const m of pairs) m.replaced.push(s.folder);
  }
  for (const o of options) {
    const folders = overlays.filter((s) => s.when.name === o.symbol).map((s) => s.folder);
    if (folders.length > 1) throw new Error(`template.json: ${o.flag} replaces ${folders.length} folders (${folders.join(', ')}), and optionNotes' \`replaces\` describes one; teach site/src/lib/options.mjs and trellis.config.mjs to describe each`);
  }
  // A modifier that makes a replacement is shown only as that replacement, so anything else it excludes
  // would vanish from the page.
  for (const m of modifiers.filter((m) => m.replaced.length)) {
    const rest = m.paths.filter((p) => !m.replaced.some((folder) => p === `${folder}**`));
    if (rest.length) throw new Error(`template.json: the modifier under ${m.condition} excludes ${m.replaced.map((f) => `${f}**`).join(', ')}, which a conditional source replaces, and also ${rest.join(', ')}, which the Options page would not show; move those to a modifier of their own under the same condition (the page lists it under "Files it adds"), or teach site/src/lib/options.mjs to show them`);
  }
  const reminders = (tpl.postActions ?? [])
    .filter((a) => a.condition && a.actionId?.toUpperCase() === 'AC1156F7-BB77-4DB8-B28F-24EEBCCA1E5C')
    .map((a) => ({ when: condition(a.condition), text: a.manualInstructions?.map((i) => i.text).join(' ') }));

  const nodes = [];
  for (const o of options) {
    const notes = optionNotes[o.symbol];
    if (!notes) throw new Error(`trellis.config.mjs: optionNotes has no entry for the template option ${o.symbol} (${o.flag})`);
    const dialog = ide.symbolInfo?.find((s) => s.id === o.symbol);
    const where = dialog?.isVisible === false ? 'Hidden in the Visual Studio dialog.' : `In the Visual Studio and Rider dialogs: “${dialog?.name?.text ?? o.displayName}”.`;

    nodes.push({ type: 'heading', depth: 2, children: [code(o.flag)] });
    nodes.push(paragraph([strong(o.defaultValue ? 'On by default.' : 'Off by default.'), text(` ${where}`)]));
    nodes.push(paragraph([text(o.description)]));

    const items = [];
    // The files only this option brings: the conditional excludes whose condition names it.
    const own = modifiers.filter((m) => m.replaced.length === 0 && identifiers(m.presentWhen).has(o.symbol));
    for (const m of own) {
      const files = m.paths.map((p) => code(p.replace(/\/\*\*$/, '/')));
      const joined = files.flatMap((f, i) => (i === 0 ? [f] : [text(i === files.length - 1 ? ' and ' : ', '), f]));
      const alone = m.presentWhen.type === 'var' && m.presentWhen.name === o.symbol;
      const label = alone ? [strong('Files it adds: ')] : [strong('Files it adds, '), ...lowerFirst(describe(m.presentWhen, bySymbol)), text(': ')];
      items.push(item([...label, ...joined, text('.')]));
    }
    if (o.convenience) {
      items.push(item([strong('A command-line convenience: '), text('it changes nothing in the solution.')]));
    } else if (own.length === 0) {
      items.push(item([strong('Files it adds: '), text('none of its own. Its code sits in conditional blocks of files every solution has.')]));
    }
    // The folder it replaces: the one conditional source under this option alone.
    for (const s of overlays.filter((s) => s.when.name === o.symbol)) {
      items.push(item([strong('Files it replaces: '), code(s.folder), text(`: ${notes.replaces ?? 'the set the template keeps for this option'}.`)]));
    }
    for (const r of reminders.filter((r) => r.when.type === 'var' && r.when.name === o.symbol)) {
      items.push(item([strong('dotnet new reminds you: '), text(`“${r.text}”`)]));
    }
    items.push(item([strong('After creating: '), ...inline(notes.after)]));
    if (notes.docs.length) {
      const links = notes.docs.map((d) => link(docHref(d), [text(docLabel(d))]));
      items.push(item([strong('Read more: '), ...links.flatMap((l, i) => (i === 0 ? [l] : [text(' · '), l]))]));
    }
    nodes.push({ type: 'list', ordered: false, spread: false, children: items });
  }
  return nodes;
}

/** "Only with `--x`" -> "only with `--x`", for use mid-sentence. */
function lowerFirst(nodes) {
  const [first, ...rest] = nodes;
  if (first?.type !== 'text') return nodes;
  return [text(first.value.charAt(0).toLowerCase() + first.value.slice(1)), ...rest];
}
