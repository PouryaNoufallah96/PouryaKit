import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { test } from 'node:test';

const site = new URL('../', import.meta.url);
const covers = new URL('covers/', site);
const data = JSON.parse(readFileSync(new URL('covers.json', covers), 'utf8'));
const inventory = JSON.parse(readFileSync(new URL('../scripts/packaging/inventory.json', site), 'utf8'));

test('every package in the inventory has exactly one cover, and every cover is a package of it', () => {
  const ids = data.packages.map((cover) => cover.id);
  assert.equal(new Set(ids).size, ids.length, `a package has two covers: ${ids}`);
  assert.deepEqual(new Set(ids), new Set(inventory.packages.map((entry) => entry.id)));
});

// next/og fetches from Google Fonts any glyph the committed fonts lack. They hold every printable ASCII character and
// the no-break space covers.ts sets between words, so a cover's own strings stay within printable ASCII.
test('every string a cover sets is printable ASCII, which the committed fonts hold', () => {
  const { eyebrow, title, tagline } = data.kit;
  const strings = [eyebrow, title, tagline, ...Object.values(data.families)];
  for (const cover of data.packages) strings.push(cover.id, cover.tagline);
  for (const text of strings) assert.match(text, /^[ -~]+$/, `not printable ASCII: ${JSON.stringify(text)}`);
});

test('each artwork is a family, the Kit or the icon, at its size and within 1 MB, and every family has one', () => {
  const families = Object.keys(data.families);
  for (const { id, family } of data.packages) assert.ok(families.includes(family), `${id}: no family ${family}`);
  const art = readdirSync(new URL('art/', covers));
  assert.deepEqual(new Set(art), new Set([...families, 'kit', 'icon'].map((name) => `${name}.png`)));
  for (const file of art) {
    const png = readFileSync(new URL(`art/${file}`, covers));
    const size = [png.readUInt32BE(16), png.readUInt32BE(20)];
    assert.deepEqual(size, file === 'icon.png' ? [1024, 1024] : [1280, 640], file);
    assert.ok(png.length <= 1_048_576, `${file} is ${png.length} bytes`);
  }
});
