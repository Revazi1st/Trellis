// Line-level reading of a docs/ file, fence-aware: the H1 (the page title), a description, the
// headings and their GitHub slugs, and one section's text. The page body itself is transformed on the
// Markdown AST (markdown-plugins.mjs); this module only answers questions the loader asks before rendering.

import GithubSlugger from 'github-slugger';

const FENCE = /^ {0,3}(`{3,}|~{3,})/;

/** { line, index, inFence } for every line, tracking ``` and ~~~ fences. */
export const lines = (text) => [...scan(text)];

/** Yields { line, index, inFence } for every line, tracking ``` and ~~~ fences. */
function* scan(text) {
  const lines = text.split(/\r?\n/);
  let fence = null;
  for (let index = 0; index < lines.length; index++) {
    const line = lines[index];
    const m = line.match(FENCE);
    if (m) {
      if (!fence) fence = m[1];
      else if (m[1][0] === fence[0] && m[1].length >= fence.length && line.trim() === m[1]) {
        yield { line, index, inFence: true };
        fence = null;
        continue;
      }
    }
    yield { line, index, inFence: Boolean(fence) };
  }
}

/** Markdown inline syntax to plain text (good enough for titles and descriptions). */
export function plain(md) {
  return md
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/\*\*([^*]+)\*\*/g, '$1')
    .replace(/(^|[^\w])[*_]([^*_]+)[*_](?=[^\w]|$)/g, '$1$2')
    .replace(/<[^>]+>/g, '')
    .replace(/\\([\\`*_{}[\]()#+\-.!])/g, '$1')
    .replace(/\s+/g, ' ')
    .trim();
}

export function headings(text) {
  const slugger = new GithubSlugger();
  const out = [];
  for (const { line, index, inFence } of scan(text)) {
    if (inFence) continue;
    const m = line.match(/^ {0,3}(#{1,6})\s+(.*?)\s*#*\s*$/);
    if (m) out.push({ depth: m[1].length, text: plain(m[2]), slug: slugger.slug(plain(m[2])), index });
  }
  return out;
}

export function title(text) {
  const h1 = headings(text).find((h) => h.depth === 1);
  return h1?.text;
}

/** The first plain paragraph after the title, trimmed to ~160 characters, for <meta name="description">. */
export function description(text, max = 160) {
  const para = [];
  let seenTitle = false;
  for (const { line, inFence } of scan(text)) {
    if (inFence) {
      if (para.length) break;
      continue;
    }
    if (!seenTitle) {
      if (/^#\s/.test(line)) seenTitle = true;
      continue;
    }
    const t = line.trim();
    const isBlock = /^(#{1,6}\s|[-*+]\s|\d+[.)]\s|\||>|<!--|<\/?[a-z]|```|~~~)/i.test(t);
    if (!t || isBlock) {
      if (para.length) break;
      continue;
    }
    para.push(t);
  }
  const s = plain(para.join(' '));
  if (s.length <= max) return s || undefined;
  return s.slice(0, s.lastIndexOf(' ', max - 1)).replace(/[,;:]$/, '') + '…';
}

/**
 * The text of the section that starts at the heading whose slug is `anchor`, up to the next heading
 * of the same or a higher level, plus the slugs of every heading inside it (the anchors that move).
 */
export function section(text, anchor) {
  const hs = headings(text);
  const start = hs.find((h) => h.slug === anchor);
  if (!start) throw new Error(`No heading with the slug "${anchor}"`);
  const next = hs.find((h) => h.index > start.index && h.depth <= start.depth);
  const lines = text.split(/\r?\n/);
  const body = lines.slice(start.index, next ? next.index : lines.length).join('\n');
  const anchors = hs.filter((h) => h.index >= start.index && (!next || h.index < next.index)).map((h) => h.slug);
  return { body, anchors, depth: start.depth };
}
