// The site's licence check (ADR 0002, permissive licences only). Every package of the npm closure, dev dependencies
// included because build tools run on this machine, must declare a licence the allow-list admits. The closure is what
// `pnpm licenses list --json` reports, held against every package installed under node_modules/.pnpm so a package the
// report leaves out cannot pass unseen. Usage: `pnpm --dir website check-licences`; exits 1 and names each offender.
import { execFileSync } from 'node:child_process';
import { existsSync, lstatSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// SPDX identifiers any package may carry. Adding one is an owner decision.
export const PERMISSIVE = new Set(['MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'ISC', '0BSD']);

// Licences admitted for named packages only, each matched by the whole name.
export const EXCEPTIONS = [
  {
    licence: 'CC-BY-4.0',
    name: /^caniuse-lite$/,
    reason:
      'browser-support data that browserslist reads at build time, a hard dependency of next; attribution only, ' +
      'with no reciprocity and no commercial terms',
  },
  {
    licence: 'MPL-2.0',
    name: /^lightningcss(?:-(?:darwin|linux|win32|freebsd|android)-(?:x64|arm64|arm)(?:-gnu|-musl|-msvc|-gnueabihf)?)?$/,
    reason:
      "Tailwind 4's CSS compiler (@tailwindcss/node) and its platform binaries, run only at build time; nothing from " +
      'it ships in out/',
  },
];

function admitted(term, name) {
  return (
    PERMISSIVE.has(term) || EXCEPTIONS.some((exception) => exception.licence === term && exception.name.test(name))
  );
}

// What a term may be, an SPDX identifier's shape: no operator, parenthesis, space or empty text.
const IDENTIFIER = /^(?!(?:AND|OR|WITH)$)[A-Za-z0-9][A-Za-z0-9.-]*\+?$/;

// The grammar is choices (OR) of conjunctions (AND) of identifiers, and the whole expression must be in it before any
// branch counts, so every other shape fails, beside an admitted MIT too: parentheses, WITH, a custom text, an empty
// branch. In it, an expression passes when one choice is a conjunction of admitted identifiers.
export function admits(expression, name) {
  const choices = expression.split(' OR ').map((choice) => choice.split(' AND '));
  return (
    choices.flat().every((term) => IDENTIFIER.test(term)) &&
    choices.some((terms) => terms.every((term) => admitted(term, name)))
  );
}

// Each reported package with the licence its own package.json declares: pnpm guesses one from a LICENSE file when the
// field is missing, and the check holds a package to what it declares.
export function closure(report, readManifest) {
  return Object.values(report)
    .flat()
    .flatMap((entry) =>
      entry.paths.map((path) => {
        const manifest = readManifest(path);
        const licence = typeof manifest.license === 'string' ? manifest.license : undefined;
        return { name: manifest.name ?? entry.name, version: manifest.version, licence };
      }),
    );
}

export function violations(packages) {
  return packages.flatMap(({ name, version, licence }) => {
    if (licence === undefined) return [`${name} ${version} declares no licence.`];
    return admits(licence, name) ? [] : [`${name} ${version} is licensed ${licence}, which the check does not admit.`];
  });
}

// name@version of every package installed under node_modules/.pnpm: the package's own directory in each entry, never
// the symbolic links to its dependencies beside it.
export function installed(store) {
  const found = new Set();
  for (const entry of readdirSync(store)) {
    const modules = join(store, entry, 'node_modules');
    if (!existsSync(modules)) continue;
    for (const child of readdirSync(modules)) {
      const directories = child.startsWith('@')
        ? readdirSync(join(modules, child)).map((name) => join(child, name))
        : [child];
      for (const directory of directories) {
        if (lstatSync(join(modules, directory)).isSymbolicLink()) continue;
        const manifest = readJson(join(modules, directory, 'package.json'));
        found.add(`${manifest.name}@${manifest.version}`);
      }
    }
  }
  return found;
}

function readJson(path) {
  return JSON.parse(readFileSync(path, 'utf8'));
}

function pnpmReport(site) {
  return JSON.parse(
    execFileSync('pnpm', ['licenses', 'list', '--json'], { cwd: site, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 }),
  );
}

export function check({ site, report = pnpmReport(site) }) {
  const packages = closure(report, (path) => readJson(join(path, 'package.json')));
  const reported = new Set(packages.map(({ name, version }) => `${name}@${version}`));
  const unreported = [...installed(join(site, 'node_modules', '.pnpm'))]
    .filter((id) => !reported.has(id))
    .map((id) => `${id} is installed but missing from pnpm's licence report.`);
  return { packages, problems: [...violations(packages), ...unreported] };
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const { packages, problems } = check({ site: join(dirname(fileURLToPath(import.meta.url)), '..') });
  if (problems.length > 0) {
    for (const problem of problems) console.error(problem);
    console.error(`licences: ${problems.length} problem(s) in ${packages.length} packages`);
    process.exit(1);
  }
  const counts = Object.entries(Object.groupBy(packages, ({ licence }) => licence))
    .map(([licence, group]) => `${licence} ${group.length}`)
    .join(', ');
  console.log(`licences: ${packages.length} packages admitted (${counts})`);
}
