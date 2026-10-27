import { INVENTORY, PACKAGES, readSource, sourceExists } from './paths.ts';
import type { SkillEntry } from './references.ts';

export type KitPackage = {
  id: string;
  /** The page slug: the ID in lower case, dots as hyphens. */
  slug: string;
  /** The Kit packages its project references. */
  references: string[];
  /** The Capabilities whose row in the skill's table names it. */
  capabilities: SkillEntry[];
  /** `src/<Id>/README.md` when the package has one. */
  readme?: string;
};

export function packageSlug(id: string) {
  return id.toLowerCase().replaceAll('.', '-');
}

/** The Kit's packages, as the packaging inventory lists them, with each one's project references and Capabilities. */
export function readPackages(root: string, entries: SkillEntry[]): KitPackage[] {
  const inventory = JSON.parse(readSource(root, INVENTORY)) as { packages: { id: string }[] };
  const ids = inventory.packages.map((entry) => entry.id);
  const known = new Set(ids);
  for (const entry of entries) {
    for (const id of entry.packages) {
      if (!known.has(id))
        throw new Error(`kit-sources: the skill's ${entry.name} row names ${id}, which is no Kit package`);
    }
  }
  return ids.map((id) => {
    const project = `${PACKAGES}/${id}/${id}.csproj`;
    if (!sourceExists(root, project)) throw new Error(`kit-sources: ${project} does not exist`);
    const references = [...readSource(root, project).matchAll(/<ProjectReference Include="[^"]*?([\w.]+)\.csproj"/g)]
      .map((match) => match[1])
      .filter((reference) => known.has(reference));
    const readme = `${PACKAGES}/${id}/README.md`;
    return {
      id,
      slug: packageSlug(id),
      references,
      capabilities: entries.filter((entry) => entry.packages.includes(id)),
      readme: sourceExists(root, readme) ? readme : undefined,
    };
  });
}
