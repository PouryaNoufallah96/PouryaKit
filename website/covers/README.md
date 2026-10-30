# Covers

The cover image of the Kit and of each package, shown at the top of its README on nuget.org and GitHub, and the packages' shared icon. The artwork is textless; `../scripts/covers.ts` typesets every word.

| Path          | What it holds                                                                                                                                                                                                                   |
| ------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `art/`        | One artwork per family in `covers.json`, `kit.png` (the Kit's hero) and `icon.png` (the icon's 1024x1024 artwork). Each cover artwork is 1280x640 with its left 40 percent empty for the title, and every file is at most 1 MB. |
| `covers.json` | The Kit's eyebrow, title and tagline; each family's label; and each package's family and tagline, one entry per package of `scripts/packaging/inventory.json`.                                                                  |
| `fonts/`      | Geist Regular and SemiBold and Geist Mono Medium, the static TTF files of the Geist 1.7.2 release, unmodified, under the SIL Open Font License 1.1 (`OFL.txt`).                                                                 |
| `out/`        | Generated and gitignored: `kit.png` and `covers/<id>.png`, each 1280x640 and at most 1 MB.                                                                                                                                      |

## Regenerating the covers

`pnpm --dir website covers` writes `out/` and the repository's `src/icon.png`, and prints each file's sha256 and size. Two runs from the same artwork write byte-identical files. The script stops on a cover over 1 MB or of another size, and on an ID or tagline that does not fit its lines.

A new package needs an entry in `covers.json`, with its family and a tagline: one true line about it, taken from its row in the skill's Capability table or from its reference. `pnpm --dir website test` fails until `covers.json` and the inventory name the same packages, and on any string outside printable ASCII: the committed fonts hold that set, and `next/og` would fetch a glyph they lack from Google Fonts.

## Making an artwork

A new family needs an artwork, and an artwork is replaced only with the owner's approval of the new image. Generate it with Codex in an empty directory outside the repository, attaching `art/kit.png` as the style reference:

```sh
codex exec -i kit.png --enable image_generation -s workspace-write -C <dir> --skip-git-repo-check --ephemeral -o <dir>/last.txt '$imagegen <prompt> Generate one image only. Then copy the generated PNG into the current directory as <name>.png and reply with its path.'
```

The prompt asks for exactly 1280x640 pixels (2:1), or 1024x1024 for an icon, then gives the subject, then the shared composition and style:

- Composition: the subject sits in the right 60 percent of the frame; the left 40 percent is calm, empty, dark background, reserved for a title added later.
- Style: match the attached reference image: the same isometric camera angle, dark near-black background with a faint isometric grid floor, translucent frosted-glass blocks with soft rounded edges, thin glowing Cobalt Indigo (#615fff) connector lines with small bright nodes, soft indigo under-glow and restrained cinematic lighting. Cobalt Indigo is the only accent hue. Smooth gradients, with no grain, noise or dithering.
- Absolutely no text: no letters, words, numbers, labels, code, logos or glyph-like symbols. Every surface is blank.

| Artwork         | Subject                                                                                                                                                                                                |
| --------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `kit`           | One large Host container holding Module blocks, linked to an orbit of the families' motifs: database cylinders, a queue rack, a shield, an envelope, a document sheet, a storage crate, a node cluster |
| `core`          | A Host container with four Module blocks around a central block; one pipeline enters, passes the centre and branches to each block                                                                     |
| `domain`        | One frosted cube, an aggregate, holding nested cubes on a pedestal, with event pulse rings rippling outward                                                                                            |
| `persistence`   | A Host container linked to stacked database cylinders and layered table slabs, one pulse travelling toward them                                                                                        |
| `messaging`     | Two Host containers linked through a central queue rack, message tiles fanning out to several destinations                                                                                             |
| `web`           | A Host container with an archway gateway; request beams arrive from the right and split to the Module blocks                                                                                           |
| `security`      | A Host container guarded by an upright shield, a key token on a checkpoint plate before it                                                                                                             |
| `observability` | Three Host containers threaded by one trace line, span bars stacked above like a timeline, a pulse wave                                                                                                |
| `caching`       | A fast memory slab floating above a Host container's blocks, a long dim line to a distant database                                                                                                     |
| `storage`       | Open storage crates of blank file tiles linked to a small Host container, one tile mid-transfer                                                                                                        |
| `email`         | A Host container sending blank sealed envelopes along a path to a relay tower                                                                                                                          |
| `ai`            | A Host container linked to a floating neural node graph and a cloud of points like a vector space                                                                                                      |
| `reporting`     | Document sheets rising from a tray, showing only bar charts and grid lines of light                                                                                                                    |
| `templates`     | A Host container assembled from a blueprint, some blocks finished and some still wireframes, a finished copy on a test bench                                                                           |
| `platform`      | A central control plate linked to a database, a queue rack, a cache slab and a model chip, a gateway arch leading to two Host containers                                                               |
| `icon`          | One bold frosted cube holding four glowing blocks, centred on a plain dark background, legible at 32 pixels                                                                                            |

Open the image and check it for any glyph and for an empty left 40 percent. Then re-encode it losslessly to at most 1 MB, without changing a pixel or using a quantiser, and copy it into `art/<name>.png`.
