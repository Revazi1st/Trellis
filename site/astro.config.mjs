// @ts-check
import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import starlight from '@astrojs/starlight';
import { defineConfig } from 'astro/config';
import starlightLinksValidator from 'starlight-links-validator';
import { title as docTitle } from './src/lib/doc-text.mjs';
import { sidebarLabel } from './src/lib/docs-loader.mjs';
import { trellisMdastPlugins } from './src/lib/markdown-plugins.mjs';
import { base, docsDir, github, repoRoot, routeForDoc, site } from './trellis.config.mjs';

const siteDir = path.dirname(fileURLToPath(import.meta.url));

// The Decisions group, generated from docs/adr/ in numeric order with the H1s as labels.
// (Starlight's own `autogenerate` groups by path under src/content/docs, which in-place docs are not.)
const decisions = readdirSync(path.join(docsDir, 'adr'))
  .filter((f) => /^\d{4}-.*\.md$/.test(f))
  .sort()
  .map((f) => ({
    slug: routeForDoc(`adr/${f}`),
    label: sidebarLabel(`adr/${f}`, docTitle(readFileSync(path.join(docsDir, 'adr', f), 'utf8')) ?? f),
  }));

/** Registers the Trellis mdast plugins ahead of Starlight's and the links validator's. */
const trellisMarkdown = {
  name: 'trellis-markdown',
  hooks: {
    'astro:config:setup': ({ config }) => {
      const processor = config.markdown.processor;
      if (processor?.name !== 'satteri') throw new Error('trellis-markdown expects Astro 7’s default Sätteri processor');
      processor.options.mdastPlugins.unshift(...trellisMdastPlugins({ siteContentDir: path.join(siteDir, 'src/content/docs') }));
      // GitHub parity: docs/ is written and reviewed on GitHub, which keeps -- and straight quotes as typed
      // (Astro's default turns `--help` in text into an en dash).
      processor.options.features.smartPunctuation = false;
    },
  },
};

// Mermaid, drawn in the browser from a pinned build, redrawn when the theme changes. (A build-time
// renderer would need a headless browser in the build.)
const mermaidScript = `
const nodes = [...document.querySelectorAll('.trellis-mermaid pre.mermaid')];
if (nodes.length) {
  const { default: mermaid } = await import('https://cdn.jsdelivr.net/npm/mermaid@12.2.0/dist/mermaid.esm.min.mjs');
  for (const el of nodes) el.dataset.src ??= el.textContent;
  const palette = (dark) => dark
    ? { darkMode: true, background: '#0C1410', primaryColor: '#172520', primaryBorderColor: '#8FD16A', primaryTextColor: '#E6EFE8',
        secondaryColor: '#3A2F1B', tertiaryColor: '#121D16', lineColor: '#9DB3A4', textColor: '#E6EFE8', mainBkg: '#172520',
        nodeBorder: '#8FD16A', actorBkg: '#172520', actorBorder: '#8FD16A', actorTextColor: '#E6EFE8', actorLineColor: '#566B5F',
        signalColor: '#E6EFE8', signalTextColor: '#E6EFE8', labelBoxBkgColor: '#172520', labelBoxBorderColor: '#8FD16A',
        labelTextColor: '#E6EFE8', loopTextColor: '#E6EFE8', noteBkgColor: '#2E2616', noteBorderColor: '#9E7E42',
        noteTextColor: '#F1E3C4', activationBkgColor: '#27331F', activationBorderColor: '#8FD16A', sequenceNumberColor: '#0E2415' }
    : { darkMode: false, background: '#FFFFFF', primaryColor: '#EEF6E8', primaryBorderColor: '#1F5C3D', primaryTextColor: '#14231B',
        secondaryColor: '#F3E6C9', tertiaryColor: '#FFFFFF', lineColor: '#566B5F', textColor: '#14231B', mainBkg: '#EEF6E8',
        nodeBorder: '#1F5C3D', actorBkg: '#EEF6E8', actorBorder: '#1F5C3D', actorTextColor: '#14231B', actorLineColor: '#9DB3A4',
        signalColor: '#14231B', signalTextColor: '#14231B', labelBoxBkgColor: '#EEF6E8', labelBoxBorderColor: '#1F5C3D',
        labelTextColor: '#14231B', loopTextColor: '#14231B', noteBkgColor: '#F8EFD9', noteBorderColor: '#C39A52',
        noteTextColor: '#4A3511', activationBkgColor: '#DFE9D6', activationBorderColor: '#1F5C3D', sequenceNumberColor: '#FFFFFF' };
  let drawing = Promise.resolve();
  const draw = () => (drawing = drawing.then(async () => {
    const dark = document.documentElement.dataset.theme === 'dark';
    mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: 'base', fontFamily: 'Geist, system-ui, sans-serif', themeVariables: palette(dark) });
    for (const el of nodes) { el.removeAttribute('data-processed'); el.textContent = el.dataset.src; }
    await mermaid.run({ nodes });
  }));
  draw();
  new MutationObserver(draw).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
}
`;

