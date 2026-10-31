import { isDirectory, listDirectory, PACKAGES, readSource } from './paths.ts';

// The Error codes the Kit's packages declare, read from their `*Errors` and `*ErrorCodes` types. The reader knows two
// member shapes; a code literal in such a file that neither shape accounts for fails, so a new shape is read or refused,
// never left out.

export type ErrorCode = {
  code: string;
  /** The Error kind (`Validation`, `Forbidden`, `NotFound`, `Conflict`, `Unexpected`), or empty for a code constant. */
  kind: string;
  /** The message the Error carries, its interpolations kept as `{name}`; empty for a code constant. */
  message: string;
  /** The member's summary, in plain text. */
  summary: string;
  type: string;
  package: string;
};

const CODE_LITERAL = /"([A-Z][A-Za-z]*\.[A-Z][A-Za-z]*)"/g;

// public static readonly Error Name = Error.Kind("Code", "Message", ...) or a method => Error.Kind("Code", $"Message")
const ERROR_MEMBER =
  /((?:[ \t]*\/\/\/[^\n]*\n)*)[ \t]*(?:public|internal) static (?:readonly Error \w+ =|Error \w+\([^)]*\) =>)\s*Error\.(\w+)\(\s*"([A-Z][A-Za-z]*\.[A-Z][A-Za-z]*)",\s*\$?"((?:[^"\\]|\\.)*)"/g;

// public const string Name = "Code";
const CODE_CONSTANT = /((?:[ \t]*\/\/\/[^\n]*\n)*)[ \t]*public const string \w+ = "([A-Z][A-Za-z]*\.[A-Z][A-Za-z]*)";/g;

/** A `///` summary as plain text: tags dropped, a `cref` or a name kept as the name it gives. */
function summaryText(comment: string): string {
  const xml = comment
    .split('\n')
    .map((line) => line.replace(/^\s*\/\/\/ ?/, ''))
    .join(' ');
  const summary = /<summary>([\s\S]*?)<\/summary>/.exec(xml)?.[1] ?? '';
  return summary
    .replace(/<(?:see|seealso) cref="(?:[A-Z]:)?([^"]+)"\s*\/>/g, '$1')
    .replace(/<(?:paramref|typeparamref) name="([^"]+)"\s*\/>/g, '$1')
    .replace(/<c>([\s\S]*?)<\/c>/g, '$1')
    .replace(/<[^>]+>/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

function walk(root: string, directory: string): string[] {
  return listDirectory(root, directory).flatMap((name) => {
    const relative = `${directory}/${name}`;
    if (name === 'bin' || name === 'obj') return [];
    if (isDirectory(root, relative)) return walk(root, relative);
    return name.endsWith('.cs') ? [relative] : [];
  });
}

function codesIn(file: string, text: string, id: string, type: string): ErrorCode[] {
  const found: ErrorCode[] = [];
  for (const match of text.matchAll(ERROR_MEMBER)) {
    found.push({
      code: match[3],
      kind: match[2],
      message: match[4].replace(/\\"/g, '"'),
      summary: summaryText(match[1]),
      type,
      package: id,
    });
  }
  for (const match of text.matchAll(CODE_CONSTANT)) {
    found.push({ code: match[2], kind: '', message: '', summary: summaryText(match[1]), type, package: id });
  }
  const read = new Set(found.map((entry) => entry.code));
  const unread = [...text.matchAll(CODE_LITERAL)].map((match) => match[1]).filter((code) => !read.has(code));
  if (unread.length > 0) {
    throw new Error(
      `kit-sources: ${file} declares ${unread.join(', ')} in a shape lib/kit-sources/errors.ts does not read`,
    );
  }
  return found;
}

/** Every Error code of the Kit's packages, in package and code order. */
export function readErrorCodes(root: string, packages: string[]): ErrorCode[] {
  const codes: ErrorCode[] = [];
  for (const id of packages) {
    for (const file of walk(root, `${PACKAGES}/${id}`)) {
      const text = readSource(root, file);
      const type = /(?:public|internal) static class (\w+(?:Errors|ErrorCodes))\b/.exec(text)?.[1];
      // A Kit package's code is in its one namespace (ADR 0010); a template's content is application code.
      if (!type || !text.includes(`namespace ${id};`)) continue;
      codes.push(...codesIn(file, text, id, type));
    }
  }
  return codes.sort((a, b) => a.package.localeCompare(b.package) || a.code.localeCompare(b.code));
}
