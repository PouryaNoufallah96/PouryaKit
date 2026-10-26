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
    'decision W11',
    'see spec #183',
    'Spec #115 decided',
    'fixed in #12',
  ]) {
    assert.ok(vocabularyViolations(text).length > 0, text);
  }
  for (const text of ['W3C trace context', 'a microwave', 'item #1', 'C# 14']) {
    assert.deepEqual(vocabularyViolations(text), [], text);
  }
});
