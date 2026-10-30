# PouryaKit

A reusable .NET 10 foundation for modular monoliths built from vertical slices. What is built, and where each part is documented: `README.md`.

## Read first

- `docs/foundation-review.md` — the plan and its evidence. Section G overrides earlier sections where they differ.
- `GLOSSARY.md` — use these terms exactly; do not use the listed synonyms.
- `docs/adr/` — decisions that are expensive to reverse. If your work contradicts one, say so explicitly instead of overriding it.

## Rules

- The owner's decisions in section G are final. If implementation produces concrete evidence against one, bring the evidence to the owner; do not change the decision silently.
- The Kit is for new projects. Never change, migrate or refactor the reference projects; they are sources of ideas only.
- One implementation per concern, one naming scheme. Adopting code from a reference project is fine; ending up with two ways to do the same thing is not.
- Never write a local machine path in docs.
- The Kit takes no runtime dependency on a reciprocal or commercially licensed package (ADR 0002).
- Feature code never saves, commits, or touches the Outbox; the Unit of Work owns that (ADR 0003).

## Operating model

The main session is judge and orchestrator. It scopes the ticket, writes the brief, delegates, judges the returned report against the gates, and decides fix or done.

Match the process to the task:

- **Clear and small** (a typo, a config value, a doc line, a few-line fix with an obvious check): do it directly. If the session requires delegation, one bounded worker owns the authorized edit, checks, commit, push and required CI observation through completion, without another handoff; the main session judges the result. A separate reviewer is needed only when risk or ambiguity requires it.
- **A normal ticket**: one `kit-implementer`, then one review sized as in step 3.
- **Unclear or stuck**: stop. Ask the owner, or send a `kit-scout` for the facts, and start only when the objective and its check are clear. Two failed attempts at the same thing is stuck.
- Tickets with disjoint files run in parallel.

When delegating: a `kit-implementer` brief carries `objective`, `allowed_files` and `verify`, and why the work matters. `stop_if` adds optional package-specific conditions; without it, apply the role's existing hard stops and record that assumption in the report. The objective states the deliverable, costly guarantees and reference baseline. Finish the selected acceptance criteria; an additional release framework or guarantee needs an owner scope decision. A role's report is a claim: read the diff and the `verify` output it reports before the next step, and re-run a check only when that leaves a doubt.

Role model and effort are pinned per role in `docs/agents/ai-setup.md`, "Roles". An explicit Claude spawn model overrides that role's frontmatter. A Codex role file's model and effort take precedence over a spawn request.

| Role | Use it for |
|---|---|
| `kit-scout` | Facts first: read-only evidence for one question, with file and line. |
| `kit-implementer` | One work package: implementation, focused tests, fixes of accepted findings. |
| `kit-reviewer` | Independent review of one diff against its ticket or spec, and the re-review after fixes. |

## Workflow for every ticket

Not test-first. Implement, then write focused tests for behaviour that matters; no redundant coverage.

1. Implement from the ticket and brief.
2. Focused tests.
3. Review, sized to the ticket. A ticket that claims a Kit runtime guarantee or changes a public API of the Kit gets two independent reviews in parallel. Other tickets follow the Operating model: clear, small changes need no separate reviewer unless risk or ambiguity requires one; normal tickets get one focused `kit-reviewer`. Executing a workflow does not itself make a Kit runtime guarantee.
4. One fix round for the accepted findings, then a re-score of those findings only. Run a new full review only after a high finding was fixed, and at most once: a ticket gets two full reviews, then re-scores only. For any medium or low finding still open, record a disposition and move on. A low that a re-score raises gets no round of its own: the orchestrator fixes it directly when it is trivial and local, one line or one test checked at once, and records a disposition otherwise.
5. Complete the runtime proof required by the ticket, including Kit guarantees and hosted publication where claimed.
6. Update docs and the architecture diagrams (`docs/architecture/README.md`, "Diagrams"). The ticket's closing comment carries a value, cost and impact paragraph (the `archify-review` lens); `/archify-review` itself runs once per wave, at close-out.
7. Update the Kit's own AI skill, rules and agent docs to match the change.

For infrastructure, CI and packaging, start with the installed standard Actions, `dotnet`/NuGet clients and MinVer. Implement custom protocol handling only after demonstrating a requirement those tools cannot cover.

When an external service is on the critical path, prepare and run the smallest real probe before full implementation and review. For publication, prove push, download and private-feed restore with the standard client. If the probe needs remote authority, prepare its exact commands, target and expected writes, then obtain the required owner approval before running it. Carry that evidence into final proof; local mocks and lint do not prove the service boundary.

Gates: relevant tests green · no open high finding, and a recorded disposition for the rest · runtime proof where applicable · docs in sync · AI guidance in sync.

Dual review invocations, including session provider/model overrides, are in `docs/agents/ai-setup.md`.

## Code Review Rules

Both reviewers apply these to the diff. Report defects the diff introduced; older issues and matters of taste stay out. Flag:

