import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { appendFileSync, cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { devNull, tmpdir } from 'node:os';
import path from 'node:path';
import { after, test } from 'node:test';
import Slugger from 'github-slugger';
import { visit } from 'unist-util-visit';
import { MARKETPLACE, PLUGIN, PUBLIC_REPOSITORY } from './addresses.ts';
import {
  assembleTree,
  fillReadme,
  formatManifest,
  manifestDifferences,
  manifestOf,
  parseManifest,
  README_TEMPLATE,
  sha256,
  skillMetadata,
  type TreeFile,
  verifyRemote,
} from './distribution.ts';
import { resolveLink } from './links.ts';
import { parseMarkdown, renderedText, sourceFrontmatter } from './markdown.ts';
import { readPackageIds } from './packages.ts';
import { findRoot, INVENTORY, SKILL } from './paths.ts';
import { PUBLISHED_REFERENCES } from './references.ts';

const root = findRoot();
const scratch = mkdtempSync(path.join(tmpdir(), 'kit-distribution-test-'));
after(() => rmSync(scratch, { recursive: true, force: true }));

// Covers as `pnpm --dir website covers` writes them, each a few distinct bytes: the tests need no typesetting.
const covers = path.join(scratch, 'covers');
mkdirSync(path.join(covers, 'covers'), { recursive: true });
writeFileSync(path.join(covers, 'kit.png'), 'kit');
for (const id of readPackageIds(root)) writeFileSync(path.join(covers, 'covers', `${id}.png`), id);

const tree = assembleTree(root, covers);
const text = (files: TreeFile[], target: string) => files.find((entry) => entry.path === target)?.content.toString();

// A repository holding only what the sync reads, so a test can plant a source without touching the real one.
function fixture(name: string): string {
  const directory = path.join(scratch, name);
  for (const source of [path.dirname(SKILL), INVENTORY, 'LICENSE', README_TEMPLATE, 'src/icon.png']) {
    cpSync(path.join(root, source), path.join(directory, source), { recursive: true });
  }
  return directory;
}

test('the tree holds the marketplace, the skill with its published references only, the covers, the README and the licence', () => {
  const expected = [
    '.claude-plugin/marketplace.json',
    'LICENSE',
    'README.md',
    'assets/icon.png',
    'assets/kit.png',
    ...readPackageIds(root).map((id) => `assets/covers/${id}.png`),
    `skills/${PLUGIN}/SKILL.md`,
    `skills/${PLUGIN}/agents/openai.yaml`,
    ...PUBLISHED_REFERENCES.map((reference) => `skills/${PLUGIN}/references/${reference}`),
  ];
  assert.deepEqual(new Set(tree.map((entry) => entry.path)), new Set(expected));
  assert.equal(tree.length, expected.length);
  assert.equal(text(tree, 'assets/covers/PouryaKit.Cqrs.png'), 'PouryaKit.Cqrs');
  assert.deepEqual(
    tree.find((entry) => entry.path === 'assets/icon.png')?.content,
    readFileSync(path.join(root, 'src/icon.png')),
  );
  assert.equal(text(tree, 'LICENSE'), readFileSync(path.join(root, 'LICENSE'), 'utf8'));
});

test('the marketplace publishes the skill from the repository root under its names, at the skill version', () => {
  const marketplace = JSON.parse(text(tree, '.claude-plugin/marketplace.json') ?? '');
  assert.equal(marketplace.name, MARKETPLACE);
  assert.ok(marketplace.owner.name);
  assert.equal(marketplace.plugins.length, 1);
  const [plugin] = marketplace.plugins;
  assert.deepEqual(
    { name: plugin.name, source: plugin.source, strict: plugin.strict, skills: plugin.skills },
    { name: 'pourya-kit', source: './', strict: false, skills: ['./skills/pourya-kit'] },
  );
  assert.equal(plugin.version, skillMetadata(root).version);

  const bumped = fixture('version');
  const skill = path.join(bumped, SKILL);
  writeFileSync(skill, readFileSync(skill, 'utf8').replace(/^(\s+version: )"[^"]+"$/m, '$1"9.9.9"'));
  const copy = JSON.parse(text(assembleTree(bumped, covers), '.claude-plugin/marketplace.json') ?? '');
  assert.equal(copy.plugins[0].version, '9.9.9');
});

test("SKILL.md's copy keeps its frontmatter, and a maintainer guide or an ADR is its own text in the copy", () => {
  const skill = text(tree, `skills/${PLUGIN}/SKILL.md`) ?? '';
  assert.ok(skill.startsWith(`${sourceFrontmatter(readFileSync(path.join(root, SKILL), 'utf8'))}\n# PouryaKit\n`));
  assert.match(skill, /\| packages\.md \|/);
  assert.match(skill, /\| publication\.md \|/);
  assert.match(skill, /\]\(references\/security\.md#continuations\)/);
  for (const entry of tree.filter((file) => file.path.endsWith('.md'))) {
    const copy = entry.content.toString();
    assert.doesNotMatch(copy, /\]\([^)\s]*(?:packages|publication)\.md[)#]|docs\/adr|\]\(\.\.\/\.\.\//, entry.path);
  }
  assert.match(
    text(tree, `skills/${PLUGIN}/references/multi-tenancy.md`) ?? '',
    /\(ADR 0007\): in a scope nobody began/,
  );
});

test("every relative link of the copy names a file of the copy and one of that file's headings", () => {
  const paths = new Set(tree.map((entry) => entry.path));
  const slugs = new Map<string, Set<string>>();
  const anchors = (target: string) => {
    if (!slugs.has(target)) {
      const slugger = new Slugger();
      const found = new Set<string>();
      visit(parseMarkdown(text(tree, target) ?? ''), 'heading', (heading) => {
        found.add(slugger.slug(renderedText(heading)));
      });
      slugs.set(target, found);
    }
    return slugs.get(target) as Set<string>;
  };
  let links = 0;
  for (const entry of tree.filter((file) => file.path.endsWith('.md'))) {
    visit(parseMarkdown(entry.content.toString()), 'link', (link) => {
      const resolved = resolveLink(entry.path, link.url);
      if (!resolved) return;
      links++;
      assert.ok(paths.has(resolved.target), `${entry.path} links ${link.url}`);
      if (resolved.anchor) assert.ok(anchors(resolved.target).has(resolved.anchor), `${entry.path} links ${link.url}`);
    });
  }
  assert.ok(links > 400, `only ${links} relative links read`);
});

test("a source the site's rules refuse fails the sync, and so does a file of the skill it does not know", () => {
  const tracker = fixture('tracker');
  appendFileSync(path.join(tracker, '.agents/skills/pourya-kit/references/caching.md'), '\nFixed in ticket #42.\n');
  assert.throws(() => assembleTree(tracker, covers), /references\/caching\.md.*tracker code/);

  const link = fixture('link');
  appendFileSync(
    path.join(link, '.agents/skills/pourya-kit/references/web.md'),
    '\n[source](https://github.com/PouryaNoufallah96/PouryaKit/blob/main/x.cs)\n',
  );
  assert.throws(() => assembleTree(link, covers), /links the private repository/);

  const description = fixture('description');
  const skill = path.join(description, SKILL);
  writeFileSync(skill, readFileSync(skill, 'utf8').replace('. Use when writing', '. Since Wave 4, use when writing'));
  assert.throws(() => assembleTree(description, covers), /SKILL\.md.*delivery vocabulary/);

  const unknown = fixture('unknown');
  mkdirSync(path.join(unknown, '.agents/skills/pourya-kit/scripts'));
  writeFileSync(path.join(unknown, '.agents/skills/pourya-kit/scripts/run.sh'), 'echo\n');
  assert.throws(() => assembleTree(unknown, covers), /pourya-kit holds SKILL\.md, agents, references, scripts/);

  const stray = fixture('stray');
  writeFileSync(path.join(stray, '.agents/skills/pourya-kit/references/diagram.png'), 'x');
  assert.throws(() => assembleTree(stray, covers), /diagram\.png under .* is on neither list/);
});

test('a private link behind a dot segment fails the sync, literal or escaped, as a link, as text and in the README', () => {
  const web = '.agents/skills/pourya-kit/references/web.md';
  for (const [name, line] of [
    ['dots-link', '[source](https://github.com/PouryaNoufallah96/pouryakit-public/../PouryaKit/blob/main/x.cs)'],
    ['dots-escaped', '[source](https://github.com/PouryaNoufallah96/pouryakit-public/%2e%2E/PouryaKit)'],
    ['dots-text', 'See `github.com/PouryaNoufallah96/pouryakit-public/../PouryaKit` here.'],
    ['dots-text-escaped', 'See `github.com/PouryaNoufallah96/pouryakit-public/%2e%2e/PouryaKit` here.'],
  ]) {
    const repository = fixture(name);
    appendFileSync(path.join(repository, web), `\n${line}\n`);
    assert.throws(() => assembleTree(repository, covers), /private repository/, line);
  }
  for (const [name, line] of [
    ['dots-readme', '[source](https://github.com/PouryaNoufallah96/pouryakit-public/../PouryaKit)'],
    ['dots-readme-escaped', '[source](https://github.com/PouryaNoufallah96/pouryakit-public/%2e%2e/PouryaKit)'],
    ['dots-readme-slash', '[source](https://github.com/PouryaNoufallah96/x%2F../../PouryaKit)'],
  ]) {
    const repository = fixture(name);
    appendFileSync(path.join(repository, README_TEMPLATE), `\n${line}\n`);
    assert.throws(() => assembleTree(repository, covers), /README\.md: private repository/, line);
  }
});

test('a title is published text: a private name or a delivery word in one fails the sync, in the README and the copy', () => {
  const name = fixture('title-name');
  appendFileSync(path.join(name, README_TEMPLATE), '\n[Docs](https://example.com "Cybernod")\n');
  assert.throws(() => assembleTree(name, covers), /README\.md: private project \(Cybernod\)/);

  const word = fixture('title-word');
  appendFileSync(path.join(word, README_TEMPLATE), '\n![Cover](https://example.com/a.png "since Wave 4")\n');
  assert.throws(() => assembleTree(word, covers), /README\.md: delivery vocabulary/);

  const copy = fixture('title-copy');
  const web = path.join(copy, '.agents/skills/pourya-kit/references/web.md');
  appendFileSync(web, '\n[Docs](https://example.com "since Wave 4")\n');
  assert.throws(() => assembleTree(copy, covers), /references\/web\.md.*delivery vocabulary/);

  const privateName = fixture('title-copy-name');
  appendFileSync(
    path.join(privateName, '.agents/skills/pourya-kit/references/web.md'),
    '\n![Cover](https://example.com/a.png "Cybernod")\n',
  );
  assert.throws(() => assembleTree(privateName, covers), /references\/web\.md.*private project \(Cybernod\)/);
});

test('the README fills every name it uses from one place and refuses one it does not know', () => {
  const readme = text(tree, 'README.md') ?? '';
  assert.doesNotMatch(readme, /\{\{|\}\}/);
  assert.match(readme, /claude plugin install pourya-kit@pouryakit/);
  assert.match(readme, /raw\.githubusercontent\.com\/PouryaNoufallah96\/pouryakit-public\/assets-v1\/assets\/kit\.png/);
  assert.throws(() => fillReadme('see {{website}}'), /names \{\{website\}\}/);
  assert.throws(() => fillReadme('see {{ site }}'), /a \{\{ or \}\}/);
});

test("the AI skill page's install commands name the public repository, the marketplace and the plugin", () => {
  // The page keeps its commands as text, so they reach its Markdown; this holds them to addresses.ts.
  const page = readFileSync(path.join(root, 'website/content/docs/get-started/ai-skill.mdx'), 'utf8');
  const sources = [...page.matchAll(/(?:skills add|plugin marketplace add) (\S+)/g)].map((match) => match[1]);
  const installs = [...page.matchAll(/plugin install (\S+)/g)].map((match) => match[1]);
  assert.ok(sources.length >= 3 && installs.length >= 1);
  assert.deepEqual(new Set(sources), new Set([PUBLIC_REPOSITORY]));
  assert.deepEqual(new Set(installs), new Set([`${PLUGIN}@${MARKETPLACE}`]));
});

test('the regeneration check fails on a changed source, in either direction, and passes after regeneration', () => {
  const repository = fixture('regenerate');
  const committed = parseManifest(formatManifest(manifestOf(assembleTree(repository, covers))));
  assert.deepEqual(manifestDifferences(committed, manifestOf(assembleTree(repository, covers))), []);

  appendFileSync(path.join(repository, '.agents/skills/pourya-kit/references/caching.md'), '\nOne more line.\n');
  const changed = manifestOf(assembleTree(repository, covers));
  assert.deepEqual(manifestDifferences(committed, changed), [`changed: skills/${PLUGIN}/references/caching.md`]);
  assert.deepEqual(manifestDifferences(parseManifest(formatManifest(changed)), changed), []);

  const extra = new Map(changed).set('assets/extra.png', sha256(Buffer.from('x')));
  assert.deepEqual(manifestDifferences(changed, extra), ['extra: assets/extra.png']);
  assert.deepEqual(manifestDifferences(extra, changed), ['missing: assets/extra.png']);
  assert.throws(() => parseManifest('not a manifest line\n'), /cannot read/);
});

// --verify-remote, against a local bare repository. Each git call runs without this machine's global and system
// configuration (an identity, hooks or a trace daemon there would write into the temporary repositories).
for (const key of Object.keys(process.env)) if (key.startsWith('GIT_')) delete process.env[key];
process.env.GIT_CONFIG_GLOBAL = devNull;
process.env.GIT_CONFIG_NOSYSTEM = '1';

function git(directory: string, ...args: string[]) {
  const settings = ['user.name=Kit Test', 'user.email=kit@example.com', `core.hooksPath=${devNull}`, 'gc.auto=0'];
  execFileSync('git', [...settings.flatMap((setting) => ['-c', setting]), '-C', directory, ...args], { stdio: 'pipe' });
}

test('--verify-remote passes only when the default branch is the tagged commit and it holds exactly the manifest', () => {
  const remote = path.join(scratch, 'remote.git');
  const work = path.join(scratch, 'work');
  mkdirSync(remote);
  mkdirSync(work);
  git(remote, 'init', '--quiet', '--bare', '--initial-branch=main');
  git(work, 'init', '--quiet', '--initial-branch=main');
  const files = { 'README.md': '# Public\n', 'assets/kit.png': 'kit', 'skills/pourya-kit/SKILL.md': '# Skill\n' };
  for (const [target, content] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(work, target)), { recursive: true });
    writeFileSync(path.join(work, target), content);
  }
  const manifest = new Map(Object.entries(files).map(([target, content]) => [target, sha256(Buffer.from(content))]));
  const publish = (message: string, tag: string, branch = true) => {
    git(work, 'add', '--all');
    git(work, 'commit', '--quiet', '--allow-empty', '-m', message);
    git(work, 'tag', '--annotate', '-m', tag, tag);
    if (branch) git(work, 'push', '--quiet', '--force', remote, 'main');
    git(work, 'push', '--quiet', remote, `refs/tags/${tag}`);
  };

  publish('the reviewed tree', 'assets-v1');
  const matching = verifyRemote(remote, 'assets-v1', manifest);
  assert.deepEqual(matching.problems, []);
  assert.equal(matching.branch, 'main');

  publish('a later tag the default branch never reached', 'assets-v2', false);
  const stale = verifyRemote(remote, 'assets-v2', manifest).problems;
  assert.equal(stale.length, 1);
  assert.match(stale[0], /^the default branch main is at [0-9a-f]{40}, not at [0-9a-f]{40}, which assets-v2 tags/);

  writeFileSync(path.join(work, 'README.md'), '# Public, edited\n');
  publish('a changed file', 'assets-v3');
  assert.deepEqual(verifyRemote(remote, 'assets-v3', manifest).problems, ['changed: README.md']);

  writeFileSync(path.join(work, 'README.md'), files['README.md']);
  writeFileSync(path.join(work, 'assets/extra.png'), 'extra');
  publish('an extra file', 'assets-v4');
  assert.deepEqual(verifyRemote(remote, 'assets-v4', manifest).problems, ['extra: assets/extra.png']);

  assert.deepEqual(verifyRemote(remote, 'assets-v9', manifest).problems, [`${remote} has no tag assets-v9`]);
});
