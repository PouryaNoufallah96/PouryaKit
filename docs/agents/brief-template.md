# Brief template

The two briefs a `kit-implementer` reads in a run of tickets: one common brief per batch, saved as `.claude/handoff/<session>/briefs/common.md`, and one brief per ticket beside it. Copy each block, fill every `<placeholder>`, and drop a section that does not apply. `docs/agents/running-a-spec.md`, "State and handoff", says what a brief carries and why.

## Common brief

````markdown
# Common brief for <batch> (spec #<spec>)

Repository: `<repository root>`. Integration branch: `<integration branch>`. Your ticket brief names the tip your worktree was created from, on a branch of its own: confirm the base as your role file says. The integration branch may move while you work; fast-forward or rebase onto it only as your role allows, and report the base you started from. (The tip lives in the ticket brief, never here: a common brief outlives several merges.)

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

Make one bounded check of NuGet metadata for each package you add or bump: the current stable version compatible with .NET 10, and a permissive licence (ADR 0002). Record it in your report. Every version goes in `Directory.Packages.props`. Versions the main session checked on <date>: <package and version, one per package>.

## A new package (where the ticket adds one)

Follow the last package added (`<package>`, #<ticket>): csproj metadata as its siblings, `PackageGraphTests` in `tests/PouryaKit.ArchitectureTests` and its project reference there, a row in `scripts/packaging/inventory.json`, a clean consumer `scripts/packaging/consumers/<package>.cs`, a row in the plan's "Allowed references" table and the I.3 row's status note, and the rest of the docs-sync set below.

## Docs-sync set (allowed by default, beside the ticket's `allowed_files`)

`.agents/skills/pourya-kit/SKILL.md` and `references/*.md`, `README.md` (the status list), `docs/architecture/README.md`, `docs/acceptance.md`, `docs/adr/*.md` (a dated amendment of an ADR the ticket touches, never a rewrite of its record), `docs/foundation-review.md` (only your Capability's section C row and I.3 row, with a dated "Built" note), `docs/*.md` (a top-level doc, such as `docs/packages.md` or `docs/private-packages.md`, where a line restates the behaviour you change), `src/PouryaKit.Templates/content/**` (the monolith's replacement of a changed sample Composition Root or Gateway endpoint, and the template csproj that matches a changed sample csproj, as `.claude/rules/kit-code.md` couples them), `scripts/packaging/consumers/<package>.cs`, `scripts/packaging/inventory.json` (a ticket that adds or removes a package), `Directory.Packages.props`, `PouryaKit.slnx`, `tests/PouryaKit.ArchitectureTests/**` (package graph and structure tests), `GLOSSARY.md` (a term the ticket introduces), and your agent memory directory. A `stop_if` never narrows this set.

## Tests

`.claude/rules/kit-code.md`, "Tests": one test per behaviour that matters, and red once for each guarantee the ticket states, listed under `shown_red`. Gateway and HTTP seams run through `AppFixture` or `HostFixture` against PostgreSQL in Testcontainers. One Docker host serves every implementer and validation of the session, and PostgreSQL's 100-connection default fails concurrent suites with 53300: use class filters while you develop, and run the full `ModularMonolith.Tests` run and `validate.py` as the command of `.agents/scripts/wait-docker-quiet.sh -- <command>`. It takes the machine's Docker lock as soon as no other waiter holds it, waits while holding it until no validation or test run holds Docker, then runs the command and exits with its status. A wait alone reserves nothing: two agents that see one suite end both start theirs. One class runs with `dotnet test --project <csproj> --filter-class <fully qualified class> --output Detailed`. A test fails when its collaborators return defaults or the promised effect never occurs.

## Verify (run exactly, after your edits; every command passes in the final run)

```bash
dotnet build PouryaKit.slnx
dotnet test --project tests/ModularMonolith.Tests/ModularMonolith.Tests.csproj --filter-class ModularMonolith.Tests.<TestClass> --output Detailed
dotnet test --project tests/ModularMonolith.Tests/ModularMonolith.Tests.csproj
dotnet test --project tests/PouryaKit.ArchitectureTests/PouryaKit.ArchitectureTests.csproj
python3 .agents/scripts/check-ai-setup.py
python3 .agents/scripts/check-docs.py
```

Each ticket's brief adds its own test projects. Every command in a brief is copied from a place that runs it, the CI workflow (`.github/workflows/ci.yml`) or an earlier brief whose report shows it passed, never retyped from memory. Whether `validate.py` runs in full or prints `SKIP` is the script's decision from its exempt paths: the main session asks it with `python3 scripts/packaging/validate.py --since <tip> --plan` on the ticket's expected paths when it matters for scheduling, and a brief never predicts it.

## Commit, then package validation

1. Commit everything on your worktree branch, in one or a few commits with conventional messages such as `feat(<area>): <change> (#<n>)`. Do not push. Do not write to the tracker.
   The hooks run on every commit. When one refuses a failure in a file outside `allowed_files`, because a parallel package has not merged yet, keep your work uncommitted and report it in `notes`: the session merges the packages, then commits.
2. On the committed tree, run `.agents/scripts/wait-docker-quiet.sh -- python3 scripts/packaging/validate.py --no-fetch --since <tip> --output <scratchpad>/validate-<n>`, where `<tip>` is the integration-branch commit this brief names. The script removes that directory after a PASS (it holds about 8 GB; `--keep` retains it for inspection), so nothing is left to clean up. Put its PASS line, with the commit it ran on, in your report. A `SKIP` line instead means nothing you changed can reach a package: quote it in your report, since CI runs the full validation. On a failure, fix inside `allowed_files` (two attempts), then report.

## Report

Your final message is your role's JSON report, as text. It is the ticket's evidence, which the orchestrator carries into the closing comment: each behaviour, its proof (test or check) and its red-once or check result. Its additions: `base` (the integration-branch commit your branch starts from, after any rebase), `branch` (your worktree branch), `head` (your last commit), `design` (at most 150 words: the choices the brief left open, and why), `packages` (each package added or bumped, with its version and licence), `docs_touched` (paths), `shown_red`, `validated` (the commit `validate.py` ran on, which `merge-package.py` checks against `head`: `validate.py --since <it> --plan` must answer SKIP, so a fix that reaches a package, a packed `src/<Id>/README.md` among them, reruns it), and `merge_notes` (the files a ticket merged in parallel likely touches too, with their right post-merge values: the README status list, `inventory.json`, the `PackageGraphTests` count, the plan's section C table). Keep the whole report under about 4 KB, with paths relative to the worktree and no text repeated between fields: the harness truncates a longer final message, and the main session then has to ask for the rest.
````

## Per-ticket brief

````markdown
# Brief: #<n> <title>

Read `common.md` beside this file first. Integration branch tip at your spawn, your base and the `--since` of your validation: `<tip>`.

## objective

Ticket #<n> (`gh issue view <n>`), all acceptance criteria. <Where it lands: the packages it extends or adds, with the plan rows and spec decisions that place it.> Reference baseline: <the reference project's code, with its repository, commit and path, or the plan's own rule where the references were rejected>.

Costly guarantees:

- **<Guarantee>.** <The behaviour, the trap that breaks it, and the test that proves it.>

Design constraints:

- `stop_if`: <package-specific conditions that end the work with a report, such as a new third-party package or a reference the plan forbids>. A file outside `allowed_files` and the docs-sync set is not one: ask the session and continue, as your role file says.
- A guarantee the final registrations can answer is an `ICompositionCheck` at build, never an options validation at start; a provider package that needs one references `Cqrs`. A structure rule over text admits an allow-list grammar and faults every other shape; it never rejects the shapes it recognises, and it reads a format with that format's own parser. A proof against a third-party image is run once with the image removed (`docker rmi`) so a fresh pull is proven, and the brief names the registry and a fallback image. A probe that starts a Host or an AppHost outside the test runner stops its whole process tree before the report. Before adding a type, grep the other Kit packages for a sibling of the same shape and share or extend it: a per-ticket reviewer sees one diff, so a duplicate of an existing sibling goes unseen. A fix instruction names the guarantee and the trap, never the mechanism: the implementer chooses the mechanism and the reviewer judges it.
- <Invariants and existing tests to keep green, the seams to extend, and the parallel tickets that touch the same files.>

## allowed_files

- <paths or globs>
- the docs-sync set in `common.md`

## verify

The common `verify` block with `<TestClass>` = `<test class>`, plus <the ticket's own test projects>.
````

## Merger brief

For a merge `merge-package.py` stops on (`docs/agents/running-a-spec.md`, "Merging with diagram conflicts").

````markdown
# Merge brief: ticket-<n> into <integration branch> (merger subagent)

Why it matters: #<n> passed its reviews; its merge into `<integration branch>` conflicts in <the files `git merge-tree --write-tree <tip> <head>` listed>. The orchestrator fast-forwards the branch to your result after checking it.

Worktree `<repository>/.claude/worktrees/merge-<n>`, branch `merge-<n>` at `<tip>` (= the integration tip). Use `git -C` and absolute paths; never touch the main checkout, the integration branch or the ticket's worktree. Do not push. No `dotnet test`, no containers (the orchestrator verifies the merged tree); `dotnet build` only when a citation check needs it.

1. `git merge --no-ff <head>`. Read the implementer's `merge_notes` in `<report path>` first. Resolve: prose files by keeping both facts in one sentence that says the merged truth; the implementer memory index as one line per memory file holding both sides' ticket numbers; each diagram source with both sides' nodes, edges, cards and citations, every cited range remapped to the merged file and verified against it; either page.
2. Commit with `--no-verify`, message `Merge ticket-<n>: <title> (#<n>)`, trailers passed explicitly: `--trailer "Co-Authored-By: <session's model line>" --trailer "Claude-Session: <session url>"`.
3. `python3 .agents/scripts/repin-diagram.py <worktree> <merge sha> <slug>.<type>` for each re-ranged diagram (and any `check-docs.py --staged` names once you stage; `--archify <dir>` or `ARCHIFY_HOME` names the Archify checkout). Commit the pins through the hooks, same trailers.
4. `python3 .agents/scripts/check-docs.py` and `python3 .agents/scripts/check-ai-setup.py` at zero. Measure your own change against the automerged tree: `git merge-tree --write-tree <tip> <head>` prints that tree id first, then `git diff --stat <tree id> HEAD` must list only the conflicted files, the diagram pairs and memory files.

Report as text, JSON: `merge_commit`, `pin_commit`, `head`, `conflicts_resolved` (file: how), `own_change`, `commands` (each with outcome), `deviations`, `blockers`. Under 4 KB.
````