// Addresses on the reader's machine that docs/ links to on purpose (see the links validator below).
const localLinks = ['http://localhost:5170', 'http://localhost:5171', 'http://localhost:8025', 'http://localhost:18888'];

// https://astro.build/config
export default defineConfig({
  site,
  base,
  trailingSlash: 'ignore',
  integrations: [
    trellisMarkdown,
    starlight({
      title: 'Trellis',
      description:
        'A dotnet new template for .NET 10 that generates a rich-DDD, Clean Architecture solution whose rules the tests enforce.',
      logo: { src: '@repo/.template.config/icon.svg', alt: '' },
      social: [{ icon: 'github', label: 'GitHub', href: github.url }],
      editLink: { baseUrl: `${github.url}/edit/main/site/` },
      lastUpdated: true,
      pagination: true,
      customCss: ['./src/styles/lattice.css'],
      components: { Hero: './src/components/Hero.astro' },
      routeMiddleware: './src/route-data.mjs',
      // Starlight's asides and heading anchors run on docs/ too, not only on src/content/docs.
      markdown: { processedDirs: [docsDir] },
      head: [
        { tag: 'link', attrs: { rel: 'preconnect', href: 'https://fonts.googleapis.com' } },
        { tag: 'link', attrs: { rel: 'preconnect', href: 'https://fonts.gstatic.com', crossorigin: true } },
        {
          tag: 'link',
          attrs: {
            rel: 'stylesheet',
            href: 'https://fonts.googleapis.com/css2?family=Geist:wght@400;500;600&family=Geist+Mono:wght@400;500&display=swap',
          },
        },
        { tag: 'script', attrs: { type: 'module' }, content: mermaidScript },
      ],
      expressiveCode: {
        themes: ['github-dark-default', 'github-light-default'],
        styleOverrides: {
          borderRadius: '10px',
          borderColor: 'var(--sl-color-hairline)',
          codeFontFamily: 'var(--__sl-font-mono)',
          uiFontFamily: 'var(--__sl-font)',
          frames: { shadowColor: 'transparent' },
        },
      },
      sidebar: [
        {
          label: 'Start',
          items: [
            { label: 'Overview', link: '/' },
            { label: 'Install and create', slug: 'start/install' },
            { label: 'First run', link: '/develop/development/#running-the-app' },
            { label: 'Try it', link: '/features/invoices/#try-it' },
            { label: 'Options', link: '/start/install/#options' },
            { label: 'Make it yours', link: '/develop/upgrading/' },
          ],
        },
        { label: 'Understand', items: [{ label: 'Architecture', slug: 'understand/architecture' }] },
        {
          label: 'Features',
          items: [
            { label: 'Invoices', slug: 'features/invoices' },
            { label: 'Auth', slug: 'features/auth' },
            { label: 'Exchange rates', slug: 'features/exchange-rates' },
          ],
        },
        {
          label: 'Develop',
          items: [
            { label: 'Development', slug: 'develop/development' },
            { label: 'Testing', slug: 'develop/testing' },
            { label: 'Upgrading', slug: 'develop/upgrading' },
          ],
        },
        {
          label: 'Operate',
          items: [
            { label: 'Deployment', slug: 'operate/deployment' },
            { label: 'Threat model', slug: 'operate/threat-model' },
          ],
        },
        { label: 'Decisions', collapsed: true, items: decisions },
      ],
      plugins: [
        starlightLinksValidator({
          // The docs send the reader to software running on their own machine: the API and the web UI
          // (development.md), Mailpit (development.md) and the Aspire dashboard through an SSH tunnel
          // (deployment.md). Exactly these addresses: any other local link still fails the build.
          exclude: ({ link }) => localLinks.includes(link),
        }),
      ],
    }),
  ],
  vite: {
    resolve: { alias: { '@repo': repoRoot } },
    // The docs, the icon and the rings live outside the site folder.
    server: { fs: { allow: [siteDir, repoRoot] } },
  },
});
