// The site build's two steps around `next build`, and the ADR manifest's refresh (package.json's scripts run them):
//   generate      writes the pages projected from the Kit's sources (lib/kit-sources/site.ts)
//   check-export  holds out/ to the rules, failing the build on any finding (lib/kit-sources/export-check.ts)
//   adr-manifest  writes the passages the ADR projection leaves out, for review in the diff
import { writeFileSync } from 'node:fs';
import path from 'node:path';
import { checkExport } from '../lib/kit-sources/export-check.ts';
import { findRoot } from '../lib/kit-sources/paths.ts';
import { ADR_MANIFEST, adrExclusions, buildSite, projectAdrs, writeSite } from '../lib/kit-sources/site.ts';

const root = findRoot();
const command = process.argv[2];

try {
  if (command === 'generate') {
    const files = buildSite(root);
    writeSite(root, files);
    console.log(`kit-sources: wrote ${files.length} generated files`);
  } else if (command === 'check-export') {
    const { findings, scanned, rendered } = checkExport(path.join(root, 'website', 'out'));
    for (const finding of findings) console.error(`${finding.file}: ${finding.rule}: ${JSON.stringify(finding.match)}`);
    if (findings.length > 0) throw new Error(`kit-sources: out/ breaks the rules ${findings.length} times`);
    console.log(`kit-sources: out/ passes the rules (${scanned} files read, ${rendered} as rendered text)`);
  } else if (command === 'adr-manifest') {
    const exclusions = adrExclusions(projectAdrs(root));
    writeFileSync(path.join(root, ADR_MANIFEST), `${JSON.stringify(exclusions, null, 2)}\n`);
    console.log(`kit-sources: ${ADR_MANIFEST} lists ${exclusions.length} passages left out`);
  } else {
    throw new Error('usage: node scripts/kit-content.ts generate | check-export | adr-manifest');
  }
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
  process.exit(1);
}
