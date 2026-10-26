import { createHash } from 'node:crypto';
import Slugger from 'github-slugger';
import type { Heading, List, Root, RootContent, Table } from 'mdast';
import { firstSentence, parseMarkdown, renderedText } from './markdown.ts';
import { addressViolations, vocabularyViolations } from './rules.ts';

// The ADRs' public projection. The records stay as they are; the site publishes each with its tracker references
// rewritten, and leaves out each passage that still names one. Every passage left out is listed in the committed
// manifest, so an ADR edit that changes what is published fails the build until the manifest is reviewed again.

/** One reviewed rewrite: on `line` of the ADR, the text `from` (any run of white space matching any other) becomes `to`. */
export type AdrRewrite = { adr: string; line: number; from: string; to: string };

/** One passage left out: where it starts, what kind of block it is, the rule it fails, and a digest of its text. */
export type AdrExclusion = { adr: string; line: number; kind: string; rule: string; digest: string; text: string };

export type ProjectedAdr = {
  slug: string;
  title: string;
  description: string;
  /** The body below the title. */
  tree: Root;
  /** Each anchor of the source to its anchor on the page, or null when its section is left out. */
  anchors: Map<string, string | null>;
  exclusions: AdrExclusion[];
};

const TRACKER = /#\d+|\bW\d+\b|\bWave\b/;

// The parts of a parenthetical that only cite the tracker: a ticket, issue, spec or pull request number, a quoted section
// of a spec, a finding or decision code, or a review of one of them.
const CITATION_PARTS = [
  /^(?:the )?(?:(?:tickets?|issues?|specs?|PRs?)\s+)?#\d+(?:(?:\s*,\s*|\s+(?:and|or|to)\s+)#\d+)*(?:'s [a-z ]+| follow-up| review| amendment(?: above| below)?)?$/i,
  /^"[^"]+"(?: item \d+)?$/,
  /^(?:owner )?(?:finding|decision) [A-Z]+\d+$/,
  /^(?:the )?(?:first |second |final )?review(?: round)? of (?:PR|ticket) #\d+$/,
  /^out of scope$/i,
];

function isCitation(inner: string): boolean {
  return (
    TRACKER.test(inner) && inner.split(/[;,]\s*/).every((part) => CITATION_PARTS.some((rule) => rule.test(part.trim())))
  );
}

// A replacement keeps the line breaks of the text it replaces, each with the indentation after it, so every line of the
// projected ADR stays at its line in the source and the manifest's lines are the record's.
function keepLines(matched: string, replacement: string, where: string): string {
  const breaks = matched.match(/\n[ \t]*/g) ?? [];
  const own = (replacement.match(/\n/g) ?? []).length;
  if (own > breaks.length)
    throw new Error(`kit-sources: the rewrite at ${where} adds lines; keep it to the lines it replaces`);
  return replacement + breaks.slice(own).join('');
}

