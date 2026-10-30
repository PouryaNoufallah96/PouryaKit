import { existsSync, readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import type { Blockquote, Heading, Paragraph, Root, RootContent, Text } from 'mdast';
import { visit } from 'unist-util-visit';
import { ASSETS_URL, SITE_ADDRESS } from './addresses.ts';
import { parseVerbatim, renderedText } from './markdown.ts';
import { readPackageIds } from './packages.ts';
import { PACKAGES, readSource, resolveInside, sourceExists } from './paths.ts';

// A package's README ships inside its version and only another publication can change it, so each rule here fails a
// README before the owner publishes it: the shape nuget.org renders, the links a reader follows and the code they copy.
// The site's own routes and anchors are read from the export, the only place they are certain (`readmeLinkViolations`).

const TEMPLATES = 'PouryaKit.Templates';
const NUGET_PACKAGES = 'https://www.nuget.org/packages/';
const SECTIONS = ['Install', 'When to use it', 'Quick start', 'What you get', 'Learn more'];
const FENCES = ['bash', 'csharp'];
const CODE_ROOTS = ['samples', 'tests', 'src/PouryaKit.Templates/content'];
const ALERT = /^\[!(TIP|WARNING)\]\n/;
const CALLOUT_TYPES = { TIP: 'tip', WARNING: 'warn' };

/** The file of a package's README. */
export function readmeOf(id: string): string {
  return `${PACKAGES}/${id}/README.md`;
}

/** The cover a README opens with: an image nuget.org renders, from the public repository's tag. */
export function coverOf(id: string): string {
  return `![${id}](${ASSETS_URL}/covers/${id}.png)`;
}

/** The README's badges, on the one image host nuget.org allows for them; no downloads badge, which reads 0 on a new package. */
export function badgesOf(id: string): string {
  return [
    `[![NuGet](https://img.shields.io/nuget/vpre/${id}?style=flat-square&logo=nuget&color=615fff)](${NUGET_PACKAGES}${id})`,
    '![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square&logo=dotnet)',
    '![License: MIT](https://img.shields.io/badge/license-MIT-615fff?style=flat-square)',
  ].join('\n');
}

function installOf(id: string): string {
  return id === TEMPLATES ? `dotnet new install ${TEMPLATES}@<version>` : `dotnet add package ${id} --prerelease`;
}

/** Every trimmed line of the Kit's real C#, which a README's snippets must quote; a generated `bin` or `obj` file is no source. */
export function codeLines(root: string): Set<string> {
  const lines = new Set<string>();
  const read = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const full = path.join(directory, entry.name);
      if (entry.isDirectory() && entry.name !== 'bin' && entry.name !== 'obj') read(full);
      else if (entry.isFile() && entry.name.endsWith('.cs')) {
        for (const line of readFileSync(full, 'utf8').split('\n')) lines.add(line.trim());
      }
    }
  };
  for (const folder of CODE_ROOTS) read(resolveInside(root, folder));
  return lines;
}

function alertOf(node: RootContent): keyof typeof CALLOUT_TYPES | undefined {
  const first = node.type === 'blockquote' ? node.children[0] : undefined;
  const text = first?.type === 'paragraph' ? first.children[0] : undefined;
  return text?.type === 'text' ? (ALERT.exec(text.value)?.[1] as keyof typeof CALLOUT_TYPES) : undefined;
}

/** A README's alerts as the site's Callout, since no reader of the page's Markdown treats `[!TIP]` as more than text. */
export function withCallouts(nodes: RootContent[]): RootContent[] {
  return nodes.map((node) => {
    const kind = alertOf(node);
    if (!kind) return node;
    const [first, ...rest] = (node as Blockquote).children;
    const [text, ...inline] = (first as Paragraph).children;
    const value = (text as Text).value.replace(ALERT, '');
    const paragraph = { ...first, children: [...(value ? [{ ...text, value }] : []), ...inline] };
    return {
      type: 'mdxJsxFlowElement',
      name: 'Callout',
      attributes: [{ type: 'mdxJsxAttribute', name: 'type', value: CALLOUT_TYPES[kind] }],
      children: [paragraph, ...rest],
    } as unknown as RootContent;
  });
}

