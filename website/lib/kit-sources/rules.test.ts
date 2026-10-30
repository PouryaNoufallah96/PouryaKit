import assert from 'node:assert/strict';
import { test } from 'node:test';
import { addressViolations, privateRepositoryReferences, vocabularyViolations } from './rules.ts';

const privateShapes = [
  'https://github.com/PouryaNoufallah96/PouryaKit',
  'github.com/PouryaNoufallah96/pouryakit',
  'https://github.com/pouryanoufallah96/POURYAKIT',
  'https://github.com/PouryaNoufallah96/PouryaKit.git',
  'https://github.com/PouryaNoufallah96/PouryaKit/blob/main/README.md',
  'https://www.github.com/PouryaNoufallah96/PouryaKit/issues/68',
  'https://raw.githubusercontent.com/PouryaNoufallah96/PouryaKit/main/assets/kit.png',
  'https://cdn.jsdelivr.net/gh/PouryaNoufallah96/PouryaKit@v1/assets/kit.png',
  'https://codeload.github.com/PouryaNoufallah96/PouryaKit/zip/refs/heads/main',
  'https://api.github.com/repos/PouryaNoufallah96/PouryaKit/contents',
  'git@github.com:PouryaNoufallah96/PouryaKit.git',
  'https://pouryanoufallah96.github.io/PouryaKit/',
  'https://github.com/PouryaNoufallah96/PouryaKit.wiki',
  'https://github.com/PouryaNoufallah96/Pourya%4Bit',
  'https://github.dev/PouryaNoufallah96/PouryaKit',
  'npx skills add PouryaNoufallah96/PouryaKit',
  'see PouryaNoufallah96/PouryaKit/blob/main/x.cs',
  'https://github.com/PouryaNoufallah96/\nPouryaKit/blob/main/x.cs',
  'https://example.com/?u=https%3A%2F%2Fgithub.com%2FPouryaNoufallah96%2FPouryaKit',
  'https://www.jsdelivr.net/package/gh/PouryaNoufallah96/PouryaKit',
  '/PouryaNoufallah96/PouryaKit/blob/main/x.cs',
];

test('every shape of a URL or name GitHub resolves to the private repository fails, whatever its case', () => {
  for (const shape of privateShapes) {
    assert.ok(privateRepositoryReferences(`see ${shape} here`).length > 0, shape);
  }
});

test('a reference to the private repository fails however the sentence around it ends', () => {
  const ends = ['.', ',', ';', ':', '!', '?', '...', '**', '.git.', '@v1.'];
  for (const shape of ['https://github.com/PouryaNoufallah96/PouryaKit', 'github.com/PouryaNoufallah96/PouryaKit']) {
    for (const end of ends) {
      assert.ok(privateRepositoryReferences(`see ${shape}${end} and more`).length > 0, `${shape}${end} mid-sentence`);
      assert.ok(privateRepositoryReferences(`see ${shape}${end}`).length > 0, `${shape}${end} at the end`);
    }
  }
  for (const end of ['.', ',', ';', '!']) {
    assert.ok(privateRepositoryReferences(`add PouryaNoufallah96/PouryaKit${end}`).length > 0, `shorthand${end}`);
  }
  for (const shape of [
    'github.com/PouryaNoufallah96/PouryaKit-public.',
    'github.com/PouryaNoufallah96/PouryaKit.Core.',
    'PouryaNoufallah96/pouryakit-public, and',
  ]) {
    assert.deepEqual(privateRepositoryReferences(shape), [], shape);
  }
});

test('another repository of the owner, the owner alone and another owner pass, so the rule compares names, not substrings', () => {
  for (const shape of [
    'https://github.com/PouryaNoufallah96/pouryakit-public',
    'github.com/PouryaNoufallah96/PouryaKit-public/blob/main/README.md',
    'https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/assets-v1/assets/kit.png',
    'npx skills add PouryaNoufallah96/pouryakit-public',
    'https://github.com/PouryaNoufallah96',
    'https://github.com/someone/PouryaKit',
    'https://www.nuget.org/packages/PouryaKit.Core',
    'PouryaKit.Persistence/EFCore',
  ]) {
    assert.deepEqual(privateRepositoryReferences(shape), [], shape);
  }
});

