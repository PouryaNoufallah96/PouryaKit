import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { readErrorCodes } from './errors.ts';
import { parseMarkdown } from './markdown.ts';
import { findRoot } from './paths.ts';
import { buildSite, referenceAnchors } from './site.ts';

const root = findRoot();

test('the site generates a page per published reference and package, and the Reference pages', () => {
  const pages = buildSite(root).map((file) => file.path);
  assert.equal(pages.filter((page) => /^content\/docs\/capabilities\/[a-z-]+\.mdx$/.test(page)).length, 29);
  assert.equal(pages.filter((page) => /^content\/docs\/reference\/packages\/pouryakit.*\.mdx$/.test(page)).length, 38);
  for (const page of ['glossary', 'error-codes', 'composition-checks', 'packages/index']) {
    assert.ok(pages.includes(`content/docs/reference/${page}.md`), page);
  }
  assert.ok(!pages.some((page) => /packages\.md|publication\.md/.test(page)));
});

test("no ADR is published: no page of one, and a reference's link to one renders as its own text", () => {
  const files = buildSite(root);
  assert.deepEqual(
    files
      .filter((file) => /\/adr\b/.test(file.path) || /docs\/adr\b|reference\/adr\b/.test(file.content))
      .map((file) => file.path),
    [],
  );
  const tenancy = files.find((file) => file.path === '.generated/references/multi-tenancy.md')?.content ?? '';
  assert.match(tenancy, /\(ADR 0007\): in a scope nobody began/);
});

test("no generated page carries a source's frontmatter: the Capabilities index starts at the skill's text", () => {
  const files = buildSite(root);
  const index = files.find((file) => file.path === 'content/docs/capabilities/index.md')?.content ?? '';
  const body = index.replace(/^---\n[\s\S]*?\n---\n/, '');
  assert.doesNotMatch(body, /^\s*(?:---|\*\*\*)\s*\n/);
  for (const file of files) assert.ok(!file.content.includes('name: pourya-kit'), file.path);
});

test("a top heading that would renumber one of the reference's anchors fails", () => {
  const reference = parseMarkdown('# Web\n\n## Testing\n\n## Limits\n');
  assert.equal(referenceAnchors(reference, ['When to use it', 'Reference'], 'top.mdx').get('testing'), 'testing');
  assert.throws(
    () => referenceAnchors(reference, ['Testing', 'Reference'], 'top.mdx'),
    /top\.mdx has a heading .* testing/,
  );
});

test("the Error codes come from each package's Errors types, and a shape the reader does not know fails", () => {
  const codes = readErrorCodes(root, ['PouryaKit.Cqrs', 'PouryaKit.Storage']);
  const mismatch = codes.find((entry) => entry.code === 'Tenant.Mismatch');
  assert.equal(mismatch?.kind, 'Forbidden');
  assert.match(mismatch?.message ?? '', /^This operation changes data of another tenant/);
  assert.equal(
    codes.find((entry) => entry.code === 'Page.SizeOutOfRange')?.summary,
    'The size is outside 1 to the declared maximum; the target is Size.',
  );
  assert.match(codes.find((entry) => entry.code === 'Storage.LinkLifetimeOutOfRange')?.message ?? '', /\{maximum\}/);

  const fixture = mkdtempSync(path.join(tmpdir(), 'kit-errors-'));
  try {
    mkdirSync(path.join(fixture, 'src/PouryaKit.X'), { recursive: true });
    writeFileSync(
      path.join(fixture, 'src/PouryaKit.X/XErrors.cs'),
      'namespace PouryaKit.X;\n\npublic static class XErrors\n{\n    public static Error Of() => Error.Validation(code: "X.Odd", message: "Odd.");\n}\n',
    );
    assert.throws(() => readErrorCodes(fixture, ['PouryaKit.X']), /declares X\.Odd in a shape .* does not read/);
  } finally {
    rmSync(fixture, { recursive: true, force: true });
  }
});
