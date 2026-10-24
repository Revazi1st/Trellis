// The template engine's conditional comments, as the docs use them:
//   <!--#if (EXPR) -->  <!--#elseif (EXPR) -->  <!--#else -->  <!--#endif -->
// EXPR is the subset of the dotnet templating C-style grammar that appears in this repository:
// identifiers, true/false, !, &&, ||, == / != against string or boolean literals, and parentheses.

import { lines } from './doc-text.mjs';

// A directive is exactly what scripts/check-template-toggles.sh allows in a shipped Markdown file: one
// comment alone on its line, the token right after "<!--" (the engine's token is "<!--#if", matched
// anywhere), nothing after the first "-->". The Markdown parser then makes it an HTML node of its own.
const DIRECTIVE = /^<!--#(if|elseif|elif|else|endif)(?![A-Za-z0-9_])((?:(?!-->)[^\n])*)-->$/;
const TOKEN = /<!--#(if|elseif|elif|else|endif)/;

/** Returns { kind, expr } for a directive comment, or null. `expr` is the AST (if/elseif only). */
export function parseDirective(html) {
  const m = html.trim().match(DIRECTIVE);
  if (!m) return null;
  const kind = m[1] === 'elif' ? 'elseif' : m[1];
  const rest = m[2].trim();
  if (kind === 'if' || kind === 'elseif') {
    if (!rest) throw new Error(`Directive without a condition: ${html.trim()}`);
    return { kind, expr: parseExpression(rest), source: rest };
  }
  return { kind };
}

/**
 * The lines of a Markdown text that hold a directive token, outside fenced code: the number of
 * directives the plugin must find as HTML nodes, or the build fails (markdown-plugins.mjs).
 */
export function directiveLines(text) {
  return lines(text).filter(({ line, inFence }) => !inFence && TOKEN.test(line));
}

export function parseExpression(src) {
  const tokens = tokenize(src);
  let i = 0;
  const peek = () => tokens[i];
  const take = (value) => {
    const t = tokens[i];
    if (value !== undefined && t?.value !== value) throw new Error(`Expected "${value}" in (${src}), found "${t?.value ?? 'end'}"`);
    i++;
    return t;
  };
  const primary = () => {
    const t = peek();
    if (!t) throw new Error(`Unexpected end of (${src})`);
    if (t.value === '(') {
      take('(');
      const e = or();
      take(')');
      return e;
    }
    if (t.value === '!') {
      take('!');
      return { type: 'not', arg: primary() };
    }
    if (t.type === 'ident') {
      take();
      if (t.value === 'true' || t.value === 'false') return { type: 'literal', value: t.value === 'true' };
      if (peek()?.value === '==' || peek()?.value === '!=') {
        const op = take().value;
        const lit = take();
        if (lit.type !== 'string' && lit.type !== 'ident') throw new Error(`Bad comparison in (${src})`);
        return { type: 'compare', op, name: t.value, value: lit.value };
      }
      return { type: 'var', name: t.value };
    }
    throw new Error(`Unexpected "${t.value}" in (${src})`);
  };
  const and = () => {
    let left = primary();
    while (peek()?.value === '&&') {
      take();
      left = { type: 'and', left, right: primary() };
    }
    return left;
  };
  const or = () => {
    let left = and();
    while (peek()?.value === '||') {
      take();
      left = { type: 'or', left, right: and() };
    }
    return left;
  };
  const ast = or();
  if (i !== tokens.length) throw new Error(`Trailing "${tokens[i].value}" in (${src})`);
  return ast;
}

function tokenize(src) {
  const tokens = [];
  const re = /\s*(?:(\|\||&&|==|!=|[()!])|([A-Za-z_][A-Za-z0-9_.]*)|"([^"]*)"|'([^']*)')/y;
  let m;
  let pos = 0;
  while (pos < src.length) {
    re.lastIndex = pos;
    m = re.exec(src);
    if (!m) {
      if (/^\s*$/.test(src.slice(pos))) break;
      throw new Error(`Cannot read the condition (${src}) at "${src.slice(pos)}"`);
    }
    if (m[1]) tokens.push({ type: 'op', value: m[1] });
    else if (m[2]) tokens.push({ type: 'ident', value: m[2] });
    else tokens.push({ type: 'string', value: m[3] ?? m[4] });
    pos = re.lastIndex;
  }
  return tokens;
}