/** The nodes of a README after its title: what the site renders, since the cover and the badges above it are nuget.org's. */
export function afterTitle(tree: Root): RootContent[] {
  return tree.children.slice(tree.children.findIndex((node) => node.type === 'heading' && node.depth === 1) + 1);
}

function sectionsOf(nodes: RootContent[]): Map<string, RootContent[]> {
  const sections = new Map<string, RootContent[]>();
  let current: RootContent[] | undefined;
  for (const node of nodes) {
    if (node.type === 'heading') {
      current = [];
      sections.set(renderedText(node), current);
    } else current?.push(node);
  }
  return sections;
}

function shapeViolations(id: string, tree: Root, text: string): string[] {
  const written = (node?: RootContent) =>
    node ? text.slice(node.position?.start.offset, node.position?.end.offset) : undefined;
  const [cover, badges, title, intro] = tree.children;
  const violations: string[] = [];
  if (written(cover) !== coverOf(id)) violations.push(`it does not open with the cover ${coverOf(id)}`);
  if (written(badges) !== badgesOf(id)) violations.push('its badges are not the three of the template');
  if (title?.type !== 'heading' || title.depth !== 1 || renderedText(title) !== id) {
    violations.push(`its title is not # ${id}`);
  }
  if (intro?.type !== 'paragraph') violations.push('its title is not followed by one paragraph');

  const headings = tree.children.filter((node): node is Heading => node.type === 'heading').slice(1);
  const names = headings.map((heading) => `${'#'.repeat(heading.depth)} ${renderedText(heading)}`);
  const expected = SECTIONS.map((section) => `## ${section}`);
  if (names.join('\n') !== expected.join('\n')) {
    violations.push(`its sections are ${names.join(', ')}, not ${expected.join(', ')}`);
    return violations;
  }
  // The one paragraph: the first section's heading comes right after it.
  if (tree.children[4]?.type !== 'heading') violations.push('its title is followed by more than one paragraph');

  const sections = sectionsOf(tree.children.slice(4));
  const install = sections.get('Install') ?? [];
  const command = install[0];
  if (command?.type !== 'code' || command.lang !== 'bash' || command.value !== installOf(id)) {
    violations.push(`its Install section does not open with the command ${installOf(id)}`);
  }
  if (id === TEMPLATES) {
    const named = install
      .slice(1)
      .some((node) => writesLink(node, `${SITE_ADDRESS}/docs/get-started/install-templates`));
    if (!named) violations.push("its Install section does not link the site's install page");
  } else if (install.length !== 1) violations.push('its Install section holds more than the command');

  const when = sections.get('When to use it') ?? [];
  if (when[0]?.type !== 'list') violations.push('its When to use it section does not open with a list');
  if (!when.some((node) => writesLink(node, NUGET_PACKAGES))) {
    violations.push('its When to use it section names no package it pairs with');
  }

  const fences = (sections.get('Quick start') ?? []).filter((node) => node.type === 'code');
  if (id !== TEMPLATES && !fences.some((node) => node.lang === 'csharp')) {
    violations.push('its Quick start section holds no C#');
  }
  if (id === TEMPLATES && fences.length === 0) violations.push('its Quick start section holds no command');

  const [list, ...alerts] = sections.get('What you get') ?? [];
  if (list?.type !== 'list') violations.push('its What you get section does not open with a list');
  if (alerts.length < 1 || alerts.length > 2 || !alerts.every(alertOf)) {
    violations.push('its What you get section does not end in one or two alerts, [!TIP] or [!WARNING]');
  }

  const learn = sections.get('Learn more') ?? [];
  if (learn.length !== 1 || learn[0].type !== 'list') violations.push('its Learn more section is not one list');
  return violations;
}

function writesLink(node: RootContent, prefix: string): boolean {
  let found = false;
  visit(node, 'link', (link) => {
    found ||= link.url.startsWith(prefix);
  });
  return found;
}

