// The public repository's tree (lib/kit-sources/distribution.ts). `pnpm --dir website sync-public` and then:
//   [<directory>]                       writes the tree to website/distribution/out/, which it empties first, or to a
//                                       directory that is absent or empty (relative to website/), then compares it
//                                       with the manifest
//   --check                             regenerates the tree and fails when any file differs from the manifest, or is
//                                       missing from it or extra
//   --write-manifest                    rewrites website/distribution/manifest.sha256 after a reviewed source change
//   --verify-remote <tag> [--remote <url>]
//                                       fails unless the public repository's default branch is the commit <tag> names
//                                       and that commit holds exactly the manifest's files and hashes
import { mkdirSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import {
  assembleTree,
  formatManifest,
  MANIFEST,
  manifestDifferences,
  manifestOf,
  OUTPUT,
  PUBLIC_REMOTE,
  readManifest,
  type TreeFile,
  verifyRemote,
} from '../lib/kit-sources/distribution.ts';
import { findRoot, resolveInside, sourceExists } from '../lib/kit-sources/paths.ts';

const root = findRoot();
const args = process.argv.slice(2);

// Files per top-level folder, for a one-line summary.
function counts(files: TreeFile[]): string {
  const folders = new Map<string, number>();
  for (const entry of files) {
    const folder = entry.path.split('/').slice(0, -1).join('/') || '.';
    folders.set(folder, (folders.get(folder) ?? 0) + 1);
  }
  return [...folders].map(([folder, count]) => `${folder} ${count}`).join(', ');
}

// Fails, naming each difference, when the files are not the manifest's.
function compare(files: TreeFile[]) {
  const differences = manifestDifferences(readManifest(root), manifestOf(files));
  for (const difference of differences) console.error(difference);
  if (differences.length > 0) {
    throw new Error(
      `sync-public: the tree differs from ${MANIFEST} ${differences.length} times; review the change, then run pnpm --dir website sync-public --write-manifest`,
    );
  }
}

function absentOrEmpty(directory: string) {
  try {
    return readdirSync(directory).length === 0;
  } catch {
    return true;
  }
}

function write(target: string | undefined) {
  const directory = target === undefined ? resolveInside(root, OUTPUT) : path.resolve(target);
  if (target === undefined) rmSync(directory, { recursive: true, force: true });
  else if (!absentOrEmpty(directory)) {
    throw new Error(`sync-public: ${directory} is not empty; give an empty or absent directory`);
  }
  const files = assembleTree(root);
  for (const entry of files) {
    const destination = path.join(directory, entry.path);
    mkdirSync(path.dirname(destination), { recursive: true });
    writeFileSync(destination, entry.content);
  }
  console.log(`sync-public: wrote ${files.length} files to ${directory} (${counts(files)})`);
  compare(files);
}

try {
  if (args[0] === '--check' && args.length === 1) {
    const files = assembleTree(root);
    compare(files);
    console.log(`sync-public: the tree matches ${MANIFEST} (${files.length} files: ${counts(files)})`);
  } else if (args[0] === '--write-manifest' && args.length === 1) {
    const files = assembleTree(root);
    const previous = sourceExists(root, MANIFEST) ? readManifest(root) : new Map<string, string>();
    const differences = manifestDifferences(previous, manifestOf(files));
    writeFileSync(resolveInside(root, MANIFEST), formatManifest(manifestOf(files)));
    for (const difference of differences) console.log(difference);
    console.log(`sync-public: wrote ${MANIFEST} (${files.length} files, ${differences.length} changed)`);
  } else if (args[0] === '--verify-remote' && (args.length === 2 || (args.length === 4 && args[2] === '--remote'))) {
    const remote = args[3] ?? PUBLIC_REMOTE;
    const { branch, commit, problems } = verifyRemote(remote, args[1], readManifest(root));
    for (const problem of problems) console.error(problem);
    if (problems.length > 0) throw new Error(`sync-public: ${remote} at ${args[1]} is not the reviewed tree`);
    console.log(`sync-public: ${remote} publishes the reviewed tree: ${args[1]} and ${branch} at ${commit}`);
  } else if (args.length <= 1 && !args[0]?.startsWith('-')) {
    write(args[0]);
  } else {
    throw new Error(
      'usage: node scripts/sync-public.ts [<directory>] | --check | --write-manifest | --verify-remote <tag> [--remote <url>]',
    );
  }
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
}
