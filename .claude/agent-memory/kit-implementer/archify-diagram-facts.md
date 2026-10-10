---
name: archify-diagram-facts
description: Archify 3.0.1 diagram packages (#106, 2026-10-11): Chrome for the browser gate, where sources may go, what renders, stale browser evidence, overflow and label-gap failures, PackageGraph edge checks
metadata:
  type: project
---

## Archify architecture diagrams (#106, 2026-10-11; Archify 3.0.1)

- **The browser gate needs Chrome:** without it `finalize` stops with `viewer/chrome-unavailable`. Prefix the command with `ARCHIFY_CHROME=` set to the Playwright build at `~/Library/Caches/ms-playwright/chromium-1243/chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing`. `cd <archify dir> && ARCHIFY_CHROME=... node bin/archify.mjs finalize ...` passed the guard; so did a scratchpad Python runner that calls it through `subprocess` with `cwd` and `env`.
- **Sources go on components only**, at most three each, with an optional `label` of at most 48 characters; connections and cards take none (`additionalProperties: false`). Back an edge with a range on the node that owns it, and give a package node its `PackageGraph.cs` row as one source, which proves all its outgoing references.
- **What renders:** a sublabel and a tag are one unwrapped line that shrinks to 6px at most; tags do not show in the default view. Put "planned" or "optional" in the label itself. Edge labels need `6.5px × characters + 21px` of clear gap, or validation fails with `composition/label-gap`, so a dense package grid takes unlabelled arrows and a card saying what an arrow means.
- **Overflow:** a card item with a long unbreakable token (a test method name with underscores, about 100 characters) overflowed 1440px (`viewer/viewport-overflow`, scrollWidth 1485); write the method name with spaces. A page taller than 900px fails the same gate; tighten rows. The receipt's `supportedFixes` names the height to reach.
- **Re-finalizing to the same HTML path** fails `viewer/evidence-path-conflict` until the old `<stem>.browser-check.json` (owned by the previous artifact) is removed. Finalize writes `.finalize.json`, `.browser-check.json`, `.delivery.json` and `.finalize-summary.json` beside the HTML; the summary holds absolute worktree paths, the HTML none.
- `meta.output` only has to be a valid relative path; the CLI output argument decides where the HTML goes. `json.dump(indent=2)` repositioning scripts reformat the candidate fully, which is harmless.
- **Grounding facts met:** `PouryaKit.Security.JwtBearer` uses no type of `PouryaKit.Security`; its tenant claim type is `PouryaKitClaimTypes` of `PouryaKit.Cqrs`, reached through that reference. A hub package with eight dependents plus a five-way `PouryaKit.Core` fan-in cannot be laid out without crossings; the receipt reports them as advisory.

**Why:** these cost repair rounds in #106; the sibling diagram packages hit the same gates.
**How to apply:** before authoring or finalizing an Archify diagram in this repository. Related: [[docs-verification-tools]], [[worktree-shell-and-check-gotchas]].
