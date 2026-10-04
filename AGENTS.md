# PouryaKit

A reusable .NET 10 foundation for modular monoliths built from vertical slices. Status: planning; no packages exist yet.

## Read first

- `docs/foundation-review.md` — the plan and its evidence. Section G holds the owner's final design decisions and overrides earlier sections where they differ.
- `GLOSSARY.md` — use these terms exactly; do not use the listed synonyms.
- `docs/adr/` — decisions that are expensive to reverse. If your work contradicts one, say so explicitly instead of overriding it.

## Rules

- The owner's decisions in section G are final. If implementation produces concrete evidence against one, bring the evidence to the owner; do not change the decision silently.
- One implementation per concern, one naming scheme. Adopting code from a reference project is fine; ending up with two ways to do the same thing is not.
- Cite upstream repository, commit and repo-relative path in docs and provenance records, never a local machine path.
- The kit takes no runtime dependency on a reciprocal or commercially licensed package (ADR 0002).
- Feature code never saves, commits, or touches the outbox; the unit of work owns that (ADR 0003).

## Workflow for every ticket

Not test-first. Implement, then write focused tests for behaviour that matters; no redundant coverage.

1. Implement (Opus).
2. Focused tests.
3. Two independent reviews in parallel: a Fable subagent and Codex (GPT-6 Astra, high or xhigh).
4. Fix and re-review until the gates hold.
5. Runtime proof where the ticket claims a runtime guarantee.
6. Update docs and C4 diagrams; run `/archify-review`.
7. Update the kit's own AI skill, rules and agent docs to match the change.

Gates: relevant tests green · no unresolved actionable review finding · runtime proof where applicable · docs in sync · AI guidance in sync.

## Agent skills

### Issue tracker

Issues and specs live as GitHub issues on `PouryaNoufallah96/PouryaKit`. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default triage labels, each named after its role. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: `GLOSSARY.md` at the root, decisions in `docs/adr/`. See `docs/agents/domain.md`.
