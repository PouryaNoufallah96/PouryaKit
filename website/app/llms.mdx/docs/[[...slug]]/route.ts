import { notFound } from 'next/navigation';
import { pageMarkdownUrl } from '@/lib/shared';
import { docsLlms, source } from '@/lib/source';

// One page's Markdown, written at build time as /llms.mdx/docs/<slugs>/content.md for each page; "Copy as Markdown"
// fetches it.
export const dynamic = 'force-static';
export const revalidate = false;

export async function GET(_request: Request, { params }: { params: Promise<{ slug?: string[] }> }) {
  const { slug = [] } = await params;
  const page = source.getPage(slug.slice(0, -1));
  if (!page) notFound();

  return new Response(await docsLlms.page(page), { headers: { 'Content-Type': 'text/markdown; charset=utf-8' } });
}

export function generateStaticParams() {
  return source.getPages().map((page) => ({ slug: pageMarkdownUrl(page).segments }));
}
