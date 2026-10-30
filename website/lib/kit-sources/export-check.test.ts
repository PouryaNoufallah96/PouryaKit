import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { test } from 'node:test';
import { checkExport } from './export-check.ts';

function findingsFor(files: Record<string, string>) {
  const out = mkdtempSync(path.join(tmpdir(), 'kit-export-'));
  try {
    for (const [file, content] of Object.entries(files)) {
      mkdirSync(path.dirname(path.join(out, file)), { recursive: true });
      writeFileSync(path.join(out, file), content);
    }
    return checkExport(out).findings.map((finding) => `${finding.file} ${finding.rule}`);
  } finally {
    rmSync(out, { recursive: true, force: true });
  }
}

test('a private link in an attribute fails the export, though no reader sees it as text', () => {
  const findings = findingsFor({
    'docs/index.html': '<a href="https://github.com/PouryaNoufallah96/PouryaKit">source</a>',
  });
  assert.deepEqual(findings, ['docs/index.html private repository']);
});

test('a private link behind a dot segment fails the export, literal or escaped, in an attribute and in a script', () => {
  const findings = findingsFor({
    '_next/static/chunk.js':
      'const a="https:\\/\\/github.com\\/PouryaNoufallah96\\/pouryakit-public\\/..\\/PouryaKit";',
    'docs/a.html': '<a href="https://github.com/PouryaNoufallah96/pouryakit-public/../PouryaKit">source</a>',
    'docs/b.html': '<a href="https://github.com/PouryaNoufallah96/pouryakit-public/%2e%2E/PouryaKit">source</a>',
    'docs/c.html': '<a href="https://github.com/PouryaNoufallah96/x/../pouryakit-public">source</a>',
    'docs/d.html': '<a href="https://github.com/../../PouryaNoufallah96/pouryakit-public">source</a>',
    'docs/e.html': '<a href="https://github.com/PouryaNoufallah96/x%2F../../PouryaKit">source</a>',
  });
  assert.deepEqual(findings, [
    '_next/static/chunk.js private repository',
    'docs/a.html private repository',
    'docs/b.html private repository',
    'docs/e.html private repository',
  ]);
});

test('a private link or a local path in a script or a payload fails, its string escapes undone', () => {
  const findings = findingsFor({
    '_next/static/chunk.js': 'const a="https:\\/\\/github.com\\/PouryaNoufallah96\\/PouryaKit";',
    'docs/index.txt': '1:["/Users/ada/kit/website/content/docs/index.mdx"]',
    'docs/page.txt': '2:["a:\\u003cid\\u003e is a key, not a drive"]',
  });
  assert.deepEqual(findings, ['_next/static/chunk.js private repository', 'docs/index.txt local path (/Users/)']);
});

test('in a script, a one-letter key or a ternary before a regular expression is no drive; a backslash drive is', () => {
  const findings = findingsFor({
    '_next/static/chunk.js': 'const r={a:/re/},s=e?t:/x/.test(v),d="D:\\\\work";',
    'docs/index.txt': '1:["at D:/repo"]',
  });
  assert.deepEqual(findings, [
    '_next/static/chunk.js local path (Windows drive)',
    'docs/index.txt local path (Windows drive)',
  ]);
});

test('a tracker code fails in rendered text: a page, its Markdown, llms.txt and the search index', () => {
  const findings = findingsFor({
    'docs/index.html': '<p>Fixed in #123.</p>',
    'llms.mdx/docs/content.md': '# Docs\n\nSince spec #183.\n',
    'llms.txt': '- [Docs](/docs): built in Wave 4\n',
    'api/search.json': JSON.stringify({ docs: [{ content: 'decision W11' }] }),
  });
  assert.deepEqual(findings, [
    'api/search.json tracker code (W<n>)',
    'docs/index.html tracker code (#<n>)',
    'llms.mdx/docs/content.md tracker code (spec #)',
    'llms.mdx/docs/content.md tracker code (#<n>)',
    'llms.txt delivery vocabulary (Wave, Phase)',
  ]);
});

test("a private project's name fails anywhere, a script's included; a delivery word fails only as text", () => {
  const findings = findingsFor({
    '_next/static/chunk.js': 'const from="cybernod@bae8ac3";',
    'docs/index.html': '<p>The Spike ran.</p><script>self.push("Spike G.12")</script>',
  });
  assert.deepEqual(findings, [
    '_next/static/chunk.js private project (Cybernod)',
    'docs/index.html delivery vocabulary (spike)',
  ]);
});

test('a phrase is read across a soft line break and inline markup, never from one block into the next', () => {
  const findings = findingsFor({
    'docs/a.html':
      '<table><tr><td>not enrolled in the session</td></tr><tr><td>3</td></tr></table><p>in <em>session</em>\n6</p>',
    'docs/b.html': '<ul><li>the plan</li><li>section C</li></ul>',
    'llms.mdx/docs/a/content.md':
      '| a | b |\n|---|---|\n| in the session | x |\n| 3 | y |\n\nas plan\nsection C expects\n',
  });
  assert.deepEqual(findings, [
    'docs/a.html delivery process (story <n>, session <n>)',
    'llms.mdx/docs/a/content.md private plan citation (plan section)',
  ]);
});

test("a link's or an image's title is rendered text in a page's Markdown, a block of its own, and its destination is not", () => {
  const findings = findingsFor({
    'llms.mdx/docs/a/content.md': '# A\n\nRead [the page](/docs/a "since Wave 4").\n',
    'llms.mdx/docs/b/content.md': '# B\n\n![Cover](/cover.png "decision W11")\n',
    'llms.mdx/docs/c/content.md': '# C\n\nin session [six](/docs/Wave/spike#123 "6") more\n',
    'llms.mdx/docs/d/content.md': '# D\n\nas plan [section](/docs/a "the plan") C expects\n',
  });
  assert.deepEqual(findings, [
    'llms.mdx/docs/a/content.md delivery vocabulary (Wave, Phase)',
    'llms.mdx/docs/b/content.md tracker code (W<n>)',
    'llms.mdx/docs/d/content.md private plan citation (plan section)',
  ]);
});

test('markup is no text: a colour, a script, a character reference, a heading id and a link destination pass', () => {
  const findings = findingsFor({
    'docs/index.html':
      '<div style="color:#615fff" data-x="#123"><script>self.push("#456 Wave")</script><style>a{color:#121212}</style>It&#39;s here</div>',
    '_next/static/app.css': '.accent{color:#615fff}',
    'llms.mdx/docs/content.md': '## 12 steps [#12-steps]\n\nRead [the page](/docs/a#12-steps).\n',
  });
  assert.deepEqual(findings, []);
});
