import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import type { Root, TableRow } from 'mdast';
import { visit } from 'unist-util-visit';
import { ASSETS_URL, MARKETPLACE, OWNER, PLUGIN, PUBLIC_REPOSITORY, SITE_ADDRESS } from './addresses.ts';
import { type RouteMap, resolveLink, rewriteLinks } from './links.ts';
import {
  firstSentence,
  headingSlugs,
  nodeViolations,
  parseMarkdown,
  parseSource,
  sourceFrontmatter,
  stringifyMarkdown,
  urlsOf,
} from './markdown.ts';
import { readPackageIds } from './packages.ts';
import { listDirectory, REFERENCES, readSource, resolveInside, SKILL } from './paths.ts';
import { MAINTAINER_GUIDES, publishedReferences } from './references.ts';
import { copyViolations, describe, renderedTextViolations, type Violation } from './rules.ts';

// The public repository's tree: the Kit's skill as a Claude Code plugin marketplace and an `npx skills` source, the
// covers, a front-door README and the licence. The skill is projected as the site projects it: the published references
// only, each link rewritten by the site's rule and every file held to the site's build-time rules, so the copy can hold
// nothing the site could not. A committed manifest of every file's sha256 is what was reviewed; the check regenerates the
// tree and fails on any difference, and the remote check compares the published commit with it.

/**
 * The front-door README, a template the sync fills (`{{site}}`, `{{repository}}`, `{{assets}}`, `{{marketplace}}`,
 * `{{plugin}}`). It is no `.md` file: its relative links name files of the public repository, which `check-docs.py`
 * would resolve in this one.
 */
export const README_TEMPLATE = 'website/distribution/README.md.template';

/** Every file of the tree and its sha256, one `<sha256>  <path>` line each, as `sha256sum` writes them. */
export const MANIFEST = 'website/distribution/manifest.sha256';

/** Where `pnpm --dir website covers` writes the Kit's and the packages' covers. */
const COVERS = 'website/covers/out';

/** The default directory the tree is written to; the sync owns it and empties it first. */
export const OUTPUT = 'website/distribution/out';

/** The repository the remote check reads by default. */
export const PUBLIC_REMOTE = `https://github.com/${PUBLIC_REPOSITORY}.git`;

const SKILL_FOLDER = path.posix.dirname(SKILL);
const AGENT_METADATA = `${SKILL_FOLDER}/agents/openai.yaml`;
const ICON = 'src/icon.png';
const LICENSE = 'LICENSE';
const COPY = `skills/${PLUGIN}`;

export type TreeFile = { path: string; content: Buffer };

/** File paths to their sha256, in hex. */
export type Manifest = Map<string, string>;

function file(target: string, content: string | Buffer): TreeFile {
  return { path: target, content: typeof content === 'string' ? Buffer.from(content) : content };
}

function fail(where: string, violations: Violation[]) {
  if (violations.length > 0) throw new Error(`kit-sources: ${where}: ${describe(violations)}`);
}

// What the skill's folder holds: a file the sync does not know fails, so nothing is published or left out unseen.
// `publishedReferences` holds `references/` to its two lists the same way.
function expectEntries(root: string, folder: string, expected: string[]) {
  const present = listDirectory(root, folder);
  if (present.join('\n') !== [...expected].sort().join('\n')) {
    throw new Error(
      `kit-sources: ${folder} holds ${present.join(', ')}; the public repository's sync knows ${expected.join(', ')} (lib/kit-sources/distribution.ts)`,
    );
  }
}

function frontmatterField(frontmatter: string, pattern: RegExp, field: string): string {
  const value = pattern.exec(frontmatter)?.[1];
  if (!value) throw new Error(`kit-sources: ${SKILL} has no ${field} in its frontmatter, in the shape the sync reads`);
  return value;
}

/** The skill's name, description and `metadata.version`, from SKILL.md's frontmatter. */
export function skillMetadata(root: string): { name: string; description: string; version: string } {
  const frontmatter = sourceFrontmatter(readSource(root, SKILL));
  return {
    name: frontmatterField(frontmatter, /^name: ([a-z0-9-]+)$/m, 'name'),
    description: frontmatterField(frontmatter, /^description: (\S.*)$/m, 'description'),
    version: frontmatterField(frontmatter, /^metadata:\n(?: {2}\S.*\n)*? {2}version: "([^"]+)"$/m, 'metadata.version'),
  };
}