test('a dot segment is resolved as a browser resolves it, escaped or not, on every host the rule reads', () => {
  for (const shape of [
    'https://github.com/PouryaNoufallah96/pouryakit-public/../PouryaKit',
    'github.com/PouryaNoufallah96/pouryakit-public/../PouryaKit',
    'https://github.com/PouryaNoufallah96/pouryakit-public/%2e%2e/PouryaKit',
    'https://github.com/PouryaNoufallah96/pouryakit-public/%2E./PouryaKit',
    'https://github.com/PouryaNoufallah96/pouryakit-public/.%2e/PouryaKit',
    'https://github.com/someone/./../PouryaNoufallah96/%2e/PouryaKit/blob/main/x.cs',
    'https://github.com/PouryaNoufallah96%2Fpouryakit-public%2F..%2FPouryaKit',
    'https://github.com/PouryaNoufallah96/x%2F../../PouryaKit',
    'https://example.com/?u=https%3A%2F%2Fgithub.com%2FPouryaNoufallah96%2Fpouryakit-public%2F..%2FPouryaKit',
    'https://github.com/PouryaNoufallah96/pouryakit-public/..\\PouryaKit',
    'https://github.com:443/PouryaNoufallah96/pouryakit-public/../PouryaKit',
    'https://raw.githubusercontent.com/PouryaNoufallah96/pouryakit-public/../PouryaKit/main/x.md',
    'https://cdn.jsdelivr.net/gh/PouryaNoufallah96/pouryakit-public/../PouryaKit@v1/x.md',
    'https://api.github.com/repos/PouryaNoufallah96/pouryakit-public/../PouryaKit/contents',
    'https://pouryanoufallah96.github.io/pouryakit-public/../PouryaKit/',
    'git@github.com:PouryaNoufallah96/pouryakit-public/../PouryaKit.git',
  ]) {
    assert.ok(privateRepositoryReferences(`see ${shape} here`).length > 0, shape);
  }
});

test('an escape that stays in a path keeps its segment, so the dot segment after it is still resolved', () => {
  for (const shape of [
    'https://github.com/PouryaNoufallah96/pouryakit-public%3F/../PouryaKit',
    'https://github.com/PouryaNoufallah96/pouryakit-public%23/../PouryaKit',
    'https://github.com/PouryaNoufallah96/pouryakit-public%20x/../PouryaKit',
    'https://github.com/PouryaNoufallah96/x%22/../PouryaKit',
  ]) {
    assert.ok(privateRepositoryReferences(`see ${shape} here`).length > 0, shape);
  }
  for (const shape of [
    'https://github.com/PouryaNoufallah96/pouryakit-public?x=/../PouryaKit',
    'https://github.com/PouryaNoufallah96/pouryakit-public#/../PouryaKit',
  ]) {
    assert.deepEqual(privateRepositoryReferences(shape), [], shape);
  }
});

test('a climb past the root stops there and reads the pair that remains, and throws on none', () => {
  for (const shape of [
    'https://github.com/../PouryaNoufallah96/PouryaKit',
    'https://github.com/../../../PouryaNoufallah96/x/../PouryaKit',
    'https://github.com//../PouryaNoufallah96/pouryakit-public/%2e%2e/PouryaKit',
  ]) {
    assert.ok(privateRepositoryReferences(shape).length > 0, shape);
  }
  for (const shape of [
    'https://github.com/PouryaNoufallah96/x/../pouryakit-public',
    'https://github.com/../PouryaNoufallah96/pouryakit-public/blob/main/x.md',
    'https://github.com/PouryaNoufallah96/pouryakit-public/x/../../../PouryaKit',
    'https://github.com/PouryaNoufallah96/pouryakit-public//../PouryaKit',
    'https://github.com/someone/x/../PouryaKit',
    'https://github.com/..',
    'https://github.com/../..',
    'https://github.com/.',
    'https://cdn.jsdelivr.net/..',
    'https://pouryanoufallah96.github.io/..',
    'https://api.github.com/repos/..',
  ]) {
    assert.deepEqual(privateRepositoryReferences(shape), [], shape);
  }
});

