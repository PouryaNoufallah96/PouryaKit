import Slugger from 'github-slugger';
import type { Heading, Nodes, Root, RootContent } from 'mdast';
import { toString as mdastToString } from 'mdast-util-to-string';
import { remark } from 'remark';
import remarkGfm from 'remark-gfm';
import remarkMdx from 'remark-mdx';
import { visit } from 'unist-util-visit';
import { addressViolations, type Violation, vocabularyViolations } from './rules.ts';

// Tables are written unpadded, as the sources write them: padding every cell to its column's widest doubled the skill's
// SKILL.md, which the public repository publishes for agents to read.
const markdown = remark()
  .use(remarkGfm, { tablePipeAlign: false })
  .use({ settings: { bullet: '-', emphasis: '_', rule: '-' } });
const mdx = remark().use(remarkGfm).use(remarkMdx);

/**
 * A Kit source as Markdown. Raw HTML becomes text: the sources hold none on purpose, so `<full CLR name>` in prose is
 * text, and a page never carries markup a source did not mean.
 */
export function parseMarkdown(text: string): Root {
  const tree = markdown.parse(text);
  visit(tree, 'html', (node, index, parent) => {
    if (!parent || index === undefined) return;
    const text = { type: 'text' as const, value: node.value, position: node.position };
    const flow = ['root', 'blockquote', 'listItem', 'footnoteDefinition'].includes(parent.type);
    parent.children.splice(
      index,
      1,
      (flow ? { type: 'paragraph', children: [text], position: node.position } : text) as never,
    );
  });
  return tree;
}

/** A Markdown file as written, for the check that refuses what `parseMarkdown` would turn into text. */
export function parseVerbatim(text: string): Root {
  return markdown.parse(text);
}

const FRONTMATTER = /^---\n[\s\S]*?\n---\n/;

/**
 * A Kit source with its YAML frontmatter blanked, each of its lines kept as an empty one so the source's line numbers
 * hold: frontmatter is metadata for its tools, and parsed as Markdown it renders as a rule and a line of text.
 */
function blankFrontmatter(text: string): string {
  const match = FRONTMATTER.exec(text);
  return match ? match[0].replace(/[^\n]/g, '') + text.slice(match[0].length) : text;
}

/** A Kit source's frontmatter as written, its `---` lines included, or nothing: the skill's copy keeps SKILL.md's. */
export function sourceFrontmatter(text: string): string {
  return FRONTMATTER.exec(text)?.[0] ?? '';
}

/** A Kit source file as Markdown, without its frontmatter. */
export function parseSource(text: string): Root {
  return parseMarkdown(blankFrontmatter(text));
}

/** A site page written in MDX, such as a Capability page's hand-written top. */
export function parseMdx(text: string): Root {
  return mdx.parse(text);
}

export function stringifyMarkdown(tree: Root): string {
  return markdown.stringify(tree);
}

/** A generated page in MDX, for one that holds a component: the text's `<` and `{` are escaped, so prose stays prose. */
export function stringifyMdx(tree: Root): string {
  return mdx.stringify(tree);
}

/** The text a node renders, code included; link destinations are markup and stay out. */
export function renderedText(node: Nodes): string {
  return mdastToString(node, { includeHtml: false });
}

/** The URLs a node links or points to. */
export function urlsOf(node: Nodes): string[] {
  const urls: string[] = [];
  visit(node, (child) => {
    if ('url' in child && typeof child.url === 'string') urls.push(child.url);
  });
  return urls;
}

// The nodes whose children are blocks: a page, a quote, a list and its items, a table and its rows, a component.
const BLOCK_PARENTS = new Set([
  'root',
  'blockquote',
  'list',
  'listItem',
  'table',
  'tableRow',
  'footnoteDefinition',
  'mdxJsxFlowElement',
]);

/** A node's rendered text with each block's end a blank line, which the rules never read a phrase across. */
function blockText(node: Nodes): string {
  return BLOCK_PARENTS.has(node.type) && 'children' in node
    ? node.children.map((child) => blockText(child as Nodes)).join('\n\n')
    : renderedText(node);
}

/** The rules a projected node must pass: its rendered text, block by block, and its URLs. */
export function nodeViolations(node: Nodes): Violation[] {
  const text = blockText(node);
  return [
    ...addressViolations(text),
    ...vocabularyViolations(text),
    ...urlsOf(node).flatMap((url) => addressViolations(url)),
  ];
}

/** Each heading's anchor as Fumadocs gives it on the rendered page: GitHub's slugs, numbered when repeated. */
export function headingSlugs(nodes: RootContent[], slugger = new Slugger()): Map<Heading, string> {
  const slugs = new Map<Heading, string>();
  for (const node of nodes) {
    visit(node, 'heading', (heading) => {
      slugs.set(heading, slugger.slug(renderedText(heading)));
    });
  }
  return slugs;
}

export function frontmatter(fields: Record<string, string>): string {
  return `---\n${Object.entries(fields)
    .map(([key, value]) => `${key}: ${JSON.stringify(value)}`)
    .join('\n')}\n---\n\n`;
}

/** The first sentence of a text, for a page's one-line description. */
export function firstSentence(text: string): string {
  const flat = text.replace(/\s+/g, ' ').trim();
  const end = flat.search(/[.!?](\s|$)/);
  return end === -1 ? flat : flat.slice(0, end + 1);
}
