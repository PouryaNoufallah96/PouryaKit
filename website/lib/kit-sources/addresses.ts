// Where the Kit is published: one home for each address the site and the public repository's files name. This module
// imports nothing, so the site's browser code can read it too (lib/shared.ts).

/** The docs site's address: the site's default for its absolute URLs, and the link of the public repository's README. */
export const SITE_ADDRESS = 'https://pouryakit.vercel.app';

/** The public repository, as `npx skills add` and `claude plugin marketplace add` take it. */
export const PUBLIC_REPOSITORY = 'PouryaNoufallah96/pouryakit-pub';

/** The public repository's tag that README image URLs pin, so a published README never shows a later image. */
const ASSETS_TAG = 'assets-v1';

/** The public repository's `assets/` folder at that tag: `<ASSETS_URL>/covers/<id>.png`, `/kit.png`, `/icon.png`. */
export const ASSETS_URL = `https://raw.githubusercontent.com/${PUBLIC_REPOSITORY}/${ASSETS_TAG}/assets`;

/** The Claude Code plugin marketplace the public repository is, and its one plugin, the Kit's skill. */
export const MARKETPLACE = 'pouryakit';
export const PLUGIN = 'pourya-kit';

/** Who publishes the marketplace, as the repository's licence names the copyright holder. */
export const OWNER = 'Pourya Nofallah';