function markupViolations(tree: Root): string[] {
  const violations: string[] = [];
  let images = 0;
  visit(tree, (node) => {
    if (node.type === 'html') violations.push(`it holds HTML, which nuget.org does not render: ${node.value}`);
    if (node.type === 'image') images += 1;
    if (node.type === 'image' && !node.alt) violations.push(`the image ${node.url} has no alt text`);
    if (node.type === 'linkReference' || node.type === 'imageReference' || node.type === 'definition') {
      violations.push('it uses a reference-style link; write it inline');
    }
  });
  // The cover and the three badges: their URLs are held by equality, so a fifth image is the only one left to fail.
  if (images !== 4) violations.push(`it holds ${images} images, not the cover and the three badges`);
  return violations;
}

function linkViolations(tree: Root, ids: ReadonlySet<string>): string[] {
  const violations: string[] = [];
  visit(tree, 'link', ({ url }) => {
    if (url.startsWith(NUGET_PACKAGES)) {
      if (!ids.has(url.slice(NUGET_PACKAGES.length))) violations.push(`it links ${url}, which names no Kit package`);
    } else if (URL.parse(url)?.origin !== SITE_ADDRESS) {
      violations.push(`it links ${url}: a link names a page of ${SITE_ADDRESS} or a Kit package on nuget.org`);
    }
  });
  return violations;
}

function codeViolations(tree: Root, code: ReadonlySet<string>): string[] {
  const violations: string[] = [];
  visit(tree, 'code', (node) => {
    if (!FENCES.includes(node.lang ?? ''))
      violations.push(`a code block is ${node.lang ?? 'unlabelled'}, not bash or csharp`);
    if (node.lang === 'bash' && node.value.includes('--version')) {
      violations.push('a command names a version, which stales once the next version ships this README');
    }
    if (node.lang !== 'csharp') return;
    for (const line of node.value.split('\n').map((text) => text.trim())) {
      if (line !== '' && !line.startsWith('//') && !code.has(line)) {
        violations.push(`this line of C# is in no file of ${CODE_ROOTS.join(', ')}: ${line}`);
      }
    }
  });
  return violations;
}

function textViolations(tree: Root): string[] {
  const text = renderedText(tree);
  return [
    ...(text.match(/\d+\.\d+\.\d+\S*/g) ?? []).map(
      (version) => `it names the version ${version}, which stales once the next version ships this README`,
    ),
    ...(text.includes('—') ? ['it holds an em dash'] : []),
  ];
}

/** Everything wrong with one package's README, as written. */
export function readmeViolations(
  id: string,
  text: string,
  code: ReadonlySet<string>,
  ids: ReadonlySet<string>,
): string[] {
  const tree = parseVerbatim(text);
  return [
    ...shapeViolations(id, tree, text),
    ...markupViolations(tree),
    ...linkViolations(tree, ids),
    ...codeViolations(tree, code),
    ...textViolations(tree),
  ].map((violation) => `${readmeOf(id)}: ${violation}`);
}

/** Every inventory package's README against its rules. */
export function allReadmeViolations(root: string): string[] {
  const ids = readPackageIds(root);
  const known = new Set(ids);
  const code = codeLines(root);
  return ids.flatMap((id) =>
    sourceExists(root, readmeOf(id))
      ? readmeViolations(id, readSource(root, readmeOf(id)), code, known)
      : [`${readmeOf(id)} does not exist, and every inventory package has one`],
  );
}

/** Each README link to the site against the export: `<route>/index.html` exists, and it declares the link's anchor. */
export function readmeLinkViolations(root: string, out: string): string[] {
  const violations: string[] = [];
  for (const id of readPackageIds(root)) {
    visit(parseVerbatim(readSource(root, readmeOf(id))), 'link', ({ url }) => {
      const link = URL.parse(url);
      if (link?.origin !== SITE_ADDRESS) return;
      const page = path.join(out, link.pathname, 'index.html');
      if (!existsSync(page)) {
        violations.push(`${readmeOf(id)}: ${url} names no page the site generates`);
      } else if (link.hash && !readFileSync(page, 'utf8').includes(` id="${decodeURIComponent(link.hash.slice(1))}"`)) {
        violations.push(`${readmeOf(id)}: ${url} names an anchor its page does not have`);
      }
    });
  }
  return violations;
}
