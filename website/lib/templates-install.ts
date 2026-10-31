import { findRoot, readSource } from './kit-sources/paths.ts';

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
