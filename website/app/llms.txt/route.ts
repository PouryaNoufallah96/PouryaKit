import { docsLlms } from '@/lib/source';

// The llms.txt index of every page, written once at build time.
export const dynamic = 'force-static';
export const revalidate = false;

export function GET() {
  return new Response(docsLlms.index(), { headers: { 'Content-Type': 'text/plain; charset=utf-8' } });
}
