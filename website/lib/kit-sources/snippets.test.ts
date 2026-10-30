import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import type { Root } from 'mdast';
import { remark } from 'remark';
import remarkMdx from 'remark-mdx';
import { findRoot } from './paths.ts';
import { readSnippet, remarkKitSnippets } from './snippets.ts';

const root = findRoot();
const handler =
  'samples/ModularMonolith/src/Modules/Catalog/Catalog/Products/Features/RenameProduct/RenameProductCommandHandler.cs';

test("a named region of the Proving Sample's code is its lines between the markers", () => {
  const code = readSnippet(root, handler, 'rename-product-handler');
  assert.match(code, /^internal sealed class RenameProductCommandHandler/);
  assert.match(code, /\n}$/);
  assert.doesNotMatch(code, /#region|#endregion|namespace/);
});

test('a region is exactly the lines between its markers, each of them kept', () => {
  const fixture = mkdtempSync(path.join(tmpdir(), 'kit-snippet-'));
  try {
    mkdirSync(path.join(fixture, 'tests'), { recursive: true });
    writeFileSync(
      path.join(fixture, 'tests/A.cs'),
      'before\n#region a\nclass A\n{\n    int B() => 1;\n\n    int C() => 2;\n}\n#endregion\nafter\n',
    );
    assert.equal(readSnippet(fixture, 'tests/A.cs', 'a'), 'class A\n{\n    int B() => 1;\n\n    int C() => 2;\n}');
  } finally {
    rmSync(fixture, { recursive: true, force: true });
  }
});

test('a missing region, a missing file and a file outside the sample and the tests each fail', () => {
  assert.throws(() => readSnippet(root, handler, 'no-such-region'), /the region no-such-region does not exist/);
  assert.throws(() => readSnippet(root, `${handler}.missing`, 'rename-product-handler'), /does not exist/);
  for (const file of [
    'docs/adr/0001-one-host-container.md',
    'samples/ModularMonolith/../../GLOSSARY.md',
    'src/PouryaKit.Cqrs/Operation.cs',
  ]) {
    assert.throws(() => readSnippet(root, file, 'x'), /is not under samples\/ModularMonolith\/ or tests\//, file);
  }
});

test('a region named twice fails', () => {
  const fixture = mkdtempSync(path.join(tmpdir(), 'kit-snippet-'));
  try {
    mkdirSync(path.join(fixture, 'tests'), { recursive: true });
    writeFileSync(path.join(fixture, 'tests/A.cs'), '#region a\nx\n#endregion\n#region a\ny\n#endregion\n');
    assert.throws(() => readSnippet(fixture, 'tests/A.cs', 'a'), /named 2 times/);
  } finally {
    rmSync(fixture, { recursive: true, force: true });
  }
});

test('<Snippet> becomes a code block titled with the file name', async () => {
  const processor = remark().use(remarkMdx).use(remarkKitSnippets, { root });
  const tree = (await processor.run(
    processor.parse(`<Snippet file="${handler}" region="rename-product-handler" />\n`),
  )) as Root;
  const [code] = tree.children;
  assert.equal(code.type, 'code');
  assert.equal(code.type === 'code' && code.lang, 'csharp');
  assert.equal(code.type === 'code' && code.meta, 'title="RenameProductCommandHandler.cs"');
  assert.equal(code.type === 'code' && code.value, readSnippet(root, handler, 'rename-product-handler'));
  await assert.rejects(processor.run(processor.parse('<Snippet file="tests/x.cs" />\n')), /needs a file and a region/);
});
