import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { findRoot, REFERENCES } from './paths.ts';
import {
  MAINTAINER_GUIDES,
  PUBLISHED_REFERENCES,
  packagesOf,
  publishedReferences,
  skillEntries,
} from './references.ts';

function referencesDirectory(files: string[]) {
  const root = mkdtempSync(path.join(tmpdir(), 'kit-references-'));
  mkdirSync(path.join(root, REFERENCES), { recursive: true });
  for (const file of files) writeFileSync(path.join(root, REFERENCES, file), '# x\n');
  return root;
}

test("the allow-list publishes every reference but the two maintainer guides, and the skill's tables group them", () => {
  const root = findRoot();
  assert.deepEqual(publishedReferences(root), [...PUBLISHED_REFERENCES]);
  assert.equal(PUBLISHED_REFERENCES.length, 29);
  assert.ok(MAINTAINER_GUIDES.every((guide) => !(PUBLISHED_REFERENCES as readonly string[]).includes(guide)));
  const entries = skillEntries(root);
  assert.equal(entries.filter((entry) => entry.group === 'Capabilities').length, 28);
  assert.deepEqual(
    entries.filter((entry) => entry.group === 'Guides').map((entry) => entry.file),
    ['host-recipes.md'],
  );
  assert.deepEqual(entries.find((entry) => entry.file === 'persistence.md')?.packages, [
    'PouryaKit.Persistence',
    'PouryaKit.Persistence.EFCore',
    'PouryaKit.Persistence.Postgres',
  ]);
});

test('a reference on neither list fails, and so does a listed reference that is gone', () => {
  const extra = referencesDirectory([...PUBLISHED_REFERENCES, ...MAINTAINER_GUIDES, 'new-capability.md']);
  const missing = referencesDirectory([
    ...PUBLISHED_REFERENCES.filter((file) => file !== 'web.md'),
    ...MAINTAINER_GUIDES,
  ]);
  try {
    assert.throws(() => publishedReferences(extra), /new-capability\.md .* is on neither list/);
    assert.throws(() => publishedReferences(missing), /published references missing .*: web\.md/);
  } finally {
    rmSync(extra, { recursive: true, force: true });
    rmSync(missing, { recursive: true, force: true });
  }
});

test("a package named from the cell's first ID is that ID with its suffix", () => {
  assert.deepEqual(packagesOf(['PouryaKit.Messaging', '.Wolverine', '.Wolverine.EFCore']), [
    'PouryaKit.Messaging',
    'PouryaKit.Messaging.Wolverine',
    'PouryaKit.Messaging.Wolverine.EFCore',
  ]);
  assert.throws(() => packagesOf(['.EFCore']), /no full ID before it/);
});