function flexible(text: string): RegExp {
  return new RegExp(
    text
      .trim()
      .split(/\s+/)
      .map((word) => word.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
      .join('\\s+'),
    'g',
  );
}

function lineStarts(text: string): number[] {
  const starts = [0];
  for (let index = 0; index < text.length; index++) if (text[index] === '\n') starts.push(index + 1);
  return starts;
}

/** Applies the reviewed rewrites of one ADR; a keyed line whose text no longer matches fails. */
export function applyRewrites(slug: string, text: string, rewrites: AdrRewrite[]): string {
  const starts = lineStarts(text);
  const edits = rewrites
    .filter((rewrite) => rewrite.adr === slug)
    .map((rewrite) => {
      const where = `${slug}.md:${rewrite.line}`;
      const lineStart = starts[rewrite.line - 1];
      const lineEnd = starts[rewrite.line] ?? text.length + 1;
      const matches = [...text.matchAll(flexible(rewrite.from))].filter(
        (match) => match.index >= lineStart && match.index < lineEnd,
      );
      if (lineStart === undefined || matches.length !== 1) {
        throw new Error(
          `kit-sources: the ADR rewrite at ${where} no longer matches its line (${matches.length} matches of ${JSON.stringify(rewrite.from)}); review lib/kit-sources/adr-rewrites.json against the ADR`,
        );
      }
      const [match] = matches;
      return { start: match.index, end: match.index + match[0].length, text: keepLines(match[0], rewrite.to, where) };
    })
    .sort((a, b) => b.start - a.start);
  let result = text;
  for (const edit of edits) result = result.slice(0, edit.start) + edit.text + result.slice(edit.end);
  return result;
}

/** The generic rules, outside fenced code: a parenthetical that only cites the tracker goes, and so does an amendment heading's citation. */
export function dropCitations(text: string): string {
  let fenced = false;
  const segments: { code: boolean; text: string }[] = [];
  for (const line of text.split('\n')) {
    const fence = /^\s*(```|~~~)/.test(line);
    const code = fenced || fence;
    if (fence) fenced = !fenced;
    const last = segments.at(-1);
    if (last && last.code === code) last.text += `\n${line}`;
    else segments.push({ code, text: line });
  }
  return segments
    .map((segment) =>
      segment.code
        ? segment.text
        : segment.text
            .replace(/^(#{2,6} Amendment \()(\d{4}-\d{2}-\d{2}), ([^\n]*)\)[ \t]*$/gm, (heading, open, date, rest) => {
              const colon = rest.indexOf(': ');
              if (colon !== -1 && TRACKER.test(rest.slice(0, colon)))
                return `${open}${date}: ${rest.slice(colon + 2)})`;
              if (colon === -1 && TRACKER.test(rest)) return `${open}${date})`;
              return heading;
            })
            .replace(/[ \t]?\(([^()]*)\)/g, (whole, inner: string) =>
              isCitation(inner) ? keepLines(whole, '', 'a citation') : whole,
            ),
    )
    .join('\n');
}

function blankFrontmatter(text: string): string {
  const match = /^---\n[\s\S]*?\n---\n/.exec(text);
  return match ? match[0].replace(/[^\n]/g, '') + text.slice(match[0].length) : text;
}

function normalized(text: string) {
  return text.replace(/\s+/g, ' ').trim();
}

function exclusion(slug: string, node: RootContent, kind: string, text: string, rule: string): AdrExclusion {
  const flat = normalized(text);
  return {
    adr: slug,
    line: node.position?.start.line ?? 0,
    kind,
    rule,
    digest: createHash('sha256').update(flat).digest('hex').slice(0, 16),
    text: flat.length > 120 ? `${flat.slice(0, 117)}...` : flat,
  };
}

/** A table without each column, past the first, in which every row cites the tracker: who built it, not what it says. */
export function withoutTrackerColumns(table: Table): Table {
  const [header, ...rows] = table.children;
  if (rows.length === 0) return table;
  const drop = header.children
    .map((_, column) => column)
    .filter((column) => column > 0 && rows.every((row) => TRACKER.test(renderedText(row.children[column] ?? header))));
  if (drop.length === 0) return table;
  const keep = <T>(cells: T[]) => cells.filter((_, column) => !drop.includes(column));
  return {
    ...table,
    align: keep(table.align ?? []),
    children: table.children.map((row) => ({ ...row, children: keep(row.children) })),
  };
}

function failingRule(text: string): string | undefined {
  return [...addressViolations(text), ...vocabularyViolations(text)][0]?.rule;
}

/** Projects one ADR, given its file name without `.md`, its text and the reviewed rewrites. */
export function projectAdr(slug: string, source: string, rewrites: AdrRewrite[]): ProjectedAdr {
  const original = parseMarkdown(blankFrontmatter(source));
  const tree = parseMarkdown(dropCitations(applyRewrites(slug, blankFrontmatter(source), rewrites)));

  const titles = tree.children.filter((node) => node.type === 'heading' && node.depth === 1);
  if (titles.length !== 1) throw new Error(`kit-sources: ${slug}.md needs exactly one title heading`);
  const title = renderedText(titles[0]);

  // The anchors a link to the record names: GitHub's slugs of its own headings, title included.
  const sourceSlugger = new Slugger();
  const sourceAnchors = new Map<number, string>();
  for (const node of original.children) {
    if (node.type === 'heading')
      sourceAnchors.set(node.position?.start.line ?? 0, sourceSlugger.slug(renderedText(node)));
  }

  const exclusions: AdrExclusion[] = [];
  const kept: RootContent[] = [];
  let skipBelow: number | undefined;
  for (const node of tree.children) {
    if (node === titles[0]) continue;
    if (skipBelow !== undefined) {
      if (node.type !== 'heading' || node.depth > skipBelow) continue;
      skipBelow = undefined;
    }
    if (node.type === 'heading') {
      const rule = failingRule(renderedText(node));
      if (rule) {
        // A heading that cites the tracker takes its whole section with it.
        const section = tree.children.slice(tree.children.indexOf(node));
        const end = section.findIndex(
          (next, index) => index > 0 && next.type === 'heading' && next.depth <= node.depth,
        );
        const text = (end === -1 ? section : section.slice(0, end)).map((part) => renderedText(part)).join('\n');
        exclusions.push(exclusion(slug, node, 'section', text, rule));
        skipBelow = node.depth;
        continue;
      }
      kept.push(node);
    } else if (node.type === 'list') {
      const items = node.children.filter((item) => {
        const rule = failingRule(renderedText(item));
        if (rule) exclusions.push(exclusion(slug, item, 'list item', renderedText(item), rule));
        return !rule;
      });
      if (items.length > 0) kept.push({ ...node, children: items } as List);
    } else if (node.type === 'table') {
      const [header, ...rows] = withoutTrackerColumns(node).children;
      const headerRule = failingRule(renderedText(header));
      if (headerRule) {
        exclusions.push(
          exclusion(slug, node, 'table', node.children.map((row) => renderedText(row)).join('\n'), headerRule),
        );
        continue;
      }
      const keptRows = rows.filter((row) => {
        const rule = failingRule(row.children.map((cell) => renderedText(cell)).join(' | '));
        if (rule)
          exclusions.push(
            exclusion(slug, row, 'table row', row.children.map((cell) => renderedText(cell)).join(' | '), rule),
          );
        return !rule;
      });
      if (keptRows.length > 0) kept.push({ ...node, children: [header, ...keptRows] } as Table);
    } else {
      const rule = failingRule(renderedText(node));
      if (rule) {
        exclusions.push(exclusion(slug, node, node.type, renderedText(node), rule));
        continue;
      }
      kept.push(node);
    }
  }

  // A heading whose section lost all of its content goes too.
  const body: RootContent[] = [];
  for (let index = kept.length - 1; index >= 0; index--) {
    const node = kept[index];
    if (node.type === 'heading') {
      const next = body[0];
      if (!next || (next.type === 'heading' && next.depth <= node.depth)) continue;
    }
    body.unshift(node);
  }

  const pageSlugger = new Slugger();
  const pageAnchors = new Map<number, string>();
  for (const node of body) {
    if (node.type === 'heading')
      pageAnchors.set(node.position?.start.line ?? 0, pageSlugger.slug(renderedText(node as Heading)));
  }
  const anchors = new Map<string, string | null>();
  for (const [line, anchor] of sourceAnchors) anchors.set(anchor, pageAnchors.get(line) ?? null);

  const first = body.find((node) => node.type === 'paragraph');
  return {
    slug,
    title,
    description: first ? firstSentence(renderedText(first)) : title,
    tree: { type: 'root', children: body },
    anchors,
    exclusions,
  };
}

function key(entry: AdrExclusion) {
  return `${entry.adr}:${entry.line} ${entry.kind} ${entry.digest}`;
}

/** Compares the passages a projection leaves out with the committed manifest; any difference fails, both ways. */
export function compareManifest(computed: AdrExclusion[], manifest: AdrExclusion[]): string[] {
  const listed = new Set(manifest.map(key));
  const found = new Set(computed.map(key));
  return [
    ...computed
      .filter((entry) => !listed.has(key(entry)))
      .map((entry) => `left out but not in the manifest: ${key(entry)} ${entry.text}`),
    ...manifest
      .filter((entry) => !found.has(key(entry)))
      .map((entry) => `in the manifest but not left out: ${key(entry)} ${entry.text}`),
  ];
}
