import { readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import type { Root } from 'mdast';
import { visit } from 'unist-util-visit';
import { parseMarkdown, parseMdx } from './markdown.ts';
import { addressViolations, type Violation, vocabularyViolations } from './rules.ts';

// The build's last step: the rules over what the static export publishes. Every file of out/ is read for the address
// rules (the private repository, a local path), markup included, since a link lives in an attribute and a bundle can
// carry a path, with character references and string escapes undone first. Only rendered text is read for delivery vocabulary and tracker codes, since a CSS colour (#615fff), an
// attribute or a script is no text a reader sees: an HTML page's text outside its scripts and styles, with its
// character references decoded; each page's Markdown and llms*.txt as Markdown text, code included, link destinations
// and heading ids left out; and every string of the search index. A phrase is read across a soft line break, never
// from one block into the next: a table's cells, a page's paragraphs and the index's strings each end a block.

export type Finding = Violation & { file: string };

const BINARY = new Set(['.png', '.jpg', '.jpeg', '.gif', '.webp', '.avif', '.ico', '.woff', '.woff2', '.ttf', '.otf']);

const ENTITIES: Record<string, string> = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ' };

export function decodeEntities(text: string): string {
  return text.replace(/&(#x[0-9a-f]+|#\d+|[a-z]+);/gi, (whole, name: string) => {
    if (name[0] === '#') {
      const code =
        name[1] === 'x' || name[1] === 'X' ? Number.parseInt(name.slice(2), 16) : Number.parseInt(name.slice(1), 10);
      return Number.isFinite(code) ? String.fromCodePoint(code) : whole;
    }
    return ENTITIES[name.toLowerCase()] ?? whole;
  });
}

/**
 * A script's or a JSON file's string escapes undone (`\u003c`, `\/`, `\\`), so a URL written `https:\/\/...` in a
 * payload is read as the URL it is, and `a:\u003cid` as the text `a:<id>` it is, not as a Windows path.
 */
export function unescapeStrings(text: string): string {
  return text.replace(/\\(u[0-9a-fA-F]{4}|\/|\\)/g, (_, sequence: string) =>
    sequence.length === 5 ? String.fromCharCode(Number.parseInt(sequence.slice(1), 16)) : sequence,
  );
}

// A block's end in extracted text: a blank line, which the rules never read a phrase across (`softSpaced`).
const BLOCK_END = '\n\n';

// The HTML elements that end a block of text, such as a table's cells: every other tag is inline and renders nothing.
const HTML_BLOCK =
  /<\/?(?:address|article|aside|blockquote|button|caption|dd|details|dialog|div|dl|dt|fieldset|figcaption|figure|footer|form|h[1-6]|header|hr|label|legend|li|main|nav|ol|option|p|pre|section|select|summary|table|tbody|td|tfoot|th|thead|title|tr|ul)\b[^>]*>/gi;

// The Markdown nodes that end a block of text.
const MARKDOWN_BLOCK = new Set([
  'blockquote',
  'code',
  'heading',
  'list',
  'listItem',
  'mdxJsxFlowElement',
  'paragraph',
  'table',
  'tableCell',
  'tableRow',
  'thematicBreak',
]);

/**
 * An HTML page's rendered text: scripts, styles and comments dropped, each block's end a blank line, other tags removed,
 * character references decoded.
 */
export function htmlText(html: string): string {
  return decodeEntities(
    html
      .replace(/<!--[\s\S]*?-->/g, BLOCK_END)
      .replace(/<(script|style|noscript|template)\b[^>]*>[\s\S]*?<\/\1\s*>/gi, BLOCK_END)
      .replace(/<br\b[^>]*>/gi, '\n')
      .replace(HTML_BLOCK, BLOCK_END)
      .replace(/<[^>]*>/g, ''),
  );
}

/**
 * The text of Markdown or MDX a reader sees, each block's end a blank line: text and code, image descriptions, link and
 * image titles and component attributes. A title comes after the text, so a phrase is still read across its link.
 */
export function markdownText(markdown: string): string {
  let tree: Root;
  try {
    tree = parseMdx(markdown);
  } catch {
    tree = parseMarkdown(markdown);
  }
  const parts: string[] = [];
  const titles: string[] = [];
  visit(tree, (node) => {
    if (MARKDOWN_BLOCK.has(node.type)) parts.push(BLOCK_END);
    if (node.type === 'heading') {
      // Fumadocs writes a heading's id after it, as `[#id]`: markup, not text.
      const last = node.children.at(-1);
      if (last?.type === 'text') last.value = last.value.replace(/\s*\[#[^\]]*\]\s*$/, '');
    }
    // A code block's lines are lines of their own.
    if (node.type === 'code') parts.push(node.value.replaceAll('\n', BLOCK_END));
    else if ('value' in node && typeof node.value === 'string' && node.type !== 'html') parts.push(node.value);
    if (node.type === 'image' && node.alt) parts.push(node.alt);
    if ((node.type === 'link' || node.type === 'image') && node.title) titles.push(node.title);
    const attributes = (node as { attributes?: { type: string; value?: unknown }[] }).attributes;
    for (const attribute of attributes ?? []) {
      if (attribute.type === 'mdxJsxAttribute' && typeof attribute.value === 'string')
        parts.push(BLOCK_END, attribute.value);
    }
  });
  return [...parts, ...titles.flatMap((title) => [BLOCK_END, title])].join('');
}

function jsonStrings(value: unknown, out: string[] = []): string[] {
  if (typeof value === 'string') out.push(value);
  else if (Array.isArray(value)) for (const item of value) jsonStrings(item, out);
  else if (value && typeof value === 'object') {
    for (const item of Object.values(value)) jsonStrings(item, out);
  }
  return out;
}

/** The rendered text of one exported file, or undefined for a file that renders none (a script, a style, a payload). */
export function renderedTextOf(relative: string, content: string): string | undefined {
  const name = path.posix.basename(relative);
  if (relative.endsWith('.html')) return htmlText(content);
  if (relative.endsWith('.md') || name === 'llms.txt' || name === 'llms-full.txt') return markdownText(content);
  if (relative === 'api/search.json') return jsonStrings(JSON.parse(content)).join(BLOCK_END);
  return undefined;
}

function files(directory: string, base = directory): string[] {
  return readdirSync(directory)
    .sort()
    .flatMap((name) => {
      const full = path.join(directory, name);
      return statSync(full).isDirectory() ? files(full, base) : [path.relative(base, full).split(path.sep).join('/')];
    });
}

/** Every rule over the static export in `out`, with the count of files read and of those read as rendered text. */
export function checkExport(out: string): { findings: Finding[]; scanned: number; rendered: number } {
  const findings: Finding[] = [];
  let scanned = 0;
  let rendered = 0;
  for (const relative of files(out)) {
    if (BINARY.has(path.posix.extname(relative).toLowerCase())) continue;
    const content = readFileSync(path.join(out, relative), 'utf8');
    if (content.includes('\u0000')) continue;
    scanned++;
    const addressed = unescapeStrings(relative.endsWith('.html') ? decodeEntities(content) : content);
    for (const violation of addressViolations(addressed, relative.endsWith('.js')))
      findings.push({ ...violation, file: relative });
    const text = renderedTextOf(relative, content);
    if (text === undefined) continue;
    rendered++;
    for (const violation of vocabularyViolations(text)) findings.push({ ...violation, file: relative });
  }
  return { findings, scanned, rendered };
}
