import assert from 'node:assert/strict';
import { test } from 'node:test';
import { applyRewrites, compareManifest, dropCitations, projectAdr } from './adr.ts';
import { nodeViolations, stringifyMarkdown } from './markdown.ts';
import { findRoot } from './paths.ts';
import { adrExclusions, projectAdrs, readManifest } from './site.ts';

test('a parenthetical that only cites the tracker is dropped, and one with substance stays', () => {
  assert.equal(dropCitations('It stands (ticket #13).'), 'It stands.');
  assert.equal(dropCitations('It stands (tickets #42 and #43).'), 'It stands.');
  assert.equal(dropCitations('A Spike (spec #33, "Required Spikes" item 2) ran.'), 'A Spike ran.');
  assert.equal(dropCitations('As before (spec #74, finding V13).'), 'As before.');
  assert.equal(dropCitations('Kept (ADR 0013, item 8).'), 'Kept (ADR 0013, item 8).');
  assert.equal(
    dropCitations('Read (ticket #90; read decompiled from WolverineFx 6.48.3).'),
    'Read (ticket #90; read decompiled from WolverineFx 6.48.3).',
  );
});

test("an amendment heading loses its citation and keeps its subject; fenced code and the source's lines stay", () => {
  assert.equal(
    dropCitations("## Amendment (2026-10-07, ticket #61: a Module's Features)"),
    "## Amendment (2026-10-07: a Module's Features)",
  );
  assert.equal(dropCitations('## Amendment (2026-10-06, Wave 1 as built)'), '## Amendment (2026-10-06)');
  assert.equal(dropCitations('```\nrun (ticket #13)\n```'), '```\nrun (ticket #13)\n```');
  assert.equal(dropCitations('a (ticket\n  #13) b').split('\n').length, 2);
});

test('a reviewed rewrite applies on its keyed line, and fails once that line no longer holds its text', () => {
  const text = 'Title\n\nso tickets #13\nand #19 made it a rule.\n';
  const rewrite = { adr: '0001-x', line: 3, from: 'so tickets #13 and #19 made it', to: 'so it is' };
  assert.equal(applyRewrites('0001-x', text, [rewrite]), 'Title\n\nso it is\n a rule.\n');
  assert.throws(() => applyRewrites('0001-x', text, [{ ...rewrite, line: 4 }]), /no longer matches its line/);
  assert.throws(() => applyRewrites('0001-x', text.replace('#19', '#20'), [rewrite]), /no longer matches its line/);
  assert.throws(() => applyRewrites('0001-x', text, [{ ...rewrite, to: 'a\nb\nc' }]), /adds lines/);
});

const record = [
  '---',
  'status: accepted',
  '---',
  '',
  '# A decision',
  '',
  'The decision holds.',
  '',
  'Fixed in #12 and nothing else.',
  '',
  '- One rule.',
  '- Built by #14.',
  '',
  '| Invariant | Owner |',
  '|---|---|',
  '| Holds | #15 |',
  '',
  '## Requirements for #15',
  '',
  'Gone with its heading.',
  '',
  '## Amendment (2026-10-07, ticket #61: one rule)',
  '',
  'Kept.',
  '',
].join('\n');

test('a passage that still cites the tracker is left out at its line, and an excluded heading takes its section', () => {
  const adr = projectAdr('0099-a-decision', record, []);
  assert.deepEqual(
    adr.exclusions.map((exclusion) => `${exclusion.line} ${exclusion.kind}`),
    ['9 paragraph', '12 list item', '18 section'],
  );
  const page = stringifyMarkdown(adr.tree);
  assert.match(page, /The decision holds\./);
  assert.match(page, /One rule\./);
  assert.match(page, /\| Invariant \|\n/);
  assert.doesNotMatch(page, /Owner|#1\d|Gone with/);
  assert.deepEqual(nodeViolations(adr.tree), []);
});

test("the record's anchors map to the page's, and an excluded section's to none", () => {
  const adr = projectAdr('0099-a-decision', record, []);
  assert.equal(adr.anchors.get('amendment-2026-10-07-ticket-61-one-rule'), 'amendment-2026-10-07-one-rule');
  assert.equal(adr.anchors.get('requirements-for-15'), null);
});

test('the manifest comparison fails both ways: a passage left out but unlisted, and a listed one no longer left out', () => {
  const [entry] = projectAdr('0099-a-decision', record, []).exclusions;
  assert.deepEqual(compareManifest([entry], [entry]), []);
  assert.match(compareManifest([entry], []).join(), /left out but not in the manifest/);
  assert.match(compareManifest([], [entry]).join(), /in the manifest but not left out/);
});

test('the real ADRs project with no tracker code left, and leave out exactly what the committed manifest lists', () => {
  const root = findRoot();
  const adrs = projectAdrs(root);
  for (const adr of adrs) assert.deepEqual(nodeViolations(adr.tree), [], adr.slug);
  assert.deepEqual(compareManifest(adrExclusions(adrs), readManifest(root)), []);
});
