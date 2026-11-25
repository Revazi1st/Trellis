// The Options page's sections, one per boolean option, generated as Markdown (mdast) at build time so
// their headings join the page's table of contents and their links go through the links validator.
// Everything but `after` and `docs` (trellis.config.mjs, optionNotes) is read from .template.config/:
//   - the default, the description and the flag (template.json, dotnetcli.host.json)
//   - the option's label in the Visual Studio and Rider dialogs (ide.host.json, the Visual Studio host file)
//   - the files only the option brings (template.json's conditional excludes)
//   - the files the option replaces (a conditional source laid over a folder a modifier excludes)
//   - the reminder `dotnet new` prints after creating a solution with it (template.json's post-actions)

import { readFileSync } from 'node:fs';
import path from 'node:path';
import GithubSlugger from 'github-slugger';
import { docHref, optionNotes, readTemplateOptions, repoRoot, sectionPages } from '../../trellis.config.mjs';
import { describe, identifiers, parseExpression } from './conditionals.mjs';
import { title as docTitle } from './doc-text.mjs';

const json = (p) => JSON.parse(readFileSync(path.join(repoRoot, p), 'utf8'));

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
  // A source after the first that is laid over a folder of the solution under a condition, and a modifier
  // of the first source that keeps that folder out under the same condition: together a replacement (the
  // multi-tenant initial migration), not files the option adds or files it takes away.
  const overlays = (tpl.sources ?? []).slice(1).filter((s) => s.condition && s.target && s.target !== './');
  const modifiers = (tpl.sources?.[0]?.modifiers ?? []).map((m) => {
    const excludedWhen = parseExpression(m.condition.replace(/^\((.*)\)$/, '$1'));
    const overlay = overlays.find((s) => s.condition === m.condition && (m.exclude ?? []).includes(`${s.target.replace(/^\.\//, '')}**`));
    return { excludedWhen, presentWhen: negate(excludedWhen), paths: m.exclude ?? [], overlay };
  });
  for (const s of overlays) {
    if (!modifiers.some((m) => m.overlay === s)) throw new Error(`template.json: the source ${s.source} is laid over ${s.target} and no modifier under ${s.condition} excludes that folder; both sets would ship`);
  }
  // A replacement is shown under the one option whose condition it is; any other condition would vanish
  // from the page without a word.
  for (const m of modifiers.filter((m) => m.overlay)) {
    if (m.excludedWhen.type !== 'var' || !bySymbol[m.excludedWhen.name]) throw new Error(`template.json: the source ${m.overlay.source} is laid over ${m.overlay.target} under ${m.overlay.condition}, which is not one option; teach site/src/lib/options.mjs to show it`);
  }
  const reminders = (tpl.postActions ?? [])
    .filter((a) => a.condition && a.actionId?.toUpperCase() === 'AC1156F7-BB77-4DB8-B28F-24EEBCCA1E5C')
    .map((a) => ({ when: parseExpression(a.condition.replace(/^\((.*)\)$/, '$1')), text: a.manualInstructions?.map((i) => i.text).join(' ') }));

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
    const own = modifiers.filter((m) => !m.overlay && identifiers(m.presentWhen).has(o.symbol));
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
    // The folders it replaces: a modifier with an overlay, excluded when this option alone is on.
    for (const m of modifiers.filter((m) => m.overlay && m.excludedWhen.type === 'var' && m.excludedWhen.name === o.symbol)) {
      const folder = code(m.overlay.target.replace(/^\.\//, ''));
      items.push(item([strong('Files it replaces: '), folder, text(`: ${notes.replaces ?? 'the set the template keeps for this option'}.`)]));
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
