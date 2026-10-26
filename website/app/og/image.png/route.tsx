import { ImageResponse } from 'next/og';
import { OgCard, ogSize } from '@/components/og-card';
import { siteDescription, siteName } from '@/lib/shared';

// The site's default Open Graph image, written once at build time as /og/image.png; a docs page names its own.
export const dynamic = 'force-static';
export const revalidate = false;

export function GET() {
  return new ImageResponse(<OgCard eyebrow="Documentation" title={siteName} description={siteDescription} />, ogSize);
}
