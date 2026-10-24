// Starlight route middleware: the favicon is the template's own icon, imported from the repository
// (Vite fingerprints it into _astro/), so the site keeps no copy of it in public/.
import { defineRouteMiddleware } from '@astrojs/starlight/route-data';
import favicon from '@repo/.template.config/icon.svg?url&no-inline';

export const onRequest = defineRouteMiddleware((context) => {
  const icon = context.locals.starlightRoute.head.find((entry) => entry.tag === 'link' && entry.attrs?.rel === 'shortcut icon');
  if (icon) {
    icon.attrs.href = favicon;
    icon.attrs.type = 'image/svg+xml';
  }
});
