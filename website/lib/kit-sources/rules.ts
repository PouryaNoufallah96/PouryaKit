// The build-time rules that keep the private repository out of everything the site publishes. Each rule reads text the
// pipeline has already projected or the export has already rendered; the callers say which text is rendered and which is
// markup (export-check.ts, project.ts).

export type Violation = { rule: string; match: string };

// GitHub resolves an owner and a repository name without regard to case, so the comparison folds both.
const PRIVATE_OWNER = 'pouryanoufallah96';
const PRIVATE_REPOSITORY = 'pouryakit';

// Delivery vocabulary and tracker codes, matched in rendered text only: a CSS colour (#615fff) or an attribute is markup.
const VOCABULARY: [string, RegExp][] = [
  ['delivery vocabulary (Wave)', /\bWave/g],
  ['tracker code (W<n>)', /\bW\d+\b/g],
  ['tracker code (spec #)', /\bspec #/gi],
  ['tracker code (#<n>)', /#\d{2,}/g],
];

// A path on a developer's machine: a macOS or Linux home, the macOS temporary directory, or a Windows drive path.
const LOCAL_PATHS: [string, RegExp][] = [
  ['local path (/Users/)', /\/Users\//g],
  ['local path (/home/)', /\/home\//g],
  ['local path (/private/tmp/)', /\/private\/tmp\//g],
  ['local path (Windows drive)', /(?<![A-Za-z0-9_$.])[A-Za-z]:\\{1,2}[A-Za-z0-9 _.-]+\\/g],
];

// A host GitHub serves a repository's content from, followed by a path or an SSH colon. A scheme is optional, so a URL
// written as text (github.com/<owner>/<repo>) counts too.
const GITHUB_URL =
  /(?<![\w.-])(?:[a-z][a-z0-9+.-]*:\/\/)?(?:[\w.-]+@)?((?:[a-z0-9-]+\.)*(?:github\.com|githubusercontent\.com|github\.io|jsdelivr\.net))([/:][^\s"'`<>()[\]{}\\|^]*)?/gi;

// The owner/repo shorthand a CLI takes (npx skills add <owner>/<repo>), with no host.
const SHORTHAND = /(?<![\w./-])([\w.-]+)\/([\w.-]+)(?![\w/-])/g;

function repositoryOf(host: string, path: string): { owner: string; repository: string } | undefined {
  const segments = path
    .replace(/^:/, '/')
    .split(/[?#]/)[0]
    .split('/')
    .filter((segment) => segment !== '')
    .map((segment) => {
      try {
        return decodeURIComponent(segment);
      } catch {
        return segment;
      }
    });
  const lowerHost = host.toLowerCase();
  let owner: string | undefined;
  let repository: string | undefined;
  if (lowerHost === 'cdn.jsdelivr.net' || lowerHost.endsWith('.jsdelivr.net')) {
    // cdn.jsdelivr.net/gh/<owner>/<repo>@<version>/<file>
    if (segments[0]?.toLowerCase() !== 'gh') return undefined;
    [owner, repository] = [segments[1], segments[2]];
  } else if (lowerHost === 'api.github.com') {
    // api.github.com/repos/<owner>/<repo>
    if (segments[0]?.toLowerCase() !== 'repos') return undefined;
    [owner, repository] = [segments[1], segments[2]];
  } else if (lowerHost.endsWith('.github.io')) {
    // <owner>.github.io/<repo>: GitHub Pages
    [owner, repository] = [lowerHost.slice(0, -'.github.io'.length), segments[0]];
  } else {
    // github.com, www.github.com, codeload.github.com, raw.githubusercontent.com and the like: /<owner>/<repo>/...
    [owner, repository] = [segments[0], segments[1]];
  }
  if (!owner || !repository) return undefined;
  return { owner: owner.toLowerCase(), repository: normalizeRepository(repository) };
}

// What GitHub serves under a repository's name: <repo>, <repo>.git, <repo>.wiki and, on a CDN, <repo>@<version>.
function normalizeRepository(name: string) {
  return name
    .toLowerCase()
    .replace(/@.*$/, '')
    .replace(/\.git$/, '')
    .replace(/\.wiki$/, '');
}

function isPrivate(owner: string, repository: string) {
  return owner.toLowerCase() === PRIVATE_OWNER && normalizeRepository(repository) === PRIVATE_REPOSITORY;
}

/** Every reference to the private repository in `text`: a URL on any GitHub host, with or without a scheme, or a bare owner/repo. */
export function privateRepositoryReferences(text: string): Violation[] {
  const found: Violation[] = [];
  for (const match of text.matchAll(GITHUB_URL)) {
    const repository = repositoryOf(match[1], match[2] ?? '');
    if (repository && isPrivate(repository.owner, repository.repository)) {
      found.push({ rule: 'private repository', match: match[0] });
    }
  }
  for (const match of text.matchAll(SHORTHAND)) {
    if (isPrivate(match[1], match[2])) found.push({ rule: 'private repository', match: match[0] });
  }
  return found;
}

function matchAll(text: string, rules: [string, RegExp][]): Violation[] {
  const found: Violation[] = [];
  for (const [rule, pattern] of rules) {
    for (const match of text.matchAll(pattern)) found.push({ rule, match: match[0] });
  }
  return found;
}

/** The rules any text the site publishes must pass, markup included: no private repository and no local path. */
export function addressViolations(text: string): Violation[] {
  return [...privateRepositoryReferences(text), ...matchAll(text, LOCAL_PATHS)];
}

/** The rule only rendered text must pass: no delivery vocabulary and no tracker code. */
export function vocabularyViolations(text: string): Violation[] {
  return matchAll(text, VOCABULARY);
}

/** Every rule, for text that is rendered as it stands. */
export function renderedTextViolations(text: string): Violation[] {
  return [...addressViolations(text), ...vocabularyViolations(text)];
}

export function describe(violations: Violation[]) {
  return violations.map((violation) => `${violation.rule}: ${JSON.stringify(violation.match)}`).join('; ');
}
