import path from 'node:path';
import { ServerCodeBlock } from 'fumadocs-ui/components/codeblock.rsc';
import { findRoot } from '@/lib/kit-sources/paths.ts';
import { readSnippet } from '@/lib/kit-sources/snippets.ts';

/** A command, highlighted at build time and titled as the docs pages title theirs. */
export function CommandBlock({ code, title = 'Terminal' }: { code: string; title?: string }) {
  return <ServerCodeBlock code={code} lang="bash" codeblock={{ title }} />;
}

/**
 * A named region of the Proving Sample's or the tests' code, read at build time as the docs pages' `<Snippet>` reads
 * it: a missing file or region fails the build.
 */
export function KitSnippet({ file, region }: { file: string; region: string }) {
  const code = readSnippet(findRoot(), file, region);
  return <ServerCodeBlock code={code} lang="csharp" codeblock={{ title: path.posix.basename(file) }} />;
}
