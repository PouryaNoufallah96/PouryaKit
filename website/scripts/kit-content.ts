// The site build's two steps around `next build` (package.json's scripts run them):
//   generate      writes the pages projected from the Kit's sources (lib/kit-sources/site.ts)
//   check-export  holds out/ to the rules, and each package README's links to out/'s pages, failing the build on any
//                 finding (lib/kit-sources/export-check.ts, lib/kit-sources/readmes.ts)
import path from 'node:path';
import { checkExport } from '../lib/kit-sources/export-check.ts';
import { findRoot } from '../lib/kit-sources/paths.ts';
import { readmeLinkViolations } from '../lib/kit-sources/readmes.ts';
import { buildSite, writeSite } from '../lib/kit-sources/site.ts';

const root = findRoot();
const command = process.argv[2];

try {
  if (command === 'generate') {
    const files = buildSite(root);
    writeSite(root, files);
    console.log(`kit-sources: wrote ${files.length} generated files`);
  } else if (command === 'check-export') {
    const out = path.join(root, 'website', 'out');
    const { findings, scanned, rendered } = checkExport(out);
    for (const finding of findings) console.error(`${finding.file}: ${finding.rule}: ${JSON.stringify(finding.match)}`);
    if (findings.length > 0) throw new Error(`kit-sources: out/ breaks the rules ${findings.length} times`);
    const links = readmeLinkViolations(root, out);
    if (links.length > 0) throw new Error(`kit-sources: ${links.join('\n')}`);
    console.log(`kit-sources: out/ passes the rules (${scanned} files read, ${rendered} as rendered text)`);
  } else {
    throw new Error('usage: node scripts/kit-content.ts generate | check-export');
  }
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
}
