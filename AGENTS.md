# PouryaKit

A reusable .NET 10 foundation for modular monoliths built from vertical slices. Status: Wave 1 in progress; `PouryaKit.Domain` (domain primitives), `PouryaKit.Cqrs` and `PouryaKit.Validation` (dispatcher, pipeline and validation), `PouryaKit.Modularity`, `PouryaKit.Persistence`, `PouryaKit.Persistence.EFCore` and `PouryaKit.Persistence.Postgres` (Modules, Gateways with the Operation Context, the generic repository, named specifications and aggregate-specific repositories, and the Unit of Work on PostgreSQL with its Domain Event Handlers), `PouryaKit.Persistence.Dapper` (Dapper reads) and `PouryaKit.Testing` (the hosted application against PostgreSQL) are usable, and no other Capability is yet.

## Read first

- `docs/foundation-review.md` — the plan and its evidence. Section G overrides earlier sections where they differ.
- `GLOSSARY.md` — use these terms exactly; do not use the listed synonyms.
- `docs/adr/` — decisions that are expensive to reverse. If your work contradicts one, say so explicitly instead of overriding it.

## Rules

- The owner's decisions in section G are final. If implementation produces concrete evidence against one, bring the evidence to the owner; do not change the decision silently.
- The Kit is for new projects. Never change, migrate or refactor the reference projects (Cybernod, Ultrabusiness and the others); they are sources of ideas only.
- One implementation per concern, one naming scheme. Adopting code from a reference project is fine; ending up with two ways to do the same thing is not.
- Cite upstream repository, commit and repo-relative path in docs and provenance records, never a local machine path.
- The Kit takes no runtime dependency on a reciprocal or commercially licensed package (ADR 0002).
- Feature code never saves, commits, or touches the Outbox; the Unit of Work owns that (ADR 0003).

## Operating model

The main session is judge and orchestrator. It scopes the ticket, writes the brief, delegates, judges the returned report against the gates, and decides fix or done.

Match the process to the task:

- **Clear and small** (a typo, a config value, a doc line, a few-line fix with an obvious check): do it directly. No brief, no review.
- **A normal ticket**: one `kit-implementer`, then one review sized as in step 3.
- **Unclear or stuck**: stop. Ask the owner, or send a `kit-scout` for the facts, and start only when the objective and its check are clear. Two failed attempts at the same thing is stuck.
- Tickets with disjoint files run in parallel.

When delegating: a `kit-implementer` brief carries `objective`, `allowed_files`, `verify` and `stop_if`, and a role's report is a claim: read the diff and the `verify` output it reports before the next step, and re-run a check only when that leaves a doubt.

| Role | Use it for |
|---|---|
| `kit-scout` | Facts first: read-only evidence for one question, with file and line. |
| `kit-implementer` | One work package: implementation, focused tests, fixes of accepted findings. |
| `kit-reviewer` | Independent review of one diff against its ticket or spec, and the re-review after fixes. |

## Workflow for every ticket

Not test-first. Implement, then write focused tests for behaviour that matters; no redundant coverage.

1. Implement (Opus).
2. Focused tests.
3. Review, sized to the ticket. A ticket that claims a runtime guarantee or changes a public API of the Kit gets two independent reviews in parallel: a Fable subagent and Codex (GPT-6 Astra, high). Any other ticket gets one `kit-reviewer`.
4. One fix round for the accepted findings, then a re-score of those findings only. Run a new full review only after a high finding was fixed. For any medium or low finding still open, record a disposition and move on.
5. Runtime proof where the ticket claims a runtime guarantee.
6. Update docs and C4 diagrams; run `/archify-review`.
7. Update the Kit's own AI skill, rules and agent docs to match the change.

Gates: relevant tests green · no open high finding, and a recorded disposition for the rest · runtime proof where applicable · docs in sync · AI guidance in sync.

The dual review in step 3 runs as `/kit-review` with `{ base, spec }` in Claude Code; from a Codex-hosted session, spawn `kit-reviewer` and run a Claude inspection through the `claudex-loop` runner. Exact invocations: `docs/agents/ai-setup.md`.

## Code Review Rules

Both reviewers apply these to the diff. Report defects the diff introduced; older issues and matters of taste stay out. Flag:

- A violation of a rule under "Rules", or a name that drifts from its `GLOSSARY.md` term. A contradiction of an ADR or a section G decision is severity high.
- A test that would still pass if every collaborator returned its default, or coverage that restates another test.
- A claimed runtime guarantee without runtime proof.
- Docs or AI guidance left out of sync with the change.

## Rule router

Claude Code loads these rules by path on its own. Codex does not: read the rule before touching a matching file.

| Rule | Applies to |
|---|---|
| `.claude/rules/ai-setup.md` | `.claude/**`, `.codex/**`, `.agents/**`, `AGENTS.md`, `CLAUDE.md`, `skills-lock.json` |
| `.claude/rules/kit-code.md` | `src/**`, `tests/**`, `samples/**` |

## Agent skills

### Issue tracker

Issues and specs live as GitHub issues on `PouryaNoufallah96/PouryaKit`. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default triage labels, each named after its role. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` at the root, decisions in `docs/adr/`. See `docs/agents/domain.md`.

### The Kit's own skill

`.agents/skills/pourya-kit/` teaches developers and agents to use each Capability. `SKILL.md` routes to one file per Capability under `references/`; workflow step 7 writes or updates the reference file of the Capability a ticket delivers and its row in `SKILL.md`.