export function identifiers(ast, out = new Set()) {
  if (!ast) return out;
  if (ast.type === 'var' || ast.type === 'compare') out.add(ast.name);
  if (ast.type === 'not') identifiers(ast.arg, out);
  if (ast.type === 'and' || ast.type === 'or') {
    identifiers(ast.left, out);
    identifiers(ast.right, out);
  }
  return out;
}

export function evaluate(ast, values) {
  switch (ast.type) {
    case 'literal':
      return ast.value;
    case 'var':
      return Boolean(values[ast.name]);
    case 'compare': {
      const equal = String(values[ast.name]).toLowerCase() === String(ast.value).toLowerCase();
      return ast.op === '==' ? equal : !equal;
    }
    case 'not':
      return !evaluate(ast.arg, values);
    case 'and':
      return evaluate(ast.left, values) && evaluate(ast.right, values);
    case 'or':
      return evaluate(ast.left, values) || evaluate(ast.right, values);
    default:
      throw new Error(`Unknown node ${ast.type}`);
  }
}

/** Replaces constant symbols by literals and folds them away. */
export function fold(ast, constants) {
  switch (ast.type) {
    case 'var':
      return ast.name in constants ? { type: 'literal', value: Boolean(constants[ast.name]) } : ast;
    case 'not': {
      const arg = fold(ast.arg, constants);
      return arg.type === 'literal' ? { type: 'literal', value: !arg.value } : { type: 'not', arg };
    }
    case 'and':
    case 'or': {
      const left = fold(ast.left, constants);
      const right = fold(ast.right, constants);
      const isAnd = ast.type === 'and';
      for (const [a, b] of [[left, right], [right, left]]) {
        if (a.type === 'literal') {
          if (isAnd) return a.value ? b : a;
          return a.value ? a : b;
        }
      }
      return { type: ast.type, left, right };
    }
    default:
      return ast;
  }
}

/**
 * The label of a toggle aside, as mdast phrasing nodes: "Only with `--use-multitenancy`",
 * "Only without `--use-multitenancy` (the default)", "Only with `--include-ui` (on by default)".
 * Falls back to the raw condition for shapes the sentence form does not cover.
 */
export function describe(ast, optionsBySymbol, rawSource) {
  const term = (node) => {
    if (node.type === 'var' && optionsBySymbol[node.name]) {
      const o = optionsBySymbol[node.name];
      return { neg: false, nodes: [code(o.flag), ...(o.defaultValue ? [text(' (on by default)')] : [])] };
    }
    if (node.type === 'not' && node.arg.type === 'var' && optionsBySymbol[node.arg.name]) {
      const o = optionsBySymbol[node.arg.name];
      return o.defaultValue
        ? { neg: false, nodes: [code(`${o.flag} false`)] }
        : { neg: true, nodes: [code(o.flag), text(' (the default)')] };
    }
    return null;
  };
  const flatten = (node, type, out = []) => {
    if (node.type === type) {
      flatten(node.left, type, out);
      flatten(node.right, type, out);
    } else out.push(node);
    return out;
  };
  const sentence = (parts, joiner) => {
    const nodes = [text('Only ')];
    parts.forEach((p, idx) => {
      if (idx > 0) nodes.push(text(` ${joiner} `));
      // "with A and B", but "with A and without B": repeat the preposition only when it changes.
      if (idx === 0 || parts[idx - 1].neg !== p.neg) nodes.push(text(p.neg ? 'without ' : 'with '));
      nodes.push(...p.nodes);
    });
    return mergeText(nodes);
  };
  for (const type of ['and', 'or']) {
    if (ast.type === type) {
      const parts = flatten(ast, type).map(term);
      if (parts.every(Boolean)) return sentence(parts, type);
    }
  }
  const single = term(ast);
  if (single) return sentence([single], 'and');
  return [text('Only when '), code(rawSource ?? '(condition)')];
}

const text = (value) => ({ type: 'text', value });
const code = (value) => ({ type: 'inlineCode', value });
function mergeText(nodes) {
  const out = [];
  for (const n of nodes) {
    const last = out.at(-1);
    if (n.type === 'text' && last?.type === 'text') last.value += n.value;
    else out.push({ ...n });
  }
  return out;
}
