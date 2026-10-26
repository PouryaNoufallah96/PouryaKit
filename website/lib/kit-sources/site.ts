import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import Slugger from 'github-slugger';
import type { Heading, Root, RootContent, Table } from 'mdast';
import { visit } from 'unist-util-visit';
import { type AdrExclusion, type AdrRewrite, compareManifest, type ProjectedAdr, projectAdr } from './adr.ts';
import { type ErrorCode, readErrorCodes } from './errors.ts';
import { glossaryMarkdown, parseGlossary, projectGlossary } from './glossary.ts';
import { type PageRoute, type RouteMap, rewriteLinks } from './links.ts';
import {
  firstSentence,
  frontmatter,
  nodeViolations,
  parseMarkdown,
  parseMdx,
  renderedText,
  stringifyMarkdown,
} from './markdown.ts';
import { type KitPackage, readPackages } from './packages.ts';
import {
  ADRS,
  CAPABILITY_TOPS,
  GLOSSARY,
  listDirectory,
  PACKAGES,
  REFERENCES,
  readSource,
  resolveInside,
  SKILL,
  sourceExists,
} from './paths.ts';
import { type SkillEntry, skillEntries } from './references.ts';
import { describe } from './rules.ts';

// The pages the site renders from the Kit's sources, generated before `next build` into folders git ignores: the
// Capability pages, and the Reference pages (the package matrix and one page per package, the ADRs, the glossary, the
// Error codes and the Host's checks). Every page is projected, its links rewritten, and checked against the rules
// before it is written.

/** The folders the generator owns, relative to `website/`; it empties them before it writes. */
export const GENERATED = ['content/docs/capabilities', 'content/docs/reference', '.generated'];

export const ADR_REWRITES = 'website/lib/kit-sources/adr-rewrites.json';
export const ADR_MANIFEST = 'website/lib/kit-sources/adr-manifest.json';
export const MANIFEST_COMMAND = 'pnpm --dir website adr-manifest';

const CAPABILITIES_ROUTE = '/docs/capabilities';
const REFERENCE_ROUTE = '/docs/reference';
const ADR_ROUTE = `${REFERENCE_ROUTE}/adr`;
const PACKAGES_ROUTE = `${REFERENCE_ROUTE}/packages`;
const GLOSSARY_ROUTE = `${REFERENCE_ROUTE}/glossary`;

export type GeneratedFile = { path: string; content: string };

type Page = { file: string; source: string; title: string; description: string; tree: Root };

/** Each anchor a link to the source may name (GitHub's slugs over all of its headings) to its slug on the page. */
function anchorMap(source: Root, page: RootContent[], pageSlugger = new Slugger()): Map<string, string | null> {
  const pageSlugs = new Map<Heading, string>();
  for (const node of page) if (node.type === 'heading') pageSlugs.set(node, pageSlugger.slug(renderedText(node)));
  const sourceSlugger = new Slugger();
  const anchors = new Map<string, string | null>();
  for (const node of source.children) {
    if (node.type === 'heading') anchors.set(sourceSlugger.slug(renderedText(node)), pageSlugs.get(node) ?? null);
  }
  return anchors;
}

/**
 * A skill reference's anchors on its Capability page, below the headings of its hand-written top: a top heading whose
 * slug would renumber one of the reference's fails, since links between references name the reference's own.
 */
export function referenceAnchors(tree: Root, topHeadings: string[], top: string): Map<string, string | null> {
  const slugger = new Slugger();
  for (const heading of topHeadings) slugger.slug(heading);
  const anchors = anchorMap(tree, withoutTitle(tree), slugger);
  for (const [anchor, alone] of anchorMap(tree, withoutTitle(tree))) {
    if (anchors.get(anchor) !== alone) {
      throw new Error(`kit-sources: ${top} has a heading whose anchor is the reference's ${alone}; rename it`);
    }
  }
  return anchors;
}

function withoutTitle(tree: Root): RootContent[] {
  return tree.children.filter((node) => !(node.type === 'heading' && node.depth === 1));
}

function titleOf(tree: Root, source: string): string {
  const title = tree.children.find((node) => node.type === 'heading' && node.depth === 1);
  if (!title) throw new Error(`kit-sources: ${source} has no title heading`);
  return renderedText(title);
}

