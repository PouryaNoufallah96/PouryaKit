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
  'npx skills add PouryaNoufallah96/PouryaKit',
];

test('every shape of a URL or name GitHub resolves to the private repository fails, whatever its case', () => {
  for (const shape of privateShapes) {
    assert.ok(privateRepositoryReferences(`see ${shape} here`).length > 0, shape);
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

test("the private plan's citations fail in text: a decision of section G, a row of section I and a section", () => {
  for (const text of ['as G.6 decides', 'plan I.3', 'the I.3 row', 'or section G.', 'Plan section D sketched']) {
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
  ]) {
    assert.ok(addressViolations(`at ${path}`).length > 0, path);
  }
  for (const text of ['https://example.com/docs', '/docs/reference/glossary', 'ratio 3:2', 'a:<id>']) {
    assert.deepEqual(addressViolations(text), [], text);
  }
});

test('delivery vocabulary and tracker codes fail in text, and W3C or a word in lower case does not', () => {
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
  ]) {
    assert.ok(vocabularyViolations(text).length > 0, text);
  }
  for (const text of [
    'W3C trace context',
    'a microwave',
    'a spike in latency',
    'the first phase',
    'item #1',
    'C# 14',
  ]) {
    assert.deepEqual(vocabularyViolations(text), [], text);
  }
});
