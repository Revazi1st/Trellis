// Stands in for elkjs, the Eclipse Layout Kernel that mermaid 12 uses for its "elk" layouts (its
// default, which MarkdownContent.astro sets back to dagre). elkjs is EPL-2.0, and everything the site
// serves to a reader is MIT, ISC, BSD, Apache-2.0 or OFL, so the build aliases it to this module
// (astro.config.mjs) and fails if ELK code reaches dist/ anyway. A diagram that asked for an ELK layout
// in its own configuration would show this error instead of a drawing.
export default class ELK {
  constructor() {
    throw new Error('The ELK layout engine is not part of this site. Use the default (dagre) layout.');
  }
}
