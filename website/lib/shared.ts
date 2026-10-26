import { createGetUrl } from 'fumadocs-core/source';

export const siteName = 'PouryaKit';
export const siteDescription =
  'A reusable .NET 10 foundation for modular monoliths, in which Feature code holds only business behaviour.';

export const docsRoute = '/docs';
const docsMarkdownRoute = '/llms.mdx/docs';
const docsImageRoute = '/og/docs';

// The search index is a file in the export, which the browser downloads and searches (Fumadocs static mode). Its .json
// suffix keeps it a file: with trailingSlash, Next.js redirects a path whose last segment has no dot to <path>/.
export const searchIndexRoute = '/api/search.json';

// The site's own address, for absolute Open Graph URLs: SITE_URL when a deployment sets it, else the production host
// Vercel's build sets, else the local server.
const vercelHost = process.env.VERCEL_PROJECT_PRODUCTION_URL;
export const siteUrl = new URL(
  process.env.SITE_URL ?? (vercelHost ? `https://${vercelHost}` : 'http://localhost:3000'),
);

// The site's default Open Graph image (app/og/image.png/route.tsx).
export const defaultImageUrl = '/og/image.png';

const getMarkdownUrl = createGetUrl(docsMarkdownRoute);
const getImageUrl = createGetUrl(docsImageRoute);

// Each page's Markdown is a static file at /llms.mdx/docs/<slugs>/content.md: an export has no rewrite to serve it at
// the page's own URL with a .md suffix.
export function pageMarkdownUrl(page: { slugs: string[] }) {
  const segments = [...page.slugs, 'content.md'];
  return { segments, url: getMarkdownUrl(segments) };
}

// Each page's Open Graph image is a static file at /og/docs/<slugs>/image.png.
export function pageImageUrl(page: { slugs: string[] }) {
  const segments = [...page.slugs, 'image.png'];
  return { segments, url: getImageUrl(segments) };
}
