// Typesets the Kit's covers and the shared package icon onto the committed textless artwork in covers/art/, with
// next/og's ImageResponse (the Satori and resvg typesetter of the site's Open Graph images) and the Geist fonts
// committed in covers/fonts/, so the model never draws a letter. next/og fetches a glyph those fonts lack from Google
// Fonts; covers.test.mjs keeps every string printable ASCII, which they hold, so nothing is fetched.
// `pnpm --dir website covers` writes covers/out/kit.png, covers/out/covers/<id>.png for each package of
// scripts/packaging/inventory.json (the list the site and the public repository read, lib/kit-sources/packages.ts), set
// with its family and tagline from covers/covers.json, and the repository's src/icon.png, then prints each file's
// sha256: the same artwork gives byte-identical files.
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { relative } from 'node:path';
import { fileURLToPath } from 'node:url';
// next has no exports map, so Node's own resolver needs the file name.
import { ImageResponse } from 'next/og.js';
import { createElement as h, type ReactElement } from 'react';
import { readPackageIds } from '../lib/kit-sources/packages.ts';

type Covers = {
  kit: { eyebrow: string; title: string; tagline: string };
  families: Record<string, string>;
  packages: { id: string; family: string; tagline: string }[];
};

const cover = { width: 1280, height: 640 };
const icon = 128;
// The largest cover a README may show: nuget.org and GitHub get the same file.
const maxBytes = 1_048_576;
// Characters per line that stay inside the text column: a title line at 56px, a tagline line at 26px.
const titleBudget = 19;
const taglineBudget = 40;

const site = new URL('../', import.meta.url);
const root = new URL('../', site);
const covers = new URL('covers/', site);
const out = new URL('out/', covers);
const data: Covers = JSON.parse(readFileSync(new URL('covers.json', covers), 'utf8'));

const font = (file: string) => readFileSync(new URL(`fonts/${file}`, covers));
const fonts = [
  { name: 'Geist', data: font('Geist-Regular.ttf'), weight: 400 as const, style: 'normal' as const },
  { name: 'Geist', data: font('Geist-SemiBold.ttf'), weight: 600 as const, style: 'normal' as const },
  { name: 'Geist Mono', data: font('GeistMono-Medium.ttf'), weight: 500 as const, style: 'normal' as const },
];

function art(name: string): string {
  return `data:image/png;base64,${readFileSync(new URL(`art/${name}.png`, covers)).toString('base64')}`;
}

// Satori measures a word as the sum of its letters but draws it kerned, so the space after a word with kerned pairs
// comes out wider than the space after one without. Each line is therefore one word, its spaces no-break spaces, and
// this script breaks the lines itself: greedily, by a character budget.
function setLines(words: string[], joiner: string, budget: number, most: number, what: string): string[] {
  const lines: string[] = [];
  for (const word of words) {
    const last = lines.at(-1);
    if (last !== undefined && (last + joiner + word).length <= budget) lines[lines.length - 1] = last + joiner + word;
    else lines.push(word);
  }
  if (lines.length > most || lines.some((line) => line.length > budget)) {
    throw new Error(`${what} does not fit ${most} lines of ${budget} characters: ${JSON.stringify(lines)}.`);
  }
  return lines.map((line) => line.replaceAll(' ', '\u00a0'));
}

// next/og lays out with flexbox only, so every element with children is a flex container.
function frame(artwork: string, eyebrow: string, title: ReactElement, tagline: string): ReactElement {
  return h(
    'div',
    {
      style: {
        display: 'flex',
        position: 'relative',
        width: '100%',
        height: '100%',
        borderRadius: 28,
        overflow: 'hidden',
        backgroundColor: '#0d0d0f',
      },
    },
    h('img', {
      src: artwork,
      width: cover.width,
      height: cover.height,
      style: { position: 'absolute', top: 0, left: 0 },
    }),
    // A dark wash under the text column keeps the type legible whatever the artwork holds there.
    h('div', {
      style: {
        position: 'absolute',
        top: 0,
        left: 0,
        width: 820,
        height: cover.height,
        backgroundImage:
          'linear-gradient(90deg, rgba(13,13,15,0.92) 0%, rgba(13,13,15,0.78) 45%, rgba(13,13,15,0) 100%)',
      },
    }),
    h(
      'div',
      {
        style: {
          position: 'absolute',
          top: 0,
          left: 80,
          width: 560,
          height: cover.height,
          display: 'flex',
          flexDirection: 'column',
          justifyContent: 'center',
          fontFamily: 'Geist',
          color: '#fafafa',
        },
      },
      h(
        'div',
        { style: { display: 'flex', alignItems: 'center', gap: 14, marginBottom: 28 } },
        h('div', { style: { width: 14, height: 14, borderRadius: 4, backgroundColor: '#615fff' } }),
        h(
          'div',
          {
            style: {
              fontFamily: 'Geist Mono',
              fontWeight: 500,
              fontSize: 20,
              letterSpacing: 3,
              textTransform: 'uppercase',
              color: '#a5a4ff',
            },
          },
          eyebrow.replaceAll(' ', '\u00a0'),
        ),
      ),
      title,
      h(
        'div',
        {
          style: {
            display: 'flex',
            flexDirection: 'column',
            marginTop: 28,
            fontSize: 26,
            fontWeight: 400,
            lineHeight: 1.4,
            color: '#c8c8d0',
          },
        },
        ...setLines(tagline.split(' '), ' ', taglineBudget, 2, `The tagline "${tagline}"`).map((line) =>
          h('div', { key: line, style: { display: 'flex' } }, line),
        ),
      ),
    ),
    // The edge: a 2px muted border, drawn over the artwork so the picture keeps its pixels.
    h('div', {
      style: {
        position: 'absolute',
        top: 0,
        left: 0,
        width: cover.width,
        height: cover.height,
        borderRadius: 28,
        border: '2px solid #26262e',
      },
    }),
  );
}

