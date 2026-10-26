---
paths:
  - "website/**"
---

# Docs site

`website/` is the Kit's public documentation site, a Next.js static export. Everything it renders is read outside this private repository.

## What a page may say

- **English only.**
- **Nothing private in rendered text.** No link or text pointing at the private repository (`github.com/PouryaNoufallah96/PouryaKit`), no local machine path, no delivery vocabulary (Wave, Phase, Spike) and no tracker code (`W4`, `spec #183`, an issue number such as `#123`).
- **Facts live in the Kit skill.** A Capability's guarantees, options and rules are in `.agents/skills/pourya-kit/` and a package's purpose and quick start in its `src/<Id>/README.md`; a page explains and renders them, and never holds a fact the skill lacks. A new how-to fact goes into the skill first.
- **Names are real.** `check-docs.py` checks the backticked names of every `website/content/**/*.mdx` page against the code; the pre-commit hook refuses an `.mdx` file staged in part.

## How it is built

- **No `public/` directory**, top-level or nested, and no `.vscode/`: they were the 2026-10-25 incident's payload paths, and `check-tree.py` refuses either segment anywhere. The icon uses the Next.js app-directory file convention (`app/icon.svg`), images are static imports, and each Open Graph image is a static `.png` route drawn with `next/og` (`app/og/image.png/route.tsx`, `app/og/docs/[...slug]/route.tsx`).
- **A static export** (`output: 'export'`): every route handler is a force-static `GET`, with `generateStaticParams` where its path has segments; no rewrite, redirect, header, proxy or handler that reads the request, which Next.js refuses in an export. A page's Markdown is the static file `/llms.mdx/docs/<slugs>/content.md`, which "Copy as Markdown" fetches, and search runs in the browser on the static index `/api/search.json`; there is no AI chat.
- **Design**: shadcn tokens in OKLCH with Fumadocs' `shadcn.css` and `preset.css`; light background `oklch(1 0 0)`, dark `oklch(0.16 0.004 285)`; one accent, Cobalt Indigo `oklch(0.585 0.233 277.117)`; Geist and Geist Mono through `next/font`; radius 0.625rem; cards rounded-xl with a 2px muted border and no shadow at rest; a neutral focus ring; reduced motion respected. No gradient text, side stripes, glassmorphism or em dashes. Fumadocs Cards, Steps, Tabs, Accordions and titled code blocks, with the site's own Card and Callout in MDX (`components/card.tsx`, `components/callout.tsx`), since Fumadocs' draw a 1px border, a shadowed icon and a side stripe; "Copy as Markdown" on each page; the theme toggle on Mod+Shift+D; static Open Graph images through `next/og`. Badges are native components; no external badge host.
- **Page skeleton**: title and one-line description; what it is; the key caveat; when to use it; steps with code; a guarantee table; failure messages; next steps.
- **Dependencies**: exact versions in `website/package.json`, pnpm pinned by its `packageManager`, the committed `website/pnpm-lock.yaml` installed with `--frozen-lockfile`. pnpm's settings are in `website/pnpm-workspace.yaml` (pnpm 11 and later read no `pnpm` field in `package.json`): a dependency's install script runs only when `allowBuilds` lists it `true`, and an unlisted one fails the install; `ignoredOptionalDependencies` keeps out `sharp`, whose libvips is LGPL-3.0-or-later and which an export never loads.
- **Licences** (ADR 0002): `pnpm --dir website check-licences` (`scripts/check-licences.mjs`) holds every package of the npm closure, dev dependencies included, to the licence its own `package.json` declares. It admits MIT, Apache-2.0, BSD-2-Clause, BSD-3-Clause, ISC and 0BSD, an `OR` with one admitted side and an `AND` of admitted terms, and fails every other shape: a missing, unknown or custom licence. Two exceptions are admitted for their named packages only: `caniuse-lite` under CC-BY-4.0, browser-support data that browserslist reads at build time, a hard dependency of next, attribution only; and `lightningcss` with its platform binaries under MPL-2.0, Tailwind 4's CSS compiler, run only at build time, nothing of it in `out/`. A new licence or exception is an owner decision.
- **CI**: the `website` job installs, checks the licences, runs the check's tests (`pnpm --dir website test`), lints with Biome and builds the export; `.agents/scripts/ci-local.sh` runs the same steps.
- **Ultrabusiness** (the owner's docs app) is a pattern source only: reimplement its patterns, never copy its code, and run nothing in its checkout.
