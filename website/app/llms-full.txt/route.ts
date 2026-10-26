import { docsLlms } from '@/lib/source';

// Every page's Markdown in one file, written once at build time.
export const dynamic = 'force-static';
export const revalidate = false;

export async function GET() {
  return new Response(await docsLlms.full(), { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
}
