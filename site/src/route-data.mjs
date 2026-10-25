// Starlight route middleware, run for every page.
//   - The favicon is the template's own icon, imported from the repository (Vite fingerprints it into
//     _astro/), so the site keeps no copy of it in public/.
//   - A preview build (any ref but a release tag) puts a banner at the top of every page, so a reader can
//     never take it for the released documentation, which it replaces on GitHub Pages until the next
//     release build.
import { defineRouteMiddleware } from '@astrojs/starlight/route-data';
import favicon from '@repo/.template.config/icon.svg?url&no-inline';
import { base } from '../trellis.config.mjs';
import { build, packageId, packagePublished, releaseNotesUrl } from './data/repo.mjs';

const escape = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

const previewBanner = build.version
  ? null
  : [
      `Preview built from <code>${escape(build.ref)}</code>${build.commit ? ` at <code>${build.commit}</code>` : ''}.`,
      packagePublished
        ? `This preview is not the released documentation; the latest release is <a href="${releaseNotesUrl(build.latestRelease)}">${build.latestRelease}</a>.`
        : `<code>${packageId}</code> is not on nuget.org yet: <a href="${base}/start/install/#install-the-template">install the template from a clone</a>.`,
    ].join(' ');

export const onRequest = defineRouteMiddleware((context) => {
  const route = context.locals.starlightRoute;
  const icon = route.head.find((entry) => entry.tag === 'link' && entry.attrs?.rel === 'shortcut icon');
  if (icon) {
    icon.attrs.href = favicon;
    icon.attrs.type = 'image/svg+xml';
  }
  if (previewBanner) route.entry.data.banner = { content: previewBanner };
});
