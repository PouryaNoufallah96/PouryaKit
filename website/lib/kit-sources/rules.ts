// The build-time rules that keep the private repository out of everything the site publishes. Each rule reads text the
// pipeline has already projected or the export has already rendered; the callers say which text is rendered and which is
// markup (export-check.ts, markdown.ts).

export type Violation = { rule: string; match: string };

// GitHub resolves an owner and a repository name without regard to case, so the comparison folds both.
const PRIVATE_OWNER = 'pouryanoufallah96';
const PRIVATE_REPOSITORY = 'pouryakit';

// Delivery vocabulary, tracker codes and the private plan's citations, matched in rendered text only, with every run of
// white space read as one space, so a soft line break hides nothing: a CSS colour (#615fff), an attribute or a script's
// `G.12` is markup. The three delivery words are the glossary's own terms, which the site leaves out: Wave and Phase
// capitalised as the glossary writes them, and spike in any case and anywhere, since the delivery named its folders,
// tags and types after it (`spikes/saga/`, `SagaSpike`). A delivery's story and session are refused by their number.
const VOCABULARY: [string, RegExp][] = [
  ['delivery vocabulary (Wave, Phase)', /\b(?:Wave|Phase)/g],
  ['delivery vocabulary (spike)', /spike/gi],
  ['delivery process (story <n>, session <n>)', /\b(?:stor(?:y|ies)|sessions?) \d+/gi],
  ['tracker code (W<n>)', /\bW\d+\b/g],
  ['tracker code (spec #)', /\bspec #/gi],
  ['tracker code (ticket #<n>)', /\b(?:tickets?|issues?|PRs?|pull requests?) #\d/gi],
  ['tracker code (#<n>)', /#\d{2,}/g],
  ['private plan citation (G.<n>, I.<n>)', /\b[GI]\.\d+/g],
  ['private plan citation (section G)', /\bsection G\b/gi],
  ['private plan citation (plan section)', /\bplan section [A-Z]\b/gi],
];

// The owner's private projects, by name, ignoring case and anywhere, markup included.
const PRIVATE_NAMES: [string, RegExp][] = [
  ['private project (Cybernod)', /\bcybernod\b/gi],
  ['private project (Ultrabusiness)', /\bultra[- ]?business\b/gi],
];

// A path on a developer's machine: a macOS or Linux home, the macOS temporary directory, or a Windows drive path.
const LOCAL_PATHS: [string, RegExp][] = [
  ['local path (/Users/)', /\/Users\//g],
  ['local path (/home/)', /\/home\//g],
  ['local path (/private/tmp/)', /\/private\/tmp\//g],
  ['local path (Windows drive)', /(?<![A-Za-z0-9_$.])[A-Za-z]:\\{1,2}[A-Za-z0-9_.-]/g],
];

// A Windows drive written with a forward slash (D:/repo), read everywhere but in JavaScript, where a one-letter key or
// a ternary before a regular expression literal (`{a:/re/}`, `e?t:/x/`) has the same shape.
const SLASH_DRIVE: [string, RegExp][] = [
  ['local path (Windows drive)', /(?<![A-Za-z0-9_$.])[A-Za-z]:\/[A-Za-z0-9_.-]/g],
];

// A host GitHub serves a repository's content from, followed by a path or an SSH colon. A scheme is optional, so a URL
// written as text (github.com/<owner>/<repo>) counts too.
const GITHUB_URL =
  /(?<![\w.-])(?:[a-z][a-z0-9+.-]*:\/\/)?(?:[\w.-]+@)?((?:[a-z0-9-]+\.)*(?:github\.com|github\.dev|githubusercontent\.com|github\.io|jsdelivr\.net))([/:][^\s"'`<>()[\]{}\\|^]*)?/gi;

// The owner/repo shorthand a CLI takes (npx skills add <owner>/<repo>), with no host, alone, after a `/` or followed by a
// path: every two adjacent segments of a path are read as a pair, so a host-less /<owner>/<repo>/... counts too.
const SHORTHAND = /(?<![\w.-])(?=([\w.-]+)\/([\w.-]+)(?![\w-]))/g;

// A path's segments as a browser requests them: `.` dropped and `..` removing the segment before it, an empty one too, and
// climbing no higher than the root. Only then is an empty segment dropped, so `a//../b` is `a/b`, not `b`.
function resolvedSegments(path: string) {
  const segments: string[] = [];
  for (const segment of path.split('/')) {
    if (segment === '..') segments.pop();
    else if (segment !== '.') segments.push(segment);
  }
  return segments.filter((segment) => segment !== '');
}

function repositoryOf(host: string, path: string): { owner: string; repository: string } | undefined {
  // What follows the host's colon is a port or, in SSH's form, the owner.
  const segments = resolvedSegments(path.replace(/^:(?:\d+(?=\/))?/, '/').split(/[?#]/)[0]);
  const lowerHost = host.toLowerCase();
  let owner: string | undefined;
  let repository: string | undefined;
  if (lowerHost === 'jsdelivr.net' || lowerHost.endsWith('.jsdelivr.net')) {
    // cdn.jsdelivr.net/gh/<owner>/<repo>@<version>/<file>, and its page www.jsdelivr.net/package/gh/<owner>/<repo>
    const at = segments[0]?.toLowerCase() === 'package' ? 1 : 0;
    if (segments[at]?.toLowerCase() !== 'gh') return undefined;
    [owner, repository] = [segments[at + 1], segments[at + 2]];
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

// What GitHub serves under a repository's name: <repo>, <repo>.git, <repo>.wiki and, on a CDN, <repo>@<version>. The
// name is its run of the characters a repository name may hold, without the dots that end it, so what follows it in a
// sentence (`<repo>.`, `<repo>,`, `<repo>**`) is not read as part of it.
function normalizeRepository(name: string) {
  return (/^[a-z0-9._-]*/.exec(name.toLowerCase())?.[0] ?? '')
    .replace(/\.+$/, '')
    .replace(/\.git$/, '')
    .replace(/\.wiki$/, '');
}

function isPrivate(owner: string, repository: string) {
  return owner.toLowerCase() === PRIVATE_OWNER && normalizeRepository(repository) === PRIVATE_REPOSITORY;
}

// An address as a browser sends it and GitHub reads it. The percent escapes of what a name or an address is made of are
// decoded over the whole text, so an escaped dot makes a dot segment and an escaped `/` or `:` (a URL inside another's
// query) separates as the character does; any other escape stays, as it stays inside a browser's path segment, so `%3F`
// ends no path where `?` would. A `\` is the `/` a browser reads it as. A line break after a `/` or a `:` is undone, since
// a hard-wrapped source may break an address there and a page renders it on one line. Each escape is one byte, enough for
// the ASCII names compared. With `keepEscapedSlash`, an escaped `/` stays in its segment, as a browser keeps it in a path,
// so a `..` after it removes that whole segment.
function asAddressed(text: string, keepEscapedSlash = false) {
  return text
    .replace(/%([0-9a-f]{2})/gi, (whole, hex: string) => {
      const character = String.fromCharCode(Number.parseInt(hex, 16));
      return /[\w.:/@-]/.test(character) && !(keepEscapedSlash && character === '/') ? character : whole;
    })
    .replaceAll('\\', '/')
    .replace(/(?<=[/:])[^\S\n]*\n[^\S\n]*/g, '');
}

/** Every reference to the private repository in `source`: a URL on any GitHub host, with or without a scheme, or a bare owner/repo. */
export function privateRepositoryReferences(source: string): Violation[] {
  // Both readings of an escaped `/` must pass: decoded, a URL inside another's query; kept, a browser's path segment.
  return [...new Set([asAddressed(source), asAddressed(source, true)])].flatMap(referencesIn);
}

function referencesIn(text: string): Violation[] {
  const found: Violation[] = [];
  // Where each URL found is, so an owner/repo pair inside it is not found a second time.
  const urls: [number, number][] = [];
  for (const match of text.matchAll(GITHUB_URL)) {
    const repository = repositoryOf(match[1], match[2] ?? '');
    if (repository && isPrivate(repository.owner, repository.repository)) {
      found.push({ rule: 'private repository', match: match[0] });
      urls.push([match.index, match.index + match[0].length]);
    }
  }
  for (const match of text.matchAll(SHORTHAND)) {
    if (urls.some(([start, end]) => match.index >= start && match.index < end)) continue;
    if (isPrivate(match[1], match[2])) found.push({ rule: 'private repository', match: `${match[1]}/${match[2]}` });
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

/**
 * The rules any text the site publishes must pass, markup included: no private repository, no local path and no private
 * project's name. In JavaScript (`javascript`), a drive written with a forward slash is not read (`SLASH_DRIVE`).
 */
export function addressViolations(text: string, javascript = false): Violation[] {
  const local = javascript ? LOCAL_PATHS : [...LOCAL_PATHS, ...SLASH_DRIVE];
  return [...privateRepositoryReferences(text), ...matchAll(text, local), ...matchAll(text, PRIVATE_NAMES)];
}

/**
 * Text with its soft white space read as one space: a line break inside a block, as Markdown and HTML render it, and any
 * other white space, a no-break space included. A blank line is a block's end, and stays one, so a phrase never runs from
 * one block into the next, such as a table's cells.
 */
function softSpaced(text: string): string {
  return text
    .replace(/[^\S\n]*\n(?:[^\S\n]*\n)+[^\S\n]*/g, '\n\n')
    .replace(/[^\S\n]*(?<!\n)\n(?!\n)[^\S\n]*/g, ' ')
    .replace(/[^\S\n]+/g, ' ');
}

/** The rules only rendered text must pass: no delivery vocabulary, no tracker code and no private plan citation. */
export function vocabularyViolations(text: string): Violation[] {
  return matchAll(softSpaced(text), VOCABULARY);
}

/** Every rule, for text that is rendered as it stands. */
export function renderedTextViolations(text: string): Violation[] {
  return [...addressViolations(text), ...vocabularyViolations(text)];
}

export function describe(violations: Violation[]) {
  return violations.map((violation) => `${violation.rule}: ${JSON.stringify(violation.match)}`).join('; ');
}