// A package ID: its PouryaKit prefix dimmed on a line of its own, the rest broken only after a dot.
function packageTitle(id: string): ReactElement {
  const [prefix, ...segments] = id.split('.');
  const words = segments.map((segment, index) => (index < segments.length - 1 ? `${segment}.` : segment));
  return h(
    'div',
    { style: { display: 'flex', flexDirection: 'column', fontSize: 56, fontWeight: 600, lineHeight: 1.12 } },
    h('div', { style: { display: 'flex', color: '#8e8e9a' } }, `${prefix}.`),
    ...setLines(words, '', titleBudget, 2, `The ID ${id}`).map((line) =>
      h('div', { key: line, style: { display: 'flex' } }, line),
    ),
  );
}

function kitTitle(title: string): ReactElement {
  return h('div', { style: { display: 'flex', fontSize: 88, fontWeight: 600 } }, title);
}

async function render(element: ReactElement, width: number, height: number): Promise<Buffer> {
  const response = new ImageResponse(element, { width, height, fonts });
  return Buffer.from(await response.arrayBuffer());
}

// resvg samples an image bicubically with no prefilter, so one step from 1024px to 128px turns the artwork's thin
// glowing edges into dots; halving three times keeps them whole. The last step rounds the corners.
async function iconFrom(artwork: string): Promise<Buffer> {
  let source = artwork;
  for (let size = 512; size > icon; size /= 2) {
    const step = await render(h('img', { src: source, width: size, height: size }), size, size);
    source = `data:image/png;base64,${step.toString('base64')}`;
  }
  return render(
    h(
      'div',
      { style: { display: 'flex', width: '100%', height: '100%', borderRadius: 24, overflow: 'hidden' } },
      h('img', { src: source, width: icon, height: icon }),
    ),
    icon,
    icon,
  );
}

// A PNG's IHDR holds its width and height at bytes 16 and 20.
function write(target: URL, png: Buffer, width: number, height: number) {
  const size = { width: png.readUInt32BE(16), height: png.readUInt32BE(20) };
  if (size.width !== width || size.height !== height) {
    throw new Error(`${target.pathname} is ${size.width}x${size.height}, not ${width}x${height}.`);
  }
  if (png.length > maxBytes) throw new Error(`${target.pathname} is ${png.length} bytes, over ${maxBytes}.`);
  writeFileSync(target, png);
  const hash = createHash('sha256').update(png).digest('hex');
  console.log(`${hash}  ${String(png.length).padStart(7)}  ${relative(fileURLToPath(root), fileURLToPath(target))}`);
}

const ids = readPackageIds(fileURLToPath(root));
const entries = new Map(data.packages.map((entry) => [entry.id, entry]));
for (const { id, family } of data.packages) {
  if (!ids.includes(id)) throw new Error(`covers.json has an entry for ${id}, which is no package of the inventory.`);
  if (!data.families[family]) throw new Error(`${id} names the family ${family}, which covers.json does not list.`);
}
for (const id of ids) {
  if (!entries.has(id)) throw new Error(`covers.json has no entry for ${id}, a package of the inventory.`);
}

mkdirSync(new URL('covers/', out), { recursive: true });
const { eyebrow, title, tagline } = data.kit;
write(
  new URL('kit.png', out),
  await render(frame(art('kit'), eyebrow, kitTitle(title), tagline), cover.width, cover.height),
  cover.width,
  cover.height,
);
for (const id of ids) {
  const { family, tagline } = entries.get(id) as Covers['packages'][number];
  const element = frame(art(family), data.families[family], packageTitle(id), tagline);
  write(new URL(`covers/${id}.png`, out), await render(element, cover.width, cover.height), cover.width, cover.height);
}
write(new URL('src/icon.png', root), await iconFrom(art('icon')), icon, icon);
