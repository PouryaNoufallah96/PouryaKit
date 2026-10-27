import path from 'node:path';
import { extractCodeRegion } from 'fumadocs-core/mdx-plugins/remark-include';
import { replaceSource } from 'fumadocs-core/mdx-plugins/stringifier';
import type { Root } from 'mdast';
import { visit } from 'unist-util-visit';
import { findRoot, readSource, sourceExists } from './paths.ts';

/** The code a page may quote: the Proving Sample's and the tests'. */
export const SNIPPET_ROOTS = ['samples/ModularMonolith/', 'tests/'];

const LANGUAGES: Record<string, string> = { '.cs': 'csharp', '.json': 'json', '.proto': 'proto', '.csproj': 'xml' };

/**
 * The named region of a file of the Proving Sample or the tests, dedented: the lines between `#region <name>` and its
 * `#endregion` (or `// #region <name>` in a file with no region directive). A file outside those two roots, a missing
 * file, a missing region or a region named twice fails.
 */
export function readSnippet(root: string, file: string, region: string): string {
  const normalized = path.posix.normalize(file);
  if (!SNIPPET_ROOTS.some((prefix) => normalized.startsWith(prefix)) || normalized !== file) {
    throw new Error(`kit-sources: the snippet file ${file} is not under ${SNIPPET_ROOTS.join(' or ')}`);
  }
  if (!sourceExists(root, file)) throw new Error(`kit-sources: the snippet file ${file} does not exist`);
  const text = readSource(root, file);
  const name = region.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const starts = text.split('\n').filter((line) => new RegExp(`^\\s*(?://\\s*)?#region\\s+${name}\\s*$`).test(line));
  if (starts.length > 1)
    throw new Error(`kit-sources: the region ${region} is named ${starts.length} times in ${file}`);
  const code = starts.length === 1 ? extractCodeRegion(text, region) : undefined;
  if (code === undefined) throw new Error(`kit-sources: the region ${region} does not exist in ${file}`);
  return code;
}

// An MDX JSX element as remark-mdx parses it; @types/mdast leaves these out.
type JsxElement = {
  type: string;
  name: string | null;
  attributes: { type: string; name?: string; value?: unknown }[];
  [key: string]: unknown;
};

function attribute(node: JsxElement, name: string): string | undefined {
  const entry = node.attributes.find((item) => item.type === 'mdxJsxAttribute' && item.name === name);
  return typeof entry?.value === 'string' ? entry.value : undefined;
}

/**
 * The remark plugin behind `<Snippet file="samples/ModularMonolith/..." region="name" />` in a page: the element becomes
 * a code block titled with the file's name, in the page, its Markdown, its search record and `llms-full.txt` alike.
 */
export function remarkKitSnippets(options: { root?: string } = {}) {
  return (tree: Root, file: unknown) => {
    const root = options.root ?? findRoot();
    visit(tree, (visited) => {
      const node = visited as unknown as JsxElement;
      if ((node.type !== 'mdxJsxFlowElement' && node.type !== 'mdxJsxTextElement') || node.name !== 'Snippet') return;
      if (node.type === 'mdxJsxTextElement') throw new Error('kit-sources: put <Snippet> on a line of its own');
      const source = attribute(node, 'file');
      const region = attribute(node, 'region');
      if (!source || !region) throw new Error('kit-sources: <Snippet> needs a file and a region');
      const extension = path.extname(source);
      const lang = LANGUAGES[extension] ?? extension.slice(1);
      const meta = `title="${path.posix.basename(source)}"`;
      const value = readSnippet(root, source, region);
      let fence = '```';
      while (value.includes(fence)) fence += '`';
      // In place, as Fumadocs' include does, so the page's Markdown carries the code where the element stood.
      const { position } = node;
      for (const key of Object.keys(node)) delete node[key];
      Object.assign(node, { type: 'code', lang, meta, value, position });
      replaceSource(file as never, node as never, `${fence}${lang} ${meta}\n${value}\n${fence}`);
      return 'skip';
    });
  };
}
