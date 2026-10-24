// @ts-check
import { readdirSync, readFileSync, statSync } from 'node:fs';
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

/** Every file under a folder, recursively. */
const filesUnder = (dir) =>
  readdirSync(dir).flatMap((name) => {
    const p = path.join(dir, name);
    return statSync(p).isDirectory() ? filesUnder(p) : [p];
  });

/**
 * Registers the Trellis mdast plugins ahead of Starlight's and the links validator's, and checks the
 * output: mermaid's optional ELK layout engine (elkjs, EPL-2.0) is aliased away below, and must not reach
 * a reader by another import path.
 */
const trellisMarkdown = {
  name: 'trellis-markdown',
  hooks: {
    'astro:build:done': ({ dir }) => {
      const leaked = filesUnder(fileURLToPath(dir)).filter((f) => f.endsWith('.js') && readFileSync(f, 'utf8').includes('org.eclipse.elk'));
      if (leaked.length) {
        throw new Error(`ELK (elkjs, EPL-2.0) reached the build output in ${leaked.map((f) => path.basename(f)).join(', ')}: mermaid imports it another way now, so update the elkjs alias in astro.config.mjs.`);
      }
    },
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
      // Geist and Geist Mono come from npm and are served by the site itself: no font CDN.
      customCss: [
        '@fontsource-variable/geist',
        '@fontsource-variable/geist/wght-italic.css',
        '@fontsource-variable/geist-mono',
        './src/styles/lattice.css',
      ],
      components: {
        Footer: './src/components/Footer.astro',
        Hero: './src/components/Hero.astro',
        MarkdownContent: './src/components/MarkdownContent.astro',
      },
      routeMiddleware: './src/route-data.mjs',
      // Starlight's asides and heading anchors run on docs/ too, not only on src/content/docs.
      markdown: { processedDirs: [docsDir] },
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
    resolve: {
      alias: [
        { find: '@repo', replacement: repoRoot },
        // mermaid's optional ELK layouts (EPL-2.0) never reach the site: see src/lib/no-elk.mjs.
        { find: /^elkjs(\/.*)?$/, replacement: path.join(siteDir, 'src/lib/no-elk.mjs') },
      ],
    },
    // The docs, the icon and the rings live outside the site folder.
    server: { fs: { allow: [siteDir, repoRoot] } },
    // mermaid's diagram types are chunks of their own (the largest, its core, about 630 kB), fetched only
    // by a page that draws a diagram.
    build: { chunkSizeWarningLimit: 800 },
  },
});
