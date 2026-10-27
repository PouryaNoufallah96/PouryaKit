import assert from 'node:assert/strict';
import { test } from 'node:test';
import { nodeViolations, parseMarkdown } from './markdown.ts';

test('a page is read block by block: a code at the end of a block fails, and no code is read from one block into the next', () => {
  for (const text of [
    'Decision W3\n\nNext paragraph.',
    '- Decision W3\n- Next item',
    '| A | B |\n|---|---|\n| Decision W3 | Next |',
  ]) {
    assert.ok(nodeViolations(parseMarkdown(text)).length > 0, text);
  }
  for (const text of ['Fixed in #1\n\n2 more.', '- item #1\n- 2 more', '| A | B |\n|---|---|\n| item #1 | 2 more |']) {
    assert.deepEqual(nodeViolations(parseMarkdown(text)), [], text);
  }
});
