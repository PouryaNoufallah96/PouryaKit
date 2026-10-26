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

- **No `public/` directory**, top-level or nested, and no `.vscode/`: they were the 2026-10-25 incident's payload paths, and `check-tree.py` refuses either segment anywhere. Favicon, Open Graph image and icons use the Next.js app-directory file conventions (`app/icon.png`, `app/opengraph-image.tsx`) and static imports.
- **Design**: shadcn tokens in OKLCH with Fumadocs' `shadcn.css` and `preset.css`; light background `oklch(1 0 0)`, dark `oklch(0.16 0.004 285)`; one accent, Cobalt Indigo `oklch(0.585 0.233 277.117)`; Geist and Geist Mono through `next/font`; radius 0.625rem; cards rounded-xl with a 2px muted border and no shadow at rest; a neutral focus ring; reduced motion respected. No gradient text, side stripes, glassmorphism or em dashes. Badges are native components; no external badge host.
- **Page skeleton**: title and one-line description; what it is; the key caveat; when to use it; steps with code; a guarantee table; failure messages; next steps.
- **Dependencies**: pnpm with the committed `website/pnpm-lock.yaml`, installed with `--frozen-lockfile`; install scripts run only for the packages in `onlyBuiltDependencies`; every npm package carries a permissive licence (ADR 0002). CI's `website` job installs, lints and builds the site.
- **Ultrabusiness** (the owner's docs app) is a pattern source only: reimplement its patterns, never copy its code, and run nothing in its checkout.
