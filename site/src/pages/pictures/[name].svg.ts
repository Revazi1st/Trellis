// The README's wide pictures at full size (docPictures in trellis.config.mjs): eng/readme/<name>-light.svg
// and -dark.svg, read at build time and written as files of the site, which the figures on Architecture and
// Testing (markdown-plugins.mjs) link to. A page links one instead of inlining it: the architecture pair
// alone is over 130 kB.
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { docPictures, repoRoot } from '../../../trellis.config.mjs';

export function getStaticPaths() {
  return docPictures.flatMap(({ name }) => ['light', 'dark'].map((theme) => ({ params: { name: `${name}-${theme}` } })));
}

export function GET({ params }: { params: { name: string } }) {
  const svg = readFileSync(path.join(repoRoot, 'eng/readme', `${params.name}.svg`), 'utf8');
  return new Response(svg, { headers: { 'Content-Type': 'image/svg+xml' } });
}
