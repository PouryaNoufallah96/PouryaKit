import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { check, closure, violations } from './check-licences.mjs';

const site = join(dirname(fileURLToPath(import.meta.url)), '..');

// A pnpm-shaped report of planted packages, each manifest keyed by its path.
function planted(manifests) {
  const report = {
    Planted: Object.keys(manifests).map((path) => ({ name: path, versions: ['1.0.0'], paths: [path] })),
  };
  return violations(closure(report, (path) => manifests[path]));
}

test('a planted non-permissive licence and a planted missing licence fail, as does every unlisted shape', () => {
  assert.deepEqual(
    planted({
      plain: { name: 'plain', version: '1.0.0', license: 'MIT' },
      copyleft: { name: 'copyleft', version: '1.0.0', license: 'GPL-3.0-only' },
      unlabelled: { name: 'unlabelled', version: '1.0.0' },
      either: { name: 'either', version: '1.0.0', license: 'MIT OR GPL-3.0-only' },
      both: { name: 'both', version: '1.0.0', license: 'MIT AND GPL-3.0-only' },
      grouped: { name: 'grouped', version: '1.0.0', license: '(MIT OR Apache-2.0)' },
      custom: { name: 'custom', version: '1.0.0', license: 'SEE LICENSE IN LICENSE.txt' },
      legacy: { name: 'legacy', version: '1.0.0', licenses: [{ type: 'MIT' }] },
    }),
    [
      'copyleft 1.0.0 is licensed GPL-3.0-only, which the check does not admit.',
      'unlabelled 1.0.0 declares no licence.',
      'both 1.0.0 is licensed MIT AND GPL-3.0-only, which the check does not admit.',
      'grouped 1.0.0 is licensed (MIT OR Apache-2.0), which the check does not admit.',
      'custom 1.0.0 is licensed SEE LICENSE IN LICENSE.txt, which the check does not admit.',
      'legacy 1.0.0 declares no licence.',
    ],
  );
});

test('each exception admits its licence for its named packages only', () => {
  assert.deepEqual(
    planted({
      'caniuse-lite': { name: 'caniuse-lite', version: '1.0.0', license: 'CC-BY-4.0' },
      'browser-data': { name: 'browser-data', version: '1.0.0', license: 'CC-BY-4.0' },
      lightningcss: { name: 'lightningcss', version: '1.0.0', license: 'MPL-2.0' },
      'lightningcss-linux-x64-gnu': { name: 'lightningcss-linux-x64-gnu', version: '1.0.0', license: 'MPL-2.0' },
      'lightningcss-evil': { name: 'lightningcss-evil', version: '1.0.0', license: 'MPL-2.0' },
      'lightningcss-linux-x64-gnu-evil': {
        name: 'lightningcss-linux-x64-gnu-evil',
        version: '1.0.0',
        license: 'MPL-2.0',
      },
      'css-compiler': { name: 'css-compiler', version: '1.0.0', license: 'MPL-2.0' },
      'lightningcss-data': { name: 'caniuse-lite', version: '2.0.0', license: 'MPL-2.0' },
    }),
    [
      'browser-data 1.0.0 is licensed CC-BY-4.0, which the check does not admit.',
      'lightningcss-evil 1.0.0 is licensed MPL-2.0, which the check does not admit.',
      'lightningcss-linux-x64-gnu-evil 1.0.0 is licensed MPL-2.0, which the check does not admit.',
      'css-compiler 1.0.0 is licensed MPL-2.0, which the check does not admit.',
      'caniuse-lite 2.0.0 is licensed MPL-2.0, which the check does not admit.',
    ],
  );
});

test('an installed package missing from pnpm report fails', () => {
  const fixture = mkdtempSync(join(tmpdir(), 'licences-'));
  try {
    for (const name of ['reported', 'unreported']) {
      const directory = join(fixture, 'node_modules', '.pnpm', `${name}@1.0.0`, 'node_modules', name);
      mkdirSync(directory, { recursive: true });
      writeFileSync(join(directory, 'package.json'), JSON.stringify({ name, version: '1.0.0', license: 'MIT' }));
    }
    const path = join(fixture, 'node_modules', '.pnpm', 'reported@1.0.0', 'node_modules', 'reported');
    const report = { MIT: [{ name: 'reported', versions: ['1.0.0'], paths: [path] }] };

    assert.deepEqual(check({ site: fixture, report }).problems, [
      "unreported@1.0.0 is installed but missing from pnpm's licence report.",
    ]);
  } finally {
    rmSync(fixture, { recursive: true, force: true });
  }
});

test('the installed closure passes, the exceptions it needs included', () => {
  const { packages, problems } = check({ site });

  assert.deepEqual(problems, []);
  for (const name of ['next', '@biomejs/biome', 'caniuse-lite', 'lightningcss']) {
    assert.ok(
      packages.some((p) => p.name === name),
      `${name} is in the closure`,
    );
  }
});
