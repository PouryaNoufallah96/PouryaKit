import { notFound } from 'next/navigation';
import { ImageResponse } from 'next/og';
import { OgCard, ogSize } from '@/components/og-card';
import { pageImageUrl, siteName } from '@/lib/shared';
import { source } from '@/lib/source';

// One page's Open Graph image, written at build time as /og/docs/<slugs>/image.png for each page.
export const dynamic = 'force-static';
export const revalidate = false;

export async function GET(_request: Request, { params }: { params: Promise<{ slug: string[] }> }) {
  const { slug } = await params;
  const page = source.getPage(slug.slice(0, -1));
  if (!page) notFound();

  return new ImageResponse(
    <OgCard eyebrow={siteName} title={page.data.title} description={page.data.description} />,
    ogSize,
  );
}

export function generateStaticParams() {
  return source.getPages().map((page) => ({ slug: pageImageUrl(page).segments }));
}
