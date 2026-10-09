# Brief template

The two briefs a `kit-implementer` reads in a run of tickets: one common brief per batch, saved as `.claude/handoff/<session>/briefs/common.md`, and one brief per ticket beside it. Copy each block, fill every `<placeholder>`, and drop a section that does not apply. `docs/agents/running-a-spec.md`, "State and handoff", says what a brief carries and why.

## Common brief

````markdown
# Common brief for <batch> (spec #<spec>)

Repository: `<repository root>`. Integration branch: `<integration branch>`, tip `<tip>`. Your worktree was created from that commit on a branch of its own: confirm the base as your role file says. The integration branch may move while you work; fast-forward or rebase onto it only as your role allows, and report the base you started from.

## Why this work matters

<What the batch completes, what consumes it next, and the owner's instruction for this run.>

## Read first (in this order)

1. Your ticket: `gh issue view <n>`. Its "What to build" and acceptance criteria are the objective.
2. The parent spec: `.claude/handoff/<session>/issues/<spec>.md` ("Implementation Decisions", "Testing Decisions", "Out of Scope").
3. `AGENTS.md`, `GLOSSARY.md`, `.claude/rules/kit-code.md` and `docs/agents/running-a-spec.md`.
4. `docs/foundation-review.md`: section G, the "Allowed references" table in section C, the I.3 row your ticket names, and the I.4 and I.6 notes that apply.
5. The ADRs your ticket touches (`docs/adr/`), with every later amendment in each file.
6. Your agent memory: `.claude/agent-memory/kit-implementer/MEMORY.md` and the facts files of your area.
7. The Kit skill reference of the Capability you extend (`.agents/skills/pourya-kit/references/*.md`), so the docs you update match the code.

## Dependencies

Make one bounded check of NuGet metadata for each package you add or bump: the current stable version compatible with .NET 10, and a permissive licence (ADR 0002). Record it in the provenance doc and your report. Every version goes in `Directory.Packages.props`. Versions the main session checked on <date>: <package and version, one per package>.

## A new package (where the ticket adds one)

Follow the last package added (`<package>`, #<ticket>): csproj metadata as its siblings, `PackageGraphTests` in `tests/PouryaKit.ArchitectureTests` and its project reference there, a row in `scripts/packaging/inventory.json`, a clean consumer `scripts/packaging/consumers/<package>.cs`, `docs/provenance/<package>.md`, a row in the plan's "Allowed references" table and the I.3 row's status note, and the rest of the docs-sync set below.

## Docs-sync set (allowed by default, beside the ticket's `allowed_files`)

`.agents/skills/pourya-kit/SKILL.md` and `references/*.md`, `README.md` (the status list), `docs/architecture/README.md`, `docs/acceptance.md`, `docs/provenance/*.md`, `docs/adr/*.md` (a dated amendment of an ADR the ticket touches, never a rewrite of its record), `docs/foundation-review.md` (only your Capability's section C row and I.3 row, with a dated "Built" note), `docs/*.md` (a top-level doc, such as `docs/packages.md` or `docs/private-packages.md`, where a line restates the behaviour you change), `src/PouryaKit.Templates/content/**` (the monolith's replacement of a changed sample Composition Root or Gateway endpoint, and the template csproj that matches a changed sample csproj, as `.claude/rules/kit-code.md` couples them), `scripts/packaging/consumers/<package>.cs`, `scripts/packaging/inventory.json` (a ticket that adds or removes a package), `Directory.Packages.props`, `PouryaKit.slnx`, `tests/PouryaKit.ArchitectureTests/**` (package graph and structure tests), `GLOSSARY.md` (a term the ticket introduces), and your agent memory directory. A `stop_if` never narrows this set.

## Tests

`.claude/rules/kit-code.md`, "Tests": one test per behaviour that matters, and red once for each guarantee the ticket states, listed under `shown_red`. Gateway and HTTP seams run through `AppFixture` or `HostFixture` against PostgreSQL in Testcontainers. One class runs with `dotnet test --project <csproj> --filter-class <fully qualified class> --output Detailed`. A test fails when its collaborators return defaults or the promised effect never occurs.

## Verify (run exactly, after your edits; every command passes in the final run)

```bash
dotnet build PouryaKit.slnx
dotnet test --project tests/ModularMonolith.Tests/ModularMonolith.Tests.csproj --filter-class ModularMonolith.Tests.<TestClass> --output Detailed
dotnet test --project tests/ModularMonolith.Tests/ModularMonolith.Tests.csproj
dotnet test --project tests/PouryaKit.ArchitectureTests/PouryaKit.ArchitectureTests.csproj
python3 .agents/scripts/check-ai-setup.py
python3 .agents/scripts/check-docs.py
```

Each ticket's brief adds its own test projects.

## Commit, then package validation

1. Commit everything on your worktree branch, in one or a few commits with conventional messages such as `feat(<area>): <change> (#<n>)`. Do not push. Do not write to the tracker.
2. On the committed tree, run `python3 scripts/packaging/validate.py --no-fetch --since <tip> --output <scratchpad>/validate-<n>`, where `<tip>` is the integration-branch commit this brief names. Put its PASS line, with the commit it ran on, into `docs/acceptance.md` as earlier tickets did, and commit that. A `SKIP` line instead means nothing you changed can reach a package: quote it in your report and record nothing, since CI runs the full validation. On a failure, fix inside `allowed_files` (two attempts), then report.

## Report

Your final message is your role's JSON report, as text, with these additions: `base` (the integration-branch commit your branch starts from, after any rebase), `branch` (your worktree branch), `head` (your last commit), `design` (at most 150 words: the choices the brief left open, and why), `packages` (each package added or bumped, with its version and licence), `docs_touched` (paths), `shown_red`, `validated` (the commit `validate.py` ran on, which `merge-package.py` checks against `head`: only Markdown may differ, so a fix after the validation reruns it), and `merge_notes` (the files a ticket merged in parallel likely touches too, with their right post-merge values: the README status list, acceptance counts, `inventory.json`, the `PackageGraphTests` count, the plan's section C table).
````

## Per-ticket brief

````markdown
# Brief: #<n> <title>

Read `common.md` beside this file first.

## objective

Ticket #<n> (`gh issue view <n>`), all acceptance criteria. <Where it lands: the packages it extends or adds, with the plan rows and spec decisions that place it.> Reference baseline: <the reference project's code, with its repository, commit and path, or the plan's own rule where the references were rejected>.

Costly guarantees:

- **<Guarantee>.** <The behaviour, the trap that breaks it, and the test that proves it.>

Design constraints:

- `stop_if`: <package-specific conditions that end the work with a report, such as a new third-party package or a reference the plan forbids>. A file outside `allowed_files` and the docs-sync set is not one: ask the session and continue, as your role file says.
- <Invariants and existing tests to keep green, the seams to extend, and the parallel tickets that touch the same files.>

## allowed_files

- <paths or globs>
- the docs-sync set in `common.md`

## verify

The common `verify` block with `<TestClass>` = `<test class>`, plus <the ticket's own test projects>.
````
