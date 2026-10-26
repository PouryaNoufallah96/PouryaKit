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
// and heading ids left out; and every string of the search index.

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

/** An HTML page's rendered text: scripts, styles and comments dropped, tags removed, character references decoded. */
export function htmlText(html: string): string {
  return decodeEntities(
    html
      .replace(/<!--[\s\S]*?-->/g, ' ')
      .replace(/<(script|style|noscript|template)\b[^>]*>[\s\S]*?<\/\1\s*>/gi, ' ')
      .replace(/<[^>]*>/g, ' '),
  );
}

/** The text of Markdown or MDX a reader sees: text and code, image descriptions and component attributes. */
export function markdownText(markdown: string): string {
  let tree: Root;
  try {
    tree = parseMdx(markdown);
  } catch {
    tree = parseMarkdown(markdown);
  }
  const parts: string[] = [];
  visit(tree, (node) => {
    if (node.type === 'heading') {
      // Fumadocs writes a heading's id after it, as `[#id]`: markup, not text.
      const last = node.children.at(-1);
      if (last?.type === 'text') last.value = last.value.replace(/\s*\[#[^\]]*\]\s*$/, '');
    }
    if ('value' in node && typeof node.value === 'string' && node.type !== 'html') parts.push(node.value);
    if (node.type === 'image' && node.alt) parts.push(node.alt);
    const attributes = (node as { attributes?: { type: string; value?: unknown }[] }).attributes;
    for (const attribute of attributes ?? []) {
      if (attribute.type === 'mdxJsxAttribute' && typeof attribute.value === 'string') parts.push(attribute.value);
    }
  });
  return parts.join('\n');
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
  if (relative === 'api/search.json') return jsonStrings(JSON.parse(content)).join('\n');
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
    for (const violation of addressViolations(addressed)) findings.push({ ...violation, file: relative });
    const text = renderedTextOf(relative, content);
    if (text === undefined) continue;
    rendered++;
    for (const violation of vocabularyViolations(text)) findings.push({ ...violation, file: relative });
  }
  return { findings, scanned, rendered };
}
