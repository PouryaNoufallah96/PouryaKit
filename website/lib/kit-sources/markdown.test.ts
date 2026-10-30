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

test("a string a file publishes outside its rendered text is held to every rule: a title, a fence's info, a label", () => {
  for (const text of [
    '[Docs](https://example.com "Cybernod")',
    '![Cover](https://example.com/a.png "Cybernod")',
    '[Docs](https://example.com "since Wave 4")',
    '![Cover](https://example.com/a.png "decision W11")',
    '[Docs](https://example.com "https://github.com/PouryaNoufallah96/PouryaKit")',
    '[Docs][a]\n\n[a]: https://example.com "since Wave 4"',
    '[Docs][Cybernod]\n\n[Cybernod]: https://example.com',
    '```csharp title="since Wave 4"\nvar a = 1;\n```',
  ]) {
    assert.ok(nodeViolations(parseMarkdown(text)).length > 0, text);
  }
});

test('a destination stays markup and a title is a block of its own, so neither is read as text', () => {
  for (const text of [
    '[Docs](https://example.com/Wave/spike#123)',
    '![Cover](https://example.com/Wave-4.png "The cover")',
    '[Docs](https://example.com "The docs")',
    '[in session](https://example.com "6")',
  ]) {
    assert.deepEqual(nodeViolations(parseMarkdown(text)), [], text);
  }
});
