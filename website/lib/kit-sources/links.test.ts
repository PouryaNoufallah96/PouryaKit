import assert from 'node:assert/strict';
import { test } from 'node:test';
import { type RouteMap, rewriteLinks } from './links.ts';
import { parseMarkdown, stringifyMarkdown } from './markdown.ts';

const from = '.agents/skills/pourya-kit/references/multi-tenancy.md';
const routes: RouteMap = new Map([
  [
    '.agents/skills/pourya-kit/references/security.md',
    {
      route: '/docs/capabilities/security',
      anchors: new Map<string, string | null>([
        ['continuations', 'continuations'],
        ['security', null],
      ]),
    },
  ],
  [from, { route: '/docs/capabilities/multi-tenancy', anchors: new Map([['testing', 'testing']]) }],
]);

function rewrite(markdown: string) {
  const tree = parseMarkdown(markdown);
  rewriteLinks(tree, from, routes);
  return stringifyMarkdown(tree).trim();
}

test('a link to a rendered source becomes its page, its anchor carried over', () => {
  assert.equal(rewrite('[trust](security.md#continuations)'), '[trust](/docs/capabilities/security#continuations)');
  assert.equal(rewrite('[tests](#testing)'), '[tests](#testing)');
  assert.equal(rewrite('[it](security.md#security)'), '[it](/docs/capabilities/security)');
});

test('a link into the repository with no page becomes its own text, and a link to another site stays', () => {
  assert.equal(
    rewrite('the [structure tests](../../../../docs/architecture/README.md#structure-tests)'),
    'the structure tests',
  );
  assert.equal(rewrite('see [`docs/packages.md`](../../../../docs/packages.md)'), 'see `docs/packages.md`');
  assert.equal(
    rewrite(
      'set by it ([ADR 0007](../../../../docs/adr/0007-operation-context-set-by-the-scope-opener.md#amendment-2026-10-07-x))',
    ),
    'set by it (ADR 0007)',
  );
  assert.equal(rewrite('[Wolverine](https://wolverinefx.net/)'), '[Wolverine](https://wolverinefx.net/)');
});

test('a link to the private repository fails, and so does an anchor the target page does not have', () => {
  assert.throws(
    () => rewrite('[source](https://github.com/PouryaNoufallah96/PouryaKit/blob/main/x.cs)'),
    /links the private repository/,
  );
  assert.throws(() => rewrite('[x](security.md#no-such-section)'), /an anchor .*security\.md does not have/);
});
