import { defineCollection } from 'astro:content';
import { z } from 'astro/zod';
import { docsSchema } from '@astrojs/starlight/schema';
import { trellisDocsLoader } from './lib/docs-loader.mjs';

// One collection: the site-only pages under src/content/docs and the repository's docs/, read in place.
// `stage` carries the landing hero's extra lines (the eyebrow, the fine print under the terminal).
export const collections = {
  docs: defineCollection({
    loader: trellisDocsLoader(),
    schema: docsSchema({
      extend: z.object({
        stage: z.object({ eyebrow: z.string(), fine: z.string().optional() }).optional(),
      }),
    }),
  }),
};
