import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { after, test } from 'node:test';
import { ASSETS_URL, SITE_ADDRESS } from './addresses.ts';
import { readPackageIds } from './packages.ts';
import { findRoot, INVENTORY, readSource } from './paths.ts';
import {
  allReadmeViolations,
  badgesOf,
  codeLines,
  coverOf,
  readmeLinkViolations,
  readmeOf,
  readmeViolations,
} from './readmes.ts';
import { buildSite } from './site.ts';

const root = findRoot();
const scratch = mkdtempSync(path.join(tmpdir(), 'kit-readmes-test-'));
after(() => rmSync(scratch, { recursive: true, force: true }));

const ids = new Set(readPackageIds(root));
const code = codeLines(root);
const cqrs = 'PouryaKit.Cqrs';
const templates = 'PouryaKit.Templates';
const readme = (id: string) => readSource(root, readmeOf(id));

// The violations of a real README with one edit: each of the template's rules has to fail on a README that passes.
function drifted(id: string, edit: (text: string) => string) {
  const text = readme(id);
  const changed = edit(text);
  assert.notEqual(changed, text, 'the edit changed nothing');
  return readmeViolations(id, changed, code, ids).join('\n');
}

function swap(from: string, to: string) {
  return (text: string) => text.replace(from, to);
}

test('every package of the inventory has a README that follows the template', () => {
  assert.deepEqual(allReadmeViolations(root), []);
});

