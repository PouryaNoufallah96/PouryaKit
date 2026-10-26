import assert from 'node:assert/strict';
import { test } from 'node:test';
import Slugger from 'github-slugger';
import { glossaryMarkdown, parseGlossary, projectGlossary } from './glossary.ts';
import { findRoot, GLOSSARY, readSource } from './paths.ts';

const text = readSource(findRoot(), GLOSSARY);

function terms(sections: ReturnType<typeof parseGlossary>) {
  return sections.flatMap((section) => section.entries.map((entry) => `${section.title} / ${entry.term}`));
}

test('the projection leaves out exactly Wave, Phase and Spike, and keeps every other entry of GLOSSARY.md', () => {
  const all = terms(parseGlossary(text));
  // The parser reads every entry: one per `**Term**:` line of the file.
  assert.equal(all.length, text.split('\n').filter((line) => /^\*\*.+\*\*:$/.test(line)).length);
  const left = ['Delivery of the Kit / Wave', 'Delivery of the Kit / Phase', 'Delivery of the Kit / Spike'];
  assert.deepEqual(
    terms(projectGlossary(parseGlossary(text))),
    all.filter((term) => !left.includes(term)),
  );
  assert.ok(terms(projectGlossary(parseGlossary(text))).includes('Delivery of the Kit / Proving Sample'));
  assert.ok(terms(projectGlossary(parseGlossary(text))).includes('Delivery of the Kit / Project template'));
});

test('each term has an anchor of its own on the page', () => {
  const markdown = glossaryMarkdown(projectGlossary(parseGlossary(text)));
  const slugger = new Slugger();
  const anchors = markdown
    .split('\n')
    .filter((line) => line.startsWith('#'))
    .map((line) => slugger.slug(line.replace(/^#+ /, '')));
  for (const anchor of ['operation-context', 'proving-sample', 'project-template', 'wire-name']) {
    assert.ok(anchors.includes(anchor), anchor);
  }
  assert.ok(!anchors.includes('wave') && !anchors.includes('spike') && !anchors.includes('phase'));
});

test('an exclusion selector that matches no entry fails the projection', () => {
  const withoutSpike = text.replace(/\*\*Spike\*\*:\n[^\n]+\n_Avoid_:[^\n]+\n/, '');
  assert.notEqual(withoutSpike, text);
  assert.throws(() => projectGlossary(parseGlossary(withoutSpike)), /Delivery of the Kit \/ Spike matches 0 entries/);
});

test('a block of "Language" that is no entry fails, so no entry is skipped unread', () => {
  assert.throws(() => parseGlossary(text.replace('**Kit**:', 'Kit:')), /GLOSSARY\.md:\d+ is not an entry/);
});
