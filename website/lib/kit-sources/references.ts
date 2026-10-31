import type { Root, Table, TableRow } from 'mdast';
import { parseSource, renderedText } from './markdown.ts';
import { listDirectory, REFERENCES, readSource, SKILL } from './paths.ts';

/**
 * The skill references the site publishes, by file name. A reference is published only when it is listed here, so a new
 * one waits for a deliberate edit; the two maintainer guides below are never published.
 */
export const PUBLISHED_REFERENCES = [
  'ai.md',
  'aspire.md',
  'caching.md',
  'commands-queries-validation.md',
  'core.md',
  'dapper-reads.md',
  'domain-primitives.md',
  'email.md',
  'grpc.md',
  'host-recipes.md',
  'localization.md',
  'messaging.md',
  'modules.md',
  'multi-tenancy.md',
  'observability.md',
  'openapi.md',
  'persistence.md',
  'rabbitmq.md',
  'realtime.md',
  'reporting-module.md',
  'reporting.md',
  'reverse-proxy.md',
  'sagas.md',
  'security.md',
  'storage.md',
  'stream.md',
  'templates.md',
  'testing.md',
  'web.md',
] as const;

/** How a package is validated, consumed and published: maintainer guides, never published. */
export const MAINTAINER_GUIDES = ['packages.md', 'publication.md'] as const;

export type SkillEntry = {
  /** The Capability or guide name, as the skill's table gives it. */
  name: string;
  /** The reference's file name under `references/`. */
  file: string;
  /** The page slug: the file name without `.md`. */
  slug: string;
  /** The package IDs the table names for it. */
  packages: string[];
  /** The table's "Read when" text. */
  readWhen: string;
  group: 'Capabilities' | 'Guides';
};

/**
 * The published references, checked against the directory: an unlisted file of any kind and a listed file that is gone
 * both fail.
 */
export function publishedReferences(root: string): string[] {
  const present = listDirectory(root, REFERENCES);
  const known = new Set<string>([...PUBLISHED_REFERENCES, ...MAINTAINER_GUIDES]);
  const unlisted = present.filter((file) => !known.has(file));
  if (unlisted.length > 0) {
    throw new Error(
      `kit-sources: ${unlisted.join(', ')} under ${REFERENCES} is on neither list in lib/kit-sources/references.ts; add a reference to PUBLISHED_REFERENCES to publish it, or to MAINTAINER_GUIDES, and move any other file out`,
    );
  }
  const presentNames = new Set(present);
  const missing = PUBLISHED_REFERENCES.filter((file) => !presentNames.has(file));
  if (missing.length > 0)
    throw new Error(`kit-sources: published references missing from ${REFERENCES}: ${missing.join(', ')}`);
  return [...PUBLISHED_REFERENCES];
}

function tableAfter(tree: Root, heading: string): Table {
  const index = tree.children.findIndex((node) => node.type === 'heading' && renderedText(node) === heading);
  const table = tree.children.slice(index + 1).find((node) => node.type === 'table' || node.type === 'heading');
  if (index === -1 || table?.type !== 'table') throw new Error(`kit-sources: ${SKILL} has no table under "${heading}"`);
  return table;
}

function referenceOf(row: TableRow, cell: number): string {
  const link = row.children[cell]?.children.find((node) => node.type === 'link');
  const match = link?.type === 'link' ? /^references\/([a-z0-9-]+\.md)$/.exec(link.url) : null;
  if (!match) throw new Error(`kit-sources: a row of ${SKILL} links no reference: ${renderedText(row)}`);
  return match[1];
}

/** The IDs a "Packages" cell names: `PouryaKit.Persistence`, `.EFCore` is PouryaKit.Persistence and PouryaKit.Persistence.EFCore. */
export function packagesOf(cell: string[]): string[] {
  let base: string | undefined;
  return cell.map((name) => {
    if (!name.startsWith('.')) {
      base = name;
      return name;
    }
    if (!base) throw new Error(`kit-sources: the package ${name} in ${SKILL} has no full ID before it`);
    return base + name;
  });
}

/** The Capability and guide tables of the skill, in their order, holding only published references. */
export function skillEntries(root: string): SkillEntry[] {
  const published = new Set(publishedReferences(root));
  const tree = parseSource(readSource(root, SKILL));
  const entries: SkillEntry[] = [];
  for (const row of tableAfter(tree, 'Capabilities').children.slice(1)) {
    const file = referenceOf(row, 3);
    if (!published.has(file))
      throw new Error(`kit-sources: the Capability table links ${file}, which is not published`);
    const codes = row.children[1].children.filter((node) => node.type === 'inlineCode').map((node) => node.value);
    entries.push({
      name: renderedText(row.children[0]),
      file,
      slug: file.replace(/\.md$/, ''),
      packages: packagesOf(codes),
      readWhen: renderedText(row.children[2]),
      group: 'Capabilities',
    });
  }
  for (const row of tableAfter(tree, 'Guides').children.slice(1)) {
    const file = referenceOf(row, 2);
    if (!published.has(file)) continue;
    entries.push({
      name: renderedText(row.children[0]),
      file,
      slug: file.replace(/\.md$/, ''),
      packages: [],
      readWhen: renderedText(row.children[1]),
      group: 'Guides',
    });
  }
  const listed = new Set(entries.map((entry) => entry.file));
  const orphans = [...published].filter((file) => !listed.has(file));
  if (orphans.length > 0)
    throw new Error(`kit-sources: published references no table of ${SKILL} lists: ${orphans.join(', ')}`);
  return entries;
}