/** Removes each table row that links a maintainer guide: an agent reading the copy cannot open one. */
function dropMaintainerRows(tree: Root) {
  const linksGuide = (row: TableRow) => {
    let found = false;
    visit(row, 'link', (link) => {
      found ||= MAINTAINER_GUIDES.some((guide) => path.posix.basename(link.url.split('#')[0]) === guide);
    });
    return found;
  };
  for (const node of tree.children) {
    if (node.type === 'table') node.children = node.children.filter((row) => !linksGuide(row));
  }
}

/**
 * SKILL.md and the published references, projected: each link rewritten by the site's rule, a link to another file of
 * the copy written relative to it and checked against that file's headings, and every other link into the repository
 * (the ADRs, the docs) its own text. SKILL.md keeps its frontmatter, which agents read, and loses the rows of the
 * maintainer guides, which the copy does not hold.
 */
function skillCopy(root: string): TreeFile[] {
  expectEntries(root, SKILL_FOLDER, ['SKILL.md', 'agents', 'references']);
  expectEntries(root, path.posix.dirname(AGENT_METADATA), [path.posix.basename(AGENT_METADATA)]);
  const sources = [SKILL, ...publishedReferences(root).map((reference) => `${REFERENCES}/${reference}`)];
  const copyOf = (source: string) => `${COPY}/${path.posix.relative(SKILL_FOLDER, source)}`;
  const texts = new Map(sources.map((source) => [source, readSource(root, source)]));
  const trees = new Map(sources.map((source) => [source, parseSource(texts.get(source) as string)]));
  // A file of the copy keeps its headings, so its anchors are GitHub's slugs of its own.
  const routes: RouteMap = new Map(
    sources.map((source) => {
      const slugs = [...headingSlugs((trees.get(source) as Root).children).values()];
      return [source, { route: copyOf(source), anchors: new Map(slugs.map((slug) => [slug, slug])) }];
    }),
  );
  return sources.map((source) => {
    const tree = trees.get(source) as Root;
    if (source === SKILL) dropMaintainerRows(tree);
    rewriteLinks(tree, source, routes, copyOf(source));
    const frontmatter = sourceFrontmatter(texts.get(source) as string);
    const values: string[] = [frontmatter];
    visit(tree, (node) => {
      if ('value' in node && typeof node.value === 'string') values.push(node.value);
    });
    fail(`${copyOf(source)} (from ${source})`, [
      ...nodeViolations(tree),
      ...renderedTextViolations(frontmatter),
      ...values.flatMap(copyViolations),
    ]);
    return file(copyOf(source), `${frontmatter}${frontmatter ? '\n' : ''}${stringifyMarkdown(tree)}`);
  });
}

/** The Claude Code marketplace: one plugin, the skill, from the repository's root, its version the skill's own. */
function marketplace(root: string): TreeFile {
  const skill = skillMetadata(root);
  if (skill.name !== PLUGIN) {
    throw new Error(
      `kit-sources: ${SKILL} is named ${skill.name}, but the public repository publishes it as ${PLUGIN}`,
    );
  }
  const manifest = {
    name: MARKETPLACE,
    description: "PouryaKit's AI skill: how to build a .NET modular monolith on the Kit's packages.",
    owner: { name: OWNER },
    plugins: [
      {
        name: PLUGIN,
        source: './',
        description: firstSentence(skill.description),
        version: skill.version,
        strict: false,
        skills: [`./${COPY}`],
      },
    ],
  };
  const text = `${JSON.stringify(manifest, null, 2)}\n`;
  fail('.claude-plugin/marketplace.json', renderedTextViolations(text));
  return file('.claude-plugin/marketplace.json', text);
}

/** The values the README template names. */
const README_VALUES: Record<string, string> = {
  site: SITE_ADDRESS,
  repository: PUBLIC_REPOSITORY,
  assets: ASSETS_URL,
  marketplace: MARKETPLACE,
  plugin: PLUGIN,
};

