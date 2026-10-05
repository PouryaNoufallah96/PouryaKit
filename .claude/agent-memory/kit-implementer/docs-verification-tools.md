---
name: docs-verification-tools
description: How to prove skill snippets compile, Mermaid C4 parses and dotnet-ef commands work without touching the repo (docs packages, workflow steps 6 and 7)
metadata:
  type: project
---

Used in the #8 docs package (2026-10-05):

- **Skill snippets compile:** a throwaway project in the scratchpad (outside the repo, so no Directory.*.props) with `ManagePackageVersionsCentrally=false`, inline package versions (xunit.v3, Microsoft.Extensions.Hosting) and absolute `ProjectReference`s to the worktree's `src/` projects. Paste each snippet with minimal stubs; `dotnet build` must show 0 errors.
- **Mermaid C4 parses:** `npm i mermaid jsdom` in the scratchpad; set `window`, `document`, `DOMParser`, `Element` from a JSDOM on `globalThis`, then `await mermaid.parse(block)`. Include a broken diagram as a negative control. No `mmdc` on the machine.
- **dotnet-ef against the sample:** build the Host first (else NETSDK1004 assets error), then `dotnet ef dbcontext list --project samples/ModularMonolith/src/Modules/Catalog/Catalog --startup-project samples/ModularMonolith/src/ModularMonolith.Host --no-build`. It finds the internal `CatalogDbContext` through the Host, with no design-time factory. `migrations add` with an absolute `--output-dir` in the scratchpad proves the command without changing the repo.
- #10/#12 docs pass: a python extractor (`re.findall("```csharp\n(.*?)```")`) writes one file per block, keeps its `using`/`[assembly:]` header first, then a per-block `namespace` (blocks sharing domain types share one), wrapping statement-only blocks in a `file static class` method. Gotchas: the test block must stay in the global namespace (its `[assembly: AssemblyFixture(typeof(AppFixture))]` precedes any namespace); a stub namespace `ModularMonolith.Host` shadows `Host.CreateApplicationBuilder`, so fully qualify it; the Composition Root block shows `CreateBuilder` outside a class and needs wrapping. A negative control (rename one named argument) must fail the build.
- `HostApplicationBuilder` in the Development environment validates scopes and registrations on build; the sample's fixture passes `--environment Development` for that.

**Why:** a docs brief requires every snippet to compile and the diagram to stay valid, and none of the repo's checks cover either.
**How to apply:** at workflow steps 6 and 7 for any ticket. Related: [[persistence-ef-facts]], [[worktree-and-check-gotchas]].
