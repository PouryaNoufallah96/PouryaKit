import { llms, loader } from 'fumadocs-core/source';
import { metaSchema, pageSchema } from 'fumadocs-core/source/schema';
import { applyMdxPreset } from 'fumadocs-mdx/config';
import { defineDocs } from 'fumadocs-mdx/macro';
import { remarkKitSnippets } from './kit-sources/snippets.ts';
import { docsRoute } from './shared';

const docs = defineDocs({
  dir: 'content/docs',
  docs: {
    schema: pageSchema,
    // Fumadocs' default plugins, and <Snippet> for a named region of the Proving Sample's or the tests' code.
    mdxOptions: applyMdxPreset({ remarkPlugins: [remarkKitSnippets] }),
    // Each page's processed Markdown, for its .md file, llms-full.txt and "Copy as Markdown".
    postprocess: { includeProcessedMarkdown: true },
  },
  meta: { schema: metaSchema },
});

export const source = loader({
  baseUrl: docsRoute,
  source: docs.toFumadocsSource(),
});

export const docsLlms = llms(source, {
  renderPage: async (page) => `# ${page.data.title} (${page.url})\n\n${await page.data.getText('processed')}`,
});
