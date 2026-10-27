import assert from 'node:assert/strict';
import { test } from 'node:test';
import type { List } from 'mdast';
import { applyRewrites, compareManifest, dropCitations, projectAdr } from './adr.ts';
import { nodeViolations, parseMarkdown, parseSource, renderedText, stringifyMarkdown } from './markdown.ts';
import { ADRS, findRoot, readSource } from './paths.ts';
import { adrExclusions, projectAdrs, readManifest } from './site.ts';

test('a parenthetical that only cites the tracker is dropped, and one with substance stays', () => {
  assert.equal(dropCitations('It stands (ticket #13).'), 'It stands.');
  assert.equal(dropCitations('It stands (tickets #42 and #43).'), 'It stands.');
  assert.equal(dropCitations('A Spike (spec #33, "Required Spikes" item 2) ran.'), 'A Spike ran.');
  assert.equal(dropCitations('As before (spec #74, finding V13).'), 'As before.');
  assert.equal(dropCitations('Trusted (G.6) and scoped (plan section F).'), 'Trusted and scoped.');
  assert.equal(dropCitations('Cached (I.3 Caching).'), 'Cached.');
  assert.equal(dropCitations('Scoped (plan\n  section F) here.'), 'Scoped\n   here.');
  assert.equal(
    dropCitations('Never forwarded (G.6, plan I.3; `OutgoingHttpTests`).'),
    'Never forwarded (G.6, plan I.3; `OutgoingHttpTests`).',
  );
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
  assert.equal(applyRewrites('0001-x', text, [rewrite]), 'Title\n\nso it\nis a rule.\n');
  assert.throws(() => applyRewrites('0001-x', text, [{ ...rewrite, line: 4 }]), /no longer matches its line/);
  assert.throws(() => applyRewrites('0001-x', text.replace('#19', '#20'), [rewrite]), /no longer matches its line/);
  assert.throws(() => applyRewrites('0001-x', text, [{ ...rewrite, to: 'a\nb\nc' }]), /adds lines/);
});

test('a rewrite over several lines keeps its sentence in one paragraph, at its lines, with no space before punctuation', () => {
  const text = 'Title\n\n- Tickets #15 and\n  #16 run each attempt\n  through it, so it holds.\n- Next.\n';
  const rewrite = {
    adr: '0001-x',
    line: 3,
    from: 'Tickets #15 and #16 run each attempt through it',
    to: 'Each attempt runs through it',
  };
  const projected = applyRewrites('0001-x', text, [rewrite]);
  assert.equal(projected, 'Title\n\n- Each attempt\n  runs\n  through it, so it holds.\n- Next.\n');
  const [, list] = parseMarkdown(projected).children as [unknown, List];
  assert.equal(list.children[0].children.length, 1);
  assert.equal(list.children[1].position?.start.line, 6);

  const dropped = 'Title\n\nIt holds\n(as the record does), and more.\n';
  const citation = { adr: '0001-x', line: 4, from: '(as the record does)', to: '' };
  assert.equal(applyRewrites('0001-x', dropped, [citation]), 'Title\n\nIt holds,\n and more.\n');
  assert.equal(dropCitations('It holds\n(ticket #13). Next.'), 'It holds.\n Next.');
  assert.equal(dropCitations('- One per client\n  (#52).\n- Next.'), '- One per\n client.\n- Next.');
});

test('a rewrite that empties a line moves a word down to hold it, and one that still leaves an empty line fails', () => {
  const text = 'Title\n\nIt holds\nG.2 is unchanged.\nand stays.\n';
  const removed = { adr: '0001-x', line: 4, from: 'G.2 is unchanged.', to: '' };
  assert.equal(applyRewrites('0001-x', text, [removed]), 'Title\n\nIt\nholds\nand stays.\n');
  const spanning = 'Title\n\nIt holds as\nG.2 and the\nrecord say.\n';
  assert.throws(
    () =>
      applyRewrites('0001-x', spanning, [{ adr: '0001-x', line: 3, from: 'as G.2 and the record say.', to: 'here.' }]),
    /leaves line 4 empty/,
  );
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

test('every real amendment keeps its heading and its lead on the public page, so no decision is left out with it', () => {
  const root = findRoot();
  const lost: string[] = [];
  let amendments = 0;
  let written = 0;
  for (const adr of projectAdrs(root)) {
    const text = readSource(root, `${ADRS}/${adr.slug}.md`);
    written += (text.match(/^## Amendment/gm) ?? []).length;
    const source = parseSource(text).children;
    const kept = new Set(adr.tree.children.map((node) => node.position?.start.line));
    source.forEach((node, index) => {
      if (node.type !== 'heading' || !renderedText(node).startsWith('Amendment')) return;
      amendments++;
      for (const block of [node, source[index + 1]]) {
        if (!kept.has(block.position?.start.line)) lost.push(`${adr.slug}:${block.position?.start.line}`);
      }
    });
  }
  assert.ok(written > 0);
  assert.equal(amendments, written);
  assert.deepEqual(lost, []);
});
