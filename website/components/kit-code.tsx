import path from 'node:path';
import { ServerCodeBlock } from 'fumadocs-ui/components/codeblock.rsc';
import { findRoot, readSource } from '@/lib/kit-sources/paths.ts';
import { readSnippet } from '@/lib/kit-sources/snippets.ts';

// The templates' install command, with the published version it names, written once: the Get started and Templates
// pages include this file as a code block, and the landing reads it here.
const TEMPLATES_INSTALL = 'website/content/docs/get-started/install-templates.sh';

/** The install command of `PouryaKit.Templates` and the version it names, read at build time. */
export function templatesInstall(): { command: string; version: string } {
  const command = readSource(findRoot(), TEMPLATES_INSTALL).trim();
  const version = /^dotnet new install PouryaKit\.Templates@(\S+)$/.exec(command)?.[1];
  if (!version) throw new Error(`${TEMPLATES_INSTALL} holds no versioned install command: ${command}`);
  return { command, version };
}

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
