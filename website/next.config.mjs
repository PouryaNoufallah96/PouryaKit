import { createMDX } from 'fumadocs-mdx/next';

const withMDX = createMDX();

/** @type {import('next').NextConfig} */
const config = {
  // A static export: every route is a file in out/, so Next.js refuses rewrites, redirects, headers, a proxy and any
  // route handler that reads the request.
  output: 'export',
  // Each page is written as <route>/index.html, which any static file server finds.
  trailingSlash: true,
  // No image optimiser runs in an export, and sharp is not installed (pnpm-workspace.yaml).
  images: { unoptimized: true },
  reactStrictMode: true,
};

export default withMDX(config);