- A violation of a rule under "Rules", or a name that drifts from its `GLOSSARY.md` term. A contradiction of an ADR or a section G decision is severity high.
- A test that would still pass if every collaborator returned its default or under a weaker predicate than the rule states (a prefix check where the rule says equality, a scope narrower than the ticket names), or coverage that restates another test.
- A claimed runtime guarantee without runtime proof.
- A registration that scans types for a check on every command or query (an authorizer, a requirement) must cover the set the dispatcher runs, `OperationTypes.Dispatchable`: an operation declared in another assembly and handled in the scanned one, or an assembly only `AddPouryaKitCqrs` scanned, is the case to open. PR #143's Codex review found it twice after six single-ticket reviews had passed it (#116, then `AddPouryaKitPermissions`).
- Docs or AI guidance left out of sync with the change. For each sentence or message the diff changes, search its old key phrase across `src/` (each package's `src/*/README.md` among it), the Kit's skill, `docs/architecture/`, `docs/acceptance.md` and the docs site's `website/content/`: a restatement left as it was is a finding.
- A changed XML summary, code comment, skill sentence or package README sentence that states a behaviour the cited code does not have. Open the code it describes; a sentence that reads right but says the wrong thing is the finding the PR bots found 25 times in one round of PR #103 after six single reviews had passed it, and 26 times in the READMEs of PR #195.
- A guarantee the final registrations can answer (one provider, a declared authorizer, a tenant requirement) enforced at start (an options validation, `IStartupCheck`) instead of at build (`ICompositionCheck`): a built Host runs Operations before it starts (#61 F1, #147 F1).
- A structure rule over text (SQL, a route, a header) that rejects the shapes it recognises instead of admitting an allow-list grammar and failing every other shape: #118 lost two review rounds to new shapes, #154 and #155 inherited the lesson. The rule resolves names as the target does: PostgreSQL folds an unquoted identifier to lower case and keeps a quoted one as written, and the Dapper tenant rule folded both, so `p` and `"P"` were one alias to it and two tables to the database; six reviews passed it and the whole-branch audit of PR #143 found it. A rule over a format reads it with that format's own parser: a regex over HTML lost four holes after every ticket review of PR #195 had passed it.

## Rules and what enforces them

Correct a repeated mistake at the highest level that works; add the rule here only with its enforcement. A rule with nothing in its second column is a judgement call the reviewer carries.

| Rule | Enforced by |
|---|---|
| Section G decisions are final; one implementation per concern; glossary terms | `kit-reviewer` (a contradiction of an ADR or section G is high) |
| Reference projects are read-only | `.agents/hooks/guard.py` denies file-tool writes outside the working tree; a shell write outside it is a reviewer judgement |
| Permissive licences only (ADR 0002) | `LicenceTests`; for the docs site's npm closure, `website/scripts/check-licences.mjs` (`pnpm --dir website check-licences`, in CI's `website` job, with two named build-time exceptions) |
| Feature code never saves, commits or touches the Outbox (ADR 0003) | `FeatureCodeTests`, `ModuleRules` |
| One namespace per Kit package; application namespaces follow folders (ADR 0010) | `NamespaceTests`; IDE0130 in `.editorconfig`; `SampleNamespaceTests` for the Proving Sample, partial types included |
| Private fields are `_camelCase`; `const` and `static readonly` fields PascalCase | IDE1006 in `.editorconfig` |
| Every public member of a Kit package has a summary | CS1591 in the build where `IsKitPackage` is true (`Directory.Build.props`) |
| Docs name real code, paths and tests | `check-docs.py` (names, also in `website/content/**/*.mdx` and `src/*/README.md`; links, Kit paths, acceptance pointers) |
| Nothing private on the docs site: no private repository link, local path, private project name, delivery vocabulary, tracker code or private-plan citation | `pnpm --dir website build`'s `check-export` over `out/` (`website/lib/kit-sources/`), after the projection of the Kit's sources; in CI's `website` job |
| A diagram's cited source ranges are current | `check-docs.py` (diagram sources); the pre-commit hook runs it with `--staged`, reading diagrams and cited files from the index: a change at or above a cited range requires its diagram staged too, with ranges that fit the indexed file, while a change below every cited range passes; during a merge, a revision reachable from `MERGE_HEAD` counts; CI's `ai-setup` job runs it on the committed tree |
| A merged head's code was validated | `merge-package.py` exit 14: nothing a package is built from may differ from the validated commit, by `needing_validation` in `validate.py`, the one list, which `--plan` reads too; a packed README counts, the docs site does not |
| The main session's directory stays the checkout | `CLAUDE_BASH_MAINTAIN_PROJECT_WORKING_DIR` in `.claude/settings.json`: a `cd` lasts for its one Bash call |
| mise installs no .NET SDK on its own in an agent's shell; an explicit `mise install` stays the owner's | `MISE_AUTO_INSTALL_DISABLE_TOOLS` in `.claude/settings.json` and `.codex/config.toml` (`docs/agents/ai-setup.md`, "An agent's shell and the SDK") |
| A pushed tip is what the remote holds | `merge-package.py` reads the remote tip back after `--push` and exits 15 with `remote=REPLACED(<sha7>)` when it differs (2026-10-25: pushed tips force-replaced within seconds by a stolen credential); `check-tree.py` refuses a `.vscode` or `public` path segment at any depth, the replacement's payload paths (`test_check_tree.py`) |
| A negative test discriminates the rule's exact predicate | `kit-reviewer` (the review rule above) |
| A registration-answerable guarantee is a build-time composition check; a structure rule over text admits a grammar and reads a format with its parser | `kit-reviewer` (the two review rules above); `docs/agents/brief-template.md` carries both as design constraints |
| A parallel merge leaves one line per memory file in the implementer index | `check-docs.py` (union duplicates), run by `merge-package.py --post-merge` and CI |
| A check registered over scanned types covers every operation the dispatcher runs | `OperationTypes.Dispatchable` is the one discovery; `DeclaredAuthorizersCheck` (an attributed operation with no authorizer) and `TenantRequirementCheck` (a tenant-scoped scope with an unguarded operation) fail the Host when it is built; `kit-reviewer` for a new registration of that shape |
| A brief's commands come from the environment, never from memory | `docs/agents/brief-template.md`, "Verify" |
| A review reads the implementer's commit, never the integration branch against itself | `kit-review.js` refuses `head` equal to `base` |
| Pre-Wave 3, Wave 4 and Docs site acceptance sections stay in ticket order after every merge | `check-docs.py` (ordered sections), run by `merge-package.py --post-merge` |
| A package validation leaves no evidence directory behind after a PASS | `validate.py` removes it unless `--keep` (CI passes `--keep` for its artifact); `test_validation.py`, `FinishTests` |
| A local CI reproduction keeps what it proved: each step's output and the Release test totals | `validate.py` prints the totals in its PASS line (`test_totals`, `TestTotalsTests`); `.agents/scripts/ci-local.sh` keeps every step's output and the validation log |
| The Kit skill's description stays within the 1024 characters Claude Code loads | `check-ai-setup.py` (`SKILL_DESCRIPTION_LIMIT`); the pre-commit hook and CI's `ai-setup` job run it |
| A docs or AI-setup edit passes its checks before it commits | `.githooks/pre-commit`: `check-docs.py --staged` on every commit, `check-ai-setup.py` when an AI-setup path is staged, a gated path staged in part refused, a `.md` or `.mdx` file among them (`test_pre_commit.py`); CI's `ai-setup` job is the gate (`docs/agents/ai-setup.md`, "Git hooks") |
| A changed sentence's restatements change with it | `kit-implementer` searches each changed sentence's old key phrase before it reports (its Contract); `kit-reviewer` (the doc-sync review rule) |
| One Docker user at a time: a validation or a full suite never runs beside another | `.agents/scripts/wait-docker-quiet.sh -- <command>`, which takes a machine-wide lock, then runs the full suite or `validate.py` under it once Docker is quiet (`docs/agents/brief-template.md`, "Tests") |
| A brief never predicts whether the validation runs or skips | `validate.py --since <tip> --plan` answers it (`docs/agents/brief-template.md`, "Verify") |
| A merge's acceptance conflict and diagram re-pins use the shared tools, and the merger's own change is measured against the automerged tree | `.agents/scripts/resolve-acceptance.py`, `.agents/scripts/repin-diagram.py`; the merger block of `docs/agents/brief-template.md` |

## Rule router

Claude Code loads these rules by path on its own. Codex does not: read the rule before touching a matching file.

| Rule | Applies to |
|---|---|
| `.claude/rules/ai-setup.md` | `.claude/**`, `.codex/**`, `.agents/**`, `AGENTS.md`, `CLAUDE.md`, `skills-lock.json` |
| `.claude/rules/kit-code.md` | `src/**`, `tests/**`, `samples/**` |
| `.claude/rules/website.md` | `website/**` |

## Agent skills

### Issue tracker

Issues and specs live as GitHub issues on `PouryaNoufallah96/PouryaKit`. See `docs/agents/issue-tracker.md`.

For `/to-tickets`, follow that guide's "Spec tickets" rules. They override ambiguous vendored wording about parent modification and distinguish parent links from blocking dependencies.

### Triage labels

The five default triage labels, each named after its role. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` at the root, decisions in `docs/adr/`. See `docs/agents/domain.md`.

### The Kit's own skill

`.agents/skills/pourya-kit/`: step 7 updates the delivered Capability's file under `references/` and its row in `SKILL.md`. Every edit under that folder ends with `pnpm --dir website covers`, then `pnpm --dir website sync-public --write-manifest`, so the public repository's manifest follows (CI's `website` job runs `sync-public --check`).