function introOf(nodes: RootContent[]): string {
  const first = nodes.find((node) => node.type === 'paragraph');
  return first ? firstSentence(renderedText(first)) : '';
}

/** A Capability page's hand-written top, written for the site: its description and the slugs of its headings. */
function readTop(root: string, slug: string): { file: string; description?: string; headings: string[] } | undefined {
  const file = `${CAPABILITY_TOPS}/${slug}.mdx`;
  if (!sourceExists(root, file)) return undefined;
  const text = readSource(root, file);
  const tree = parseMdx(text);
  const esm = tree.children.find((node) => (node.type as string) === 'mdxjsEsm');
  if (esm) throw new Error(`kit-sources: ${file} imports or exports; a top uses the site's components without either`);
  const violations = nodeViolations(tree);
  if (violations.length > 0) throw new Error(`kit-sources: ${file}: ${describe(violations)}`);
  const yaml = /^---\n([\s\S]*?)\n---\n/.exec(text)?.[1] ?? '';
  const description = /^description:\s*(.+)$/m.exec(yaml)?.[1]?.replace(/^["']|["']$/g, '');
  // Every heading, those inside a component included, since Fumadocs gives each an anchor.
  const headings: string[] = [];
  visit(tree, 'heading', (heading) => {
    headings.push(renderedText(heading));
  });
  return { file, description, headings };
}

function check(page: Page) {
  const violations = nodeViolations(page.tree);
  for (const text of [page.title, page.description]) violations.push(...nodeViolations({ type: 'text', value: text }));
  if (violations.length > 0)
    throw new Error(`kit-sources: ${page.file} (from ${page.source}): ${describe(violations)}`);
}

function markdownPage(page: Page): GeneratedFile {
  check(page);
  return {
    path: page.file,
    content: frontmatter({ title: page.title, description: page.description }) + stringifyMarkdown(page.tree),
  };
}

function cell(text: string) {
  return text.replaceAll('|', '\\|').replace(/\s+/g, ' ').trim();
}

function code(text: string) {
  return `\`${text}\``;
}

function meta(title: string, pages: string[]) {
  return `${JSON.stringify({ title, pages }, null, 2)}\n`;
}

/** Projects every ADR with the reviewed rewrites. */
export function projectAdrs(root: string): ProjectedAdr[] {
  const rewrites = JSON.parse(readSource(root, ADR_REWRITES)) as AdrRewrite[];
  const files = listDirectory(root, ADRS).filter((file) => /^\d{4}-.+\.md$/.test(file));
  const unknown = rewrites.filter((rewrite) => !files.includes(`${rewrite.adr}.md`));
  if (unknown.length > 0)
    throw new Error(
      `kit-sources: ${ADR_REWRITES} names ADRs that do not exist: ${unknown.map((rewrite) => rewrite.adr).join(', ')}`,
    );
  return files.map((file) => projectAdr(file.replace(/\.md$/, ''), readSource(root, `${ADRS}/${file}`), rewrites));
}

/** The passages the ADR projection leaves out, as the committed manifest lists them. */
export function adrExclusions(adrs: ProjectedAdr[]): AdrExclusion[] {
  return adrs.flatMap((adr) => adr.exclusions);
}

export function readManifest(root: string): AdrExclusion[] {
  return JSON.parse(readSource(root, ADR_MANIFEST)) as AdrExclusion[];
}

/** Every generated page, checked; the ADR projection must leave out exactly what the committed manifest lists. */
export function buildSite(root: string): GeneratedFile[] {
  const entries = skillEntries(root);
  const packages = readPackages(root, entries);
  const adrs = projectAdrs(root);
  const differences = compareManifest(adrExclusions(adrs), readManifest(root));
  if (differences.length > 0) {
    throw new Error(
      `kit-sources: the ADR projection no longer leaves out what ${ADR_MANIFEST} lists; review the ADR change, then run ${MANIFEST_COMMAND} and review the manifest's diff:\n${differences.join('\n')}`,
    );
  }

  const routes: RouteMap = new Map<string, PageRoute>();
  const files: GeneratedFile[] = [];

  // The skill references, parsed first so a link between two of them can be checked against the other's anchors.
  const references = entries.map((entry) => {
    const source = `${REFERENCES}/${entry.file}`;
    const tree = parseMarkdown(readSource(root, source));
    const body = withoutTitle(tree);
    const top = readTop(root, entry.slug);
    const anchors = referenceAnchors(tree, top ? [...top.headings, 'Reference'] : [], top?.file ?? source);
    routes.set(source, { route: `${CAPABILITIES_ROUTE}/${entry.slug}`, anchors });
    return { entry, source, title: titleOf(tree, source), body, top };
  });
  routes.set(SKILL, { route: CAPABILITIES_ROUTE });
  for (const adr of adrs)
    routes.set(`${ADRS}/${adr.slug}.md`, { route: `${ADR_ROUTE}/${adr.slug}`, anchors: adr.anchors });

  const glossarySource = readSource(root, GLOSSARY);
  const glossaryTree = parseMarkdown(glossaryMarkdown(projectGlossary(parseGlossary(glossarySource))));
  const glossaryAnchors = new Map<string, string | null>();
  for (const [anchor, slug] of anchorMap(glossaryTree, glossaryTree.children)) glossaryAnchors.set(anchor, slug);
  routes.set(GLOSSARY, { route: GLOSSARY_ROUTE, anchors: glossaryAnchors });

  const readmes = new Map<string, Root>();
  for (const kit of packages) {
    const page = { route: `${PACKAGES_ROUTE}/${kit.slug}` };
    if (kit.readme) {
      const tree = parseMarkdown(readSource(root, kit.readme));
      readmes.set(kit.id, tree);
      routes.set(kit.readme, { ...page, anchors: anchorMap(tree, withoutTitle(tree)) });
    } else {
      routes.set(`${PACKAGES}/${kit.id}/README.md`, page);
    }
    routes.set(`${PACKAGES}/${kit.id}`, routes.get(kit.readme ?? `${PACKAGES}/${kit.id}/README.md`) as PageRoute);
  }

  // Capabilities: one page per published reference, its hand-written top (when #189's file exists) above it.
  for (const reference of references) {
    const tree: Root = { type: 'root', children: reference.body };
    rewriteLinks(tree, reference.source, routes);
    const description = reference.top?.description ?? introOf(reference.body);
    const page = `content/docs/capabilities/${reference.entry.slug}.mdx`;
    check({ file: page, source: reference.source, title: reference.entry.name, description, tree });
    const body = `.generated/references/${reference.entry.slug}.md`;
    files.push({ path: body, content: stringifyMarkdown(tree) });
    const includes = [
      ...(reference.top ? [`<include>../../capabilities/${reference.entry.slug}.mdx</include>`, '## Reference'] : []),
      `<include>../../../${body}</include>`,
    ];
    files.push({
      path: page,
      content: `${frontmatter({ title: reference.entry.name, description })}${includes.join('\n\n')}\n`,
    });
  }
  files.push(capabilitiesIndex(root, routes, entries));
  const groups = (group: SkillEntry['group']) =>
    entries.filter((entry) => entry.group === group).map((entry) => entry.slug);
  files.push({
    path: 'content/docs/capabilities/meta.json',
    content: meta('Capabilities', ['index', ...groups('Capabilities'), '---Guides---', ...groups('Guides')]),
  });

  // Reference: the ADRs.
  for (const adr of adrs) {
    rewriteLinks(adr.tree, `${ADRS}/${adr.slug}.md`, routes);
    files.push(
      markdownPage({
        file: `content/docs/reference/adr/${adr.slug}.md`,
        source: `${ADRS}/${adr.slug}.md`,
        title: `ADR ${adr.slug.slice(0, 4)}: ${adr.title}`,
        description: adr.description,
        tree: adr.tree,
      }),
    );
  }
  files.push(
    markdownPage({
      file: 'content/docs/reference/adr/index.md',
      source: ADRS,
      title: 'Architecture decisions',
      description: 'The decisions that shape the Kit and are expensive to reverse, each with its amendments.',
      tree: parseMarkdown(
        `| ADR | Decision |\n|---|---|\n${adrs.map((adr) => `| ${adr.slug.slice(0, 4)} | [${cell(adr.title)}](${ADR_ROUTE}/${adr.slug}) |`).join('\n')}\n`,
      ),
    }),
  );
  files.push({
    path: 'content/docs/reference/adr/meta.json',
    content: meta('Architecture decisions', ['index', ...adrs.map((adr) => adr.slug)]),
  });

  // Reference: the glossary.
  rewriteLinks(glossaryTree, GLOSSARY, routes);
  files.push(
    markdownPage({
      file: 'content/docs/reference/glossary.md',
      source: GLOSSARY,
      title: 'Glossary',
      description: "The Kit's terms: the words its code, its messages and these pages use, and the words they avoid.",
      tree: glossaryTree,
    }),
  );

  // Reference: the packages.
  files.push(...packagePages(packages, readmes, routes));

  // Reference: the Error codes and the Host's checks.
  files.push(
    errorCodesPage(
      readErrorCodes(
        root,
        packages.map((kit) => kit.id),
      ),
      routes,
    ),
  );
  files.push(checksPage(routes));

  files.push(
    markdownPage({
      file: 'content/docs/reference/index.md',
      source: 'website/lib/kit-sources/site.ts',
      title: 'Reference',
      description: 'Every package, decision, term, Error code and Host check of the Kit.',
      tree: parseMarkdown(
        [
          `- [Packages](${PACKAGES_ROUTE}): every package, the Capabilities it serves and the packages it references.`,
          `- [Architecture decisions](${ADR_ROUTE}): the decisions that shape the Kit.`,
          `- [Glossary](${GLOSSARY_ROUTE}): the Kit's terms.`,
          `- [Error codes](${REFERENCE_ROUTE}/error-codes): the Errors the Kit's packages fail an Operation with.`,
          `- [Host checks](${REFERENCE_ROUTE}/composition-checks): the checks a Host runs when it is built and when it starts.`,
        ].join('\n'),
      ),
    }),
  );
  files.push({
    path: 'content/docs/reference/meta.json',
    content: meta('Reference', ['index', 'packages', 'adr', 'glossary', 'error-codes', 'composition-checks']),
  });
  return files;
}

function capabilitiesIndex(root: string, routes: RouteMap, entries: SkillEntry[]): GeneratedFile {
  // The skill's own front page: what holds in every Capability and the Capability table, with the published guides.
  const skill = parseMarkdown(readSource(root, SKILL));
  const body = withoutTitle(skill);
  const guides = body.findIndex((node) => node.type === 'heading' && renderedText(node) === 'Guides');
  const table = body.slice(guides).find((node) => node.type === 'table') as Table;
  const published = new Set(entries.map((entry) => entry.name));
  const rows = table.children.slice(1).filter((row) => published.has(renderedText(row.children[0])));
  const tree: Root = {
    type: 'root',
    children: [...body.slice(0, guides + 1), { ...table, children: [table.children[0], ...rows] }],
  };
  rewriteLinks(tree, SKILL, routes);
  return markdownPage({
    file: 'content/docs/capabilities/index.md',
    source: SKILL,
    title: 'Capabilities',
    description: introOf(body),
    tree,
  });
}

function packagePages(packages: KitPackage[], readmes: Map<string, Root>, routes: RouteMap): GeneratedFile[] {
  const link = (id: string) => `[${code(id)}](${PACKAGES_ROUTE}/${packages.find((kit) => kit.id === id)?.slug})`;
  const capabilities = (kit: KitPackage) =>
    kit.capabilities.map((entry) => `[${entry.name}](${CAPABILITIES_ROUTE}/${entry.slug})`).join(', ');
  const referencedBy = (kit: KitPackage) =>
    packages.filter((other) => other.references.includes(kit.id)).map((other) => other.id);
  const files: GeneratedFile[] = [];
  for (const kit of packages) {
    const source = kit.readme ?? `${PACKAGES}/${kit.id}`;
    const readme = readmes.get(kit.id);
    let tree: Root;
    let description: string;
    if (readme) {
      tree = { type: 'root', children: withoutTitle(readme) };
      description = introOf(tree.children);
    } else {
      // No README yet: the package's row of the matrix.
      const lines = [
        `- Capabilities: ${capabilities(kit) || 'none of the skill’s Capability rows names it'}`,
        `- References: ${kit.references.map(link).join(', ') || 'no other Kit package'}`,
        `- Referenced by: ${referencedBy(kit).map(link).join(', ') || 'no other Kit package'}`,
      ];
      tree = parseMarkdown(`${lines.join('\n')}\n`);
      description =
        kit.capabilities.length > 0
          ? `A package of ${kit.capabilities.map((entry) => entry.name).join(', ')}.`
          : `A package of the Kit.`;
    }
    rewriteLinks(tree, kit.readme ?? `${PACKAGES}/${kit.id}/README.md`, routes);
    files.push(
      markdownPage({
        file: `content/docs/reference/packages/${kit.slug}.md`,
        source,
        title: kit.id,
        description,
        tree,
      }),
    );
  }
  const matrix = [
    '| Package | Capabilities | References |',
    '|---|---|---|',
    ...packages.map(
      (kit) => `| ${link(kit.id)} | ${cell(capabilities(kit))} | ${kit.references.map(code).join(', ')} |`,
    ),
  ];
  files.push(
    markdownPage({
      file: 'content/docs/reference/packages/index.md',
      source: `${SKILL} and ${PACKAGES}/*/*.csproj`,
      title: 'Packages',
      description:
        "Every package of the Kit, the Capabilities the skill's table puts it in, and the Kit packages its project references.",
      tree: parseMarkdown(`${matrix.join('\n')}\n`),
    }),
  );
  files.push({
    path: 'content/docs/reference/packages/meta.json',
    content: meta('Packages', ['index', ...packages.map((kit) => kit.slug)]),
  });
  return files;
}

function errorCodesPage(codes: ErrorCode[], routes: RouteMap): GeneratedFile {
  const sections = [...new Set(codes.map((entry) => entry.package))].map((id) => {
    const rows = codes
      .filter((entry) => entry.package === id)
      .map((entry) => `| ${code(entry.code)} | ${entry.kind} | ${cell(entry.message)} | ${cell(entry.summary)} |`);
    return `## ${id}\n\n| Code | Kind | Message | When |\n|---|---|---|---|\n${rows.join('\n')}\n`;
  });
  const intro =
    'The Errors the Kit fails an Operation with, by package, read from its `*Errors` and `*ErrorCodes` types. The kind decides the HTTP status (see [problem responses](.agents/skills/pourya-kit/references/web.md#problem-responses)); a code constant names the Errors a validation returns, each with its own message.';
  const tree = parseMarkdown(`${intro}\n\n${sections.join('\n')}`);
  rewriteLinks(tree, 'error-codes.md', routes);
  return markdownPage({
    file: 'content/docs/reference/error-codes.md',
    source: `${PACKAGES}/*/*Errors.cs`,
    title: 'Error codes',
    description: 'Every Error code the Kit declares, its kind, its message and when it is returned.',
    tree,
  });
}

function checksPage(routes: RouteMap): GeneratedFile {
  // The checks' messages are interpolated strings built in many shapes, so they are not extracted: the page links to
  // the skill's sections, which name each check and quote its message.
  const tree = parseMarkdown(
    [
      "A Host refuses its own composition mistakes before it serves: its checks run when the Host is built, when it starts and once Wolverine has started, and each failure names its fix. The skill's sections list every check and quote its message:",
      '',
      "- [The Host's checks](.agents/skills/pourya-kit/references/modules.md#the-hosts-checks), for every Host: the container's validation, the composition checks and the startup checks.",
      "- [The Host's checks for messaging](.agents/skills/pourya-kit/references/messaging.md#the-hosts-checks): publications, subscriptions, routes and the message store.",
      "- [The Host's checks for RabbitMQ](.agents/skills/pourya-kit/references/rabbitmq.md#the-hosts-checks): a Host that sends or receives over a broker.",
      '',
    ].join('\n'),
  );
  rewriteLinks(tree, 'composition-checks.md', routes);
  return markdownPage({
    file: 'content/docs/reference/composition-checks.md',
    source: 'website/lib/kit-sources/site.ts',
    title: 'Host checks',
    description: 'The composition and startup checks a Host runs before it serves, and where each is listed.',
    tree,
  });
}

/** Writes the generated pages under `website/`, after emptying the folders the generator owns. */
export function writeSite(root: string, files: GeneratedFile[]): void {
  const website = resolveInside(root, 'website');
  for (const folder of GENERATED) rmSync(path.join(website, folder), { recursive: true, force: true });
  for (const file of files) {
    const target = path.join(website, file.path);
    mkdirSync(path.dirname(target), { recursive: true });
    writeFileSync(target, file.content);
  }
}