/** The README template filled: a name it does not know, or a brace pair left over, fails. */
export function fillReadme(template: string): string {
  const text = template.replace(/\{\{(\w+)\}\}/g, (_, name: string) => {
    const value = README_VALUES[name];
    if (value === undefined) {
      throw new Error(
        `kit-sources: ${README_TEMPLATE} names {{${name}}}; the sync fills ${Object.keys(README_VALUES).join(', ')}`,
      );
    }
    return value;
  });
  if (/\{\{|\}\}/.test(text)) throw new Error(`kit-sources: ${README_TEMPLATE} has a {{ or }} the sync does not fill`);
  return text;
}

/** The front door, held to the rules, its relative links to files of the tree. */
function readme(root: string, paths: string[]): TreeFile {
  const text = fillReadme(readSource(root, README_TEMPLATE));
  const tree = parseMarkdown(text);
  fail('README.md', nodeViolations(tree));
  for (const url of urlsOf(tree)) {
    const target = resolveLink('README.md', url)?.target;
    if (target !== undefined && !paths.some((entry) => entry === target || entry.startsWith(`${target}/`))) {
      throw new Error(`kit-sources: ${README_TEMPLATE} links ${url}, which is no file of the public repository`);
    }
  }
  return file('README.md', text);
}

function copied(root: string, source: string, target: string): TreeFile {
  const text = readSource(root, source);
  fail(`${target} (from ${source})`, renderedTextViolations(text));
  return file(target, text);
}

/** The Kit's cover, every package's cover in the inventory's order and the packages' shared icon. */
function assets(root: string, covers: string): TreeFile[] {
  const png = (source: string) => {
    if (!existsSync(source)) throw new Error(`kit-sources: ${source} is missing; run pnpm --dir website covers first`);
    return readFileSync(source);
  };
  return [
    file('assets/kit.png', png(path.join(covers, 'kit.png'))),
    ...readPackageIds(root).map((id) => file(`assets/covers/${id}.png`, png(path.join(covers, 'covers', `${id}.png`)))),
    file('assets/icon.png', readFileSync(resolveInside(root, ICON))),
  ];
}

/** Every file of the public repository, in path order. `covers` is where the covers were typeset. */
export function assembleTree(root: string, covers = resolveInside(root, COVERS)): TreeFile[] {
  const files = [
    marketplace(root),
    ...skillCopy(root),
    copied(root, AGENT_METADATA, `${COPY}/agents/openai.yaml`),
    ...assets(root, covers),
    copied(root, LICENSE, 'LICENSE'),
  ];
  files.push(readme(root, [...files.map((entry) => entry.path), 'README.md']));
  return files.sort((a, b) => (a.path < b.path ? -1 : 1));
}

export function sha256(content: Buffer): string {
  return createHash('sha256').update(content).digest('hex');
}

export function manifestOf(files: TreeFile[]): Manifest {
  return new Map(files.map((entry) => [entry.path, sha256(entry.content)]));
}

export function formatManifest(manifest: Manifest): string {
  return [...manifest]
    .sort(([a], [b]) => (a < b ? -1 : 1))
    .map(([target, hash]) => `${hash}  ${target}\n`)
    .join('');
}

export function parseManifest(text: string): Manifest {
  const manifest: Manifest = new Map();
  for (const line of text.split('\n').filter((entry) => entry !== '')) {
    const match = /^([0-9a-f]{64}) {2}(\S.*)$/.exec(line);
    if (!match || manifest.has(match[2]))
      throw new Error(`kit-sources: ${MANIFEST} has a line it cannot read: ${line}`);
    manifest.set(match[2], match[1]);
  }
  return manifest;
}

export function readManifest(root: string): Manifest {
  return parseManifest(readSource(root, MANIFEST));
}

