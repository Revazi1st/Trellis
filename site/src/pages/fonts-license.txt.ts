// The site serves Geist and Geist Mono itself (the @fontsource-variable packages), so it serves their
// licence too: the SIL Open Font License 1.1 with each family's copyright line, at /Trellis/fonts-license.txt,
// which the landing page links. Read from the installed packages at build time, never copied here.
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { siteDir } from '../../trellis.config.mjs';

const resolve = createRequire(path.join(siteDir, 'package.json')).resolve;
const licence = (pkg: string) => readFileSync(resolve(`${pkg}/LICENSE`), 'utf8').trim();

export function GET() {
  const body = [
    'The Trellis site uses the Geist and Geist Mono typefaces, served from this site.',
    'Both are licensed under the SIL Open Font License, Version 1.1, reproduced below for each family.',
    '',
    '=== Geist ===',
    '',
    licence('@fontsource-variable/geist'),
    '',
    '=== Geist Mono ===',
    '',
    licence('@fontsource-variable/geist-mono'),
    '',
  ].join('\n');
  return new Response(body, { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
}
