// The glossary's public projection: every entry but the delivery vocabulary, each under a heading of its own so it has
// an anchor.

export type GlossaryEntry = { term: string; body: string; line: number };
export type GlossarySection = { title: string; entries: GlossaryEntry[] };

/** The entries the site never publishes: how the Kit was delivered, not how it is used. Each must match one entry. */
const EXCLUDED_ENTRIES = [
  { section: 'Delivery of the Kit', term: 'Wave' },
  { section: 'Delivery of the Kit', term: 'Phase' },
  { section: 'Delivery of the Kit', term: 'Spike' },
] as const;

const ENTRY_HEAD = /^\*\*(.+)\*\*:$/;

/** Every entry of `GLOSSARY.md`'s "Language", by section. A block that is not an entry fails, so no entry is skipped. */
export function parseGlossary(text: string): GlossarySection[] {
  const lines = text.split('\n');
  const start = lines.indexOf('## Language');
  if (start === -1) throw new Error('kit-sources: GLOSSARY.md has no "## Language" section');
  const sections: GlossarySection[] = [];
  let block: { line: number; lines: string[] } | undefined;
  const flush = () => {
    if (!block) return;
    const head = ENTRY_HEAD.exec(block.lines[0]);
    const section = sections.at(-1);
    if (!head || !section || block.lines.length < 2) {
      throw new Error(`kit-sources: GLOSSARY.md:${block.line} is not an entry (**Term**: then its definition)`);
    }
    section.entries.push({ term: head[1], body: block.lines.slice(1).join('\n'), line: block.line });
    block = undefined;
  };
  for (let index = start + 1; index < lines.length; index++) {
    const line = lines[index];
    if (line.startsWith('## ')) break;
    if (line.startsWith('### ')) {
      flush();
      sections.push({ title: line.slice(4).trim(), entries: [] });
    } else if (line.trim() === '') {
      flush();
    } else if (block) {
      block.lines.push(line);
    } else {
      block = { line: index + 1, lines: [line] };
    }
  }
  flush();
  return sections;
}

/** The sections without the excluded entries; a selector that matches no entry, or more than one, fails. */
export function projectGlossary(sections: GlossarySection[]): GlossarySection[] {
  for (const selector of EXCLUDED_ENTRIES) {
    const matches = sections
      .filter((section) => section.title === selector.section)
      .flatMap((section) => section.entries.filter((entry) => entry.term === selector.term));
    if (matches.length !== 1) {
      throw new Error(
        `kit-sources: the glossary exclusion ${selector.section} / ${selector.term} matches ${matches.length} entries, not one; update EXCLUDED_ENTRIES in lib/kit-sources/glossary.ts with GLOSSARY.md`,
      );
    }
  }
  const excluded = (section: string, term: string) =>
    EXCLUDED_ENTRIES.some((selector) => selector.section === section && selector.term === term);
  return sections
    .map((section) => ({
      ...section,
      entries: section.entries.filter((entry) => !excluded(section.title, entry.term)),
    }))
    .filter((section) => section.entries.length > 0);
}

/**
 * The projected glossary as Markdown: a heading per section, and per term, whose slug is the term's anchor; the words
 * to avoid get a paragraph of their own.
 */
export function glossaryMarkdown(sections: GlossarySection[]): string {
  const body = (entry: GlossaryEntry) => entry.body.replace(/\n(?=_Avoid_:)/g, '\n\n');
  return sections
    .map(
      (section) =>
        `## ${section.title}\n\n${section.entries.map((entry) => `### ${entry.term}\n\n${body(entry)}\n`).join('\n')}`,
    )
    .join('\n');
}
