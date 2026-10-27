import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import path from 'node:path';

// The Kit's sources, as paths relative to the repository root.
export const SKILL = '.agents/skills/pourya-kit/SKILL.md';
export const REFERENCES = '.agents/skills/pourya-kit/references';
export const GLOSSARY = 'GLOSSARY.md';
export const PACKAGES = 'src';
export const INVENTORY = 'scripts/packaging/inventory.json';

// The site's own files, relative to the repository root.
export const WEBSITE = 'website';
export const CAPABILITY_TOPS = 'website/content/capabilities';

/** The repository root: the closest directory above `start` that holds the Kit skill. */
export function findRoot(start = process.cwd()): string {
  let directory = path.resolve(start);
  for (;;) {
    if (existsSync(path.join(directory, SKILL))) return directory;
    const parent = path.dirname(directory);
    if (parent === directory) throw new Error(`kit-sources: no ${SKILL} above ${start}`);
    directory = parent;
  }
}

/** A path inside the repository, refused when it would leave it. */
export function resolveInside(root: string, relative: string): string {
  const resolved = path.resolve(root, relative);
  if (path.isAbsolute(relative) || !resolved.startsWith(path.resolve(root) + path.sep)) {
    throw new Error(`kit-sources: ${JSON.stringify(relative)} is not a path inside the repository`);
  }
  return resolved;
}

export function readSource(root: string, relative: string): string {
  return readFileSync(resolveInside(root, relative), 'utf8');
}

export function sourceExists(root: string, relative: string): boolean {
  return existsSync(resolveInside(root, relative));
}

export function isDirectory(root: string, relative: string): boolean {
  const resolved = resolveInside(root, relative);
  return existsSync(resolved) && statSync(resolved).isDirectory();
}

export function listDirectory(root: string, relative: string): string[] {
  return readdirSync(resolveInside(root, relative)).sort();
}

/** A repository path in the POSIX form the route map keys use. */
export function posix(relative: string): string {
  return relative.split(path.sep).join('/');
}