/** How `actual` differs from `expected`, in both directions: a file missing, a file changed and a file extra. */
export function manifestDifferences(expected: Manifest, actual: Manifest): string[] {
  const differences: string[] = [];
  for (const [target, hash] of expected) {
    if (!actual.has(target)) differences.push(`missing: ${target}`);
    else if (actual.get(target) !== hash) differences.push(`changed: ${target}`);
  }
  for (const target of actual.keys()) if (!expected.has(target)) differences.push(`extra: ${target}`);
  return differences.sort((a, b) => (a < b ? -1 : 1));
}

function git(args: string[]): Buffer {
  try {
    return execFileSync('git', args, { maxBuffer: 512 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (error) {
    const stderr = (error as { stderr?: Buffer }).stderr?.toString().trim();
    throw new Error(`kit-sources: git ${args.join(' ')} failed${stderr ? `: ${stderr}` : ''}`);
  }
}

export type RemoteCheck = { branch?: string; commit?: string; problems: string[] };

/**
 * What a remote publishes, against the manifest: the commit `tag` names, which must be the default branch's (what
 * `npx skills add` and the plugin marketplace read), fetched into a temporary directory, with its complete file set and
 * every file's sha256 compared. Each problem found is returned; none means the remote publishes the reviewed tree.
 */
function remoteHead(remote: string, tag: string): { branch?: string; head?: string; commit?: string } {
  let branch: string | undefined;
  let head: string | undefined;
  let tagged: string | undefined;
  let peeled: string | undefined;
  for (const line of git(['ls-remote', '--symref', remote]).toString().split('\n')) {
    const symbolic = /^ref: (\S+)\tHEAD$/.exec(line);
    if (symbolic) branch = symbolic[1].replace(/^refs\/heads\//, '');
    const [object, ref] = line.split('\t');
    if (ref === 'HEAD' && !symbolic) head = object;
    if (ref === `refs/tags/${tag}`) tagged = object;
    if (ref === `refs/tags/${tag}^{}`) peeled = object;
  }
  return { branch, head, commit: peeled ?? tagged };
}

function publishedTree(directory: string, commit: string): { problems: string[]; files: Manifest } {
  const problems: string[] = [];
  const files: Manifest = new Map();
  for (const entry of git(['-C', directory, 'ls-tree', '-r', '-z', '--full-tree', commit]).toString().split('\0')) {
    if (entry === '') continue;
    const tab = entry.indexOf('\t');
    const [mode, type, object] = entry.slice(0, tab).split(' ');
    const target = entry.slice(tab + 1);
    if (type !== 'blob' || (mode !== '100644' && mode !== '100755')) {
      problems.push(`${target} is not a regular file (${mode} ${type})`);
      continue;
    }
    files.set(target, sha256(git(['-C', directory, 'cat-file', 'blob', object])));
  }
  return { problems, files };
}

function fetchedProblems(remote: string, tag: string, commit: string, manifest: Manifest): string[] {
  const directory = mkdtempSync(path.join(tmpdir(), 'kit-distribution-'));
  try {
    git(['init', '--quiet', directory]);
    git(['-C', directory, 'fetch', '--quiet', '--no-tags', '--depth', '1', remote, `refs/tags/${tag}`]);
    const fetched = git(['-C', directory, 'rev-parse', 'FETCH_HEAD^{commit}']).toString().trim();
    const moved = fetched === commit ? [] : [`${tag} moved from ${commit} to ${fetched} while it was read`];
    const published = publishedTree(directory, fetched);
    return [...moved, ...published.problems, ...manifestDifferences(manifest, published.files)];
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
}

export function verifyRemote(remote: string, tag: string, manifest: Manifest): RemoteCheck {
  if (remote.startsWith('-') || tag.startsWith('-'))
    throw new Error('kit-sources: a remote or a tag cannot start with -');
  const { branch, head, commit } = remoteHead(remote, tag);
  if (!commit) return { branch, problems: [`${remote} has no tag ${tag}`] };
  const drifted =
    head === commit
      ? []
      : [
          `the default branch ${branch ?? '(none)'} is at ${head ?? 'no commit'}, not at ${commit}, which ${tag} tags: npx skills add and the plugin marketplace read the default branch`,
        ];
  return { branch, commit, problems: [...drifted, ...fetchedProblems(remote, tag, commit, manifest)] };
}