test("the private plan's citations fail in text: a decision of section G, a row of section I and a section", () => {
  for (const text of [
    'as G.6 decides',
    'plan I.3',
    'the I.3 row',
    'or section G.',
    'Plan section D sketched',
    'as plan\nsection C expects',
    'see spec\n  #183',
    'Section G decides',
    'as plan\u00a0section C expects',
  ]) {
    assert.ok(vocabularyViolations(text).length > 0, text);
  }
  for (const text of ['GDPR', 'IEEE 754', 'the G string', 'section 4']) {
    assert.deepEqual(vocabularyViolations(text), [], text);
  }
});

test("the owner's private projects fail by name, whole words in any case, markup included; the public ones pass", () => {
  for (const text of [
    'as Cybernod does',
    '`cybernod@bae8ac3 src/x.cs`',
    'CYBERNOD',
    'Ultrabusiness',
    'ultra-business',
    'UltraBusiness docs',
  ]) {
    assert.ok(addressViolations(text).length > 0, text);
  }
  for (const text of [
    'cybernodes',
    'CloudNativeKit',
    'meysamhadeli/woo',
    'food-delivery-microservices',
    'kgrzybek/modular-monolith-with-ddd',
    'vertical-slice-api-template',
  ]) {
    assert.deepEqual(addressViolations(text), [], text);
  }
});

test('a local path fails: a macOS or Linux home, the macOS temporary directory and a Windows drive', () => {
  for (const path of [
    '/Users/ada/kit',
    '/home/ada/kit',
    '/private/tmp/kit',
    'C:\\Users\\ada\\kit',
    'D:\\\\work\\\\kit',
    'D:\\repo',
    'D:/repo',
  ]) {
    assert.ok(addressViolations(`at ${path}`).length > 0, path);
  }
  for (const text of ['https://example.com/docs', '/docs/reference/glossary', 'ratio 3:2', 'a:<id>']) {
    assert.deepEqual(addressViolations(text), [], text);
  }
});

test('delivery vocabulary and tracker codes fail in text, and W3C or Wave and Phase in lower case do not', () => {
  for (const text of [
    'in Wave 3',
    'Waves of work',
    'the next Phase',
    'The Spike code',
    'two Spikes ran',
    'decision W11',
    'see spec #183',
    'Spec #115 decided',
    'fixed in #12',
    'Shape for ticket #7',
    'tickets #9 and #10',
    'Issue #4 asked',
  ]) {
    assert.ok(vocabularyViolations(text).length > 0, text);
  }
  for (const text of ['W3C trace context', 'a microwave', 'the first phase', 'item #1', 'C# 14']) {
    assert.deepEqual(vocabularyViolations(text), [], text);
  }
});

test('the word spike fails in any case, in a path and inside an identifier', () => {
  for (const text of [
    'a spike in latency',
    'The spike that proved it',
    'dotnet test spikes/s1-atomic-commit',
    'spikes/saga/SagaSpike.Shipping/ShippingModule.cs',
    'SagaSpike.Kit',
    'tracestate=kit=spike-46',
    'SPIKE',
  ]) {
    assert.ok(vocabularyViolations(text).length > 0, text);
  }
  for (const text of ['a spoke', 'the spire', 'a pike']) {
    assert.deepEqual(vocabularyViolations(text), [], text);
  }
});

test('a story or a session of the delivery fails by its number; the words alone, and an orchestrator, pass', () => {
  for (const text of [
    'spec story 71',
    'Story 17 requires',
    'stories 70 and 71',
    'in session 6',
    "session 7's reports",
    'Story\u00a071 requires',
    'in session\u00a06',
  ]) {
    assert.ok(vocabularyViolations(text).length > 0, text);
  }
  for (const text of [
    'a user story',
    "the Marten session's commit",
    'one session per Operation',
    'an orchestrator restarts a Host',
    'the orchestrator would restart a healthy Host',
  ]) {
    assert.deepEqual(vocabularyViolations(text), [], text);
  }
});