test('a README that drifts from the shape fails', () => {
  const cover = coverOf(cqrs);
  const vercel = `![${cqrs}](${SITE_ADDRESS}/covers/${cqrs}.png)`;
  const fragments: { edit: (text: string) => string; expected: RegExp }[] = [
    { edit: swap(cover, vercel), expected: /does not open with the cover/ },
    { edit: swap(cover, cover.replace('assets-v1', 'assets-v0')), expected: /does not open with the cover/ },
    { edit: swap(cover, cover.replace(cqrs, 'PouryaKit.Web')), expected: /does not open with the cover/ },
    { edit: swap('](https://www.nuget.org/packages/PouryaKit.Cqrs)\n', ')\n'), expected: /badges are not the three/ },
    {
      edit: swap(
        badgesOf(cqrs),
        `${badgesOf(cqrs)}\n![Downloads](https://img.shields.io/nuget/dt/${cqrs}?style=flat-square)`,
      ),
      expected: /badges are not the three/,
    },
    { edit: swap(`# ${cqrs}\n`, '# Cqrs\n'), expected: /its title is not # PouryaKit\.Cqrs/ },
    { edit: swap('## Install\n', 'One paragraph too many.\n\n## Install\n'), expected: /more than one paragraph/ },
    { edit: swap('## Quick start', '## Getting started'), expected: /its sections are/ },
    { edit: swap('## When to use it', '### When to use it'), expected: /its sections are/ },
    { edit: swap('## Learn more\n', '## Learn more\n\nRead on.\n\n'), expected: /Learn more section is not one list/ },
    {
      edit: swap(`dotnet add package ${cqrs} --prerelease`, `dotnet add package ${cqrs}`),
      expected: /does not open with the command/,
    },
    {
      edit: (text) =>
        text.replace(/## When to use it\n[\s\S]*?## Quick start/, '## When to use it\n\n- X.\n\n## Quick start'),
      expected: /names no package it pairs with/,
    },
    { edit: (text) => text.replace(/> \[!WARNING\][\s\S]*?\n\n/, ''), expected: /one or two alerts/ },
    { edit: swap('> [!WARNING]', '> [!NOTE]'), expected: /one or two alerts/ },
    { edit: swap('> [!WARNING]\n> ', '> [!WARNING] '), expected: /one or two alerts/ },
    {
      edit: swap('## Learn more', '> [!TIP]\n> One.\n\n> [!TIP]\n> Two.\n\n## Learn more'),
      expected: /one or two alerts/,
    },
  ];
  for (const { edit, expected } of fragments) assert.match(drifted(cqrs, edit), expected);
});

test('HTML, a missing alt text and a fifth image fail', () => {
  const add = (fragment: string) => swap('## Install\n', `${fragment}\n\n## Install\n`);
  assert.match(drifted(cqrs, add('<picture></picture>')), /it holds HTML/);
  assert.match(drifted(cqrs, swap('## Install', 'A <b>bold</b> word.\n\n## Install')), /it holds HTML/);
  assert.match(drifted(cqrs, add(`![](${ASSETS_URL}/kit.png)`)), /has no alt text/);
  assert.match(drifted(cqrs, add(`![Hero](${SITE_ADDRESS}/hero.png)`)), /it holds 5 images/);
  assert.match(drifted(cqrs, add('[Docs][docs]\n\n[docs]: https://example.com')), /reference-style link/);
});

test('a link names a page of the site or a Kit package on nuget.org, and nothing else', () => {
  const link = (url: string) => swap('## Learn more\n\n', `## Learn more\n\n- [Link](${url})\n`);
  assert.match(drifted(cqrs, link('../PouryaKit.Web/README.md')), /it links \.\.\/PouryaKit\.Web/);
  assert.match(drifted(cqrs, link('https://example.com/docs')), /it links https:\/\/example\.com/);
  assert.match(drifted(cqrs, link(`${SITE_ADDRESS}.example.com/docs`)), /it links https:/);
  assert.match(drifted(cqrs, link('https://www.nuget.org/packages/PouryaKit.Nothing')), /names no Kit package/);
  assert.match(drifted(cqrs, link('https://www.nuget.org/packages/pouryakit.web')), /names no Kit package/);
  assert.deepEqual(readmeViolations(cqrs, link(`${SITE_ADDRESS}/docs/guides/validate`)(readme(cqrs)), code, ids), []);
});

test('a line of C# that no real file holds fails, and a comment or a blank line is excepted', () => {
  const handler = 'internal sealed class CreateProductCommandHandler(';
  assert.match(
    drifted(cqrs, swap(handler, handler.replace('CreateProductCommandHandler', 'MakeProductCommandHandler'))),
    /this line of C# is in no file/,
  );
  assert.deepEqual(readmeViolations(cqrs, swap(handler, `// A comment.\n\n${handler}`)(readme(cqrs)), code, ids), []);
  assert.match(drifted(cqrs, swap('```csharp', '```cs')), /a code block is cs, not bash or csharp/);
  assert.match(drifted(cqrs, swap('```csharp', '```')), /a code block is unlabelled/);
});

test('no README names a version: a literal is stale once the next version ships it', () => {
  assert.match(drifted(cqrs, swap('--prerelease', '--version 0.1.0-preview.0.28')), /a command names a version/);
  assert.match(
    drifted(cqrs, swap('## Install\n', '## Install\n\nAt 0.1.0-preview.0.28.\n')),
    /names the version 0\.1\.0/,
  );
  assert.match(drifted(cqrs, swap('## Install\n', 'A dash — here.\n\n## Install\n')), /an em dash/);
});

test("the templates' README keeps its install form and links the page that names the version", () => {
  const install = `dotnet new install ${templates}@<version>`;
  assert.match(
    drifted(templates, swap(install, `dotnet new install ${templates}@0.1.0-preview.0.28`)),
    /does not open with the command/,
  );
  assert.match(
    drifted(templates, (text) => text.replace(/\[Install the templates\]\(\S+?\)/, 'The install page')),
    /does not link the site's install page/,
  );
});

test('an inventory package with no README fails', () => {
  const repository = path.join(scratch, 'no-readme');
  for (const folder of ['samples', 'tests', 'src/PouryaKit.Templates/content', 'scripts/packaging']) {
    mkdirSync(path.join(repository, folder), { recursive: true });
  }
  writeFileSync(path.join(repository, INVENTORY), JSON.stringify({ packages: [{ id: cqrs }] }));
  assert.deepEqual(allReadmeViolations(repository), [
    'src/PouryaKit.Cqrs/README.md does not exist, and every inventory package has one',
  ]);
});

test('a link to the site must name a page of the export, and an anchor of that page', () => {
  const repository = path.join(scratch, 'links');
  mkdirSync(path.join(repository, 'scripts/packaging'), { recursive: true });
  mkdirSync(path.join(repository, 'src', cqrs), { recursive: true });
  writeFileSync(path.join(repository, INVENTORY), JSON.stringify({ packages: [{ id: cqrs }] }));
  const links = [
    `${SITE_ADDRESS}/docs/guides/validate`,
    `${SITE_ADDRESS}/docs/guides/validate#the-rule`,
    `${SITE_ADDRESS}/docs/guides/missing`,
    `${SITE_ADDRESS}/docs/guides/validate#missing`,
    `${SITE_ADDRESS}/docs/guides/validate#data-only`,
    `${SITE_ADDRESS}/`,
    'https://www.nuget.org/packages/PouryaKit.Cqrs',
  ];
  writeFileSync(
    path.join(repository, readmeOf(cqrs)),
    links.map((url, index) => `- [Link ${index}](${url})`).join('\n'),
  );
  const out = path.join(scratch, 'links-out');
  mkdirSync(path.join(out, 'docs/guides/validate'), { recursive: true });
  writeFileSync(path.join(out, 'index.html'), '<h1>Home</h1>');
  writeFileSync(
    path.join(out, 'docs/guides/validate/index.html'),
    '<h2 id="the-rule">The rule</h2><p data-id="data-only">Not an anchor.</p>',
  );

  assert.deepEqual(readmeLinkViolations(repository, out), [
    `src/PouryaKit.Cqrs/README.md: ${SITE_ADDRESS}/docs/guides/missing names no page the site generates`,
    `src/PouryaKit.Cqrs/README.md: ${SITE_ADDRESS}/docs/guides/validate#missing names an anchor its page does not have`,
    `src/PouryaKit.Cqrs/README.md: ${SITE_ADDRESS}/docs/guides/validate#data-only names an anchor its page does not have`,
  ]);
});

test("a package's page renders what follows its title, its alerts as the site's Callout", () => {
  const files = buildSite(root);
  const page = (slug: string) =>
    files.find((file) => file.path === `content/docs/reference/packages/${slug}.mdx`)?.content ?? '';
  const text = page('pouryakit-realtime');
  for (const external of ['img.shields.io', 'raw.githubusercontent.com', 'License: MIT', '[!WARNING]', '[!TIP]']) {
    assert.ok(!text.includes(external), external);
  }
  assert.match(text, /^description: "Server-sent events pushed from Notification Handlers\."$/m);
  assert.match(text, /<Callout type="warn">\n {2}Delivery to clients is at most once\./);
  assert.match(text, /<Callout type="tip">\n {2}Keep each push a small fact/);
  assert.match(text, /\[Realtime\]\(\/docs\/capabilities\/realtime\)/);
  assert.ok(!text.includes(`${SITE_ADDRESS}/docs`));
  assert.ok(readme('PouryaKit.Realtime').includes(`${SITE_ADDRESS}/docs/capabilities/realtime`));
});

test('the packed project URL and the READMEs hold the addresses of addresses.ts', () => {
  const targets = readFileSync(path.join(root, 'Directory.Build.targets'), 'utf8');
  assert.deepEqual(
    [...targets.matchAll(/<PackageProjectUrl>(.*)<\/PackageProjectUrl>/g)].map((match) => match[1]),
    [SITE_ADDRESS],
  );
  const main = readFileSync(path.join(root, 'README.md'), 'utf8');
  assert.ok(main.startsWith(`![PouryaKit](${ASSETS_URL}/kit.png)\n`));
  assert.ok(main.includes(`[Docs](${SITE_ADDRESS})`));
  assert.ok(main.includes(`[Install](${SITE_ADDRESS}/docs/get-started/install-templates)`));
});

test('the check reads every root a snippet may quote, and no build output', () => {
  const repository = path.join(scratch, 'code');
  for (const folder of ['samples/a', 'tests/b', 'src/PouryaKit.Templates/content/c', 'tests/b/obj', 'tests/b/bin']) {
    mkdirSync(path.join(repository, folder), { recursive: true });
  }
  writeFileSync(path.join(repository, 'samples/a/A.cs'), '    var sample = 1;\n');
  writeFileSync(path.join(repository, 'tests/b/B.cs'), 'var test = 2;\n');
  writeFileSync(path.join(repository, 'src/PouryaKit.Templates/content/c/C.cs'), 'var template = 3;\n');
  writeFileSync(path.join(repository, 'tests/b/obj/D.cs'), 'var generated = 4;\n');
  writeFileSync(path.join(repository, 'tests/b/bin/E.cs'), 'var built = 5;\n');
  writeFileSync(path.join(repository, 'tests/b/F.md'), 'var prose = 6;\n');
  const lines = codeLines(repository);
  for (const line of ['var sample = 1;', 'var test = 2;', 'var template = 3;']) assert.ok(lines.has(line), line);
  for (const line of ['var generated = 4;', 'var built = 5;', 'var prose = 6;']) assert.ok(!lines.has(line), line);
});
