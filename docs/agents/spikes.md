# Spikes

A Spike answers one design question with throwaway code. What lasts is its decision record (G.10).

## Mechanics

- A Spike lives in `spikes/<name>/` on its own branch and is self-contained: its own `Directory.Build.props` and `Directory.Packages.props` stop the checkout's root ones, which otherwise reach a nested worktree. `global.json` cannot be stopped, so its tests follow the repository's runner (`.claude/rules/kit-code.md`, "Stack").
- Only the decision record is merged. The code is not merged and stays reachable through a pushed tag `spike/<name>`; that is how G.10's "deleted once the decision is recorded" is met. Commit the code first and the record second, so the record cites a final hash.
- When Spikes run in parallel, the orchestrator assigns each record's ADR number in the brief.
- The record follows `.agents/skills/domain-modeling/ADR-FORMAT.md`. Because the code is not merged, it also carries what a later implementer needs: pinned versions and licences, the registration or code shape that worked as a short snippet, and each invariant worth a permanent test with its seam and owning ticket.

## Evidence standard

All three Phase 0 records overstated their evidence at first: two deciding points reversed under a fair configuration of the rejected option, one real gap was missed, one decision was blocked and reversed. A record holds when:

- Every claim names the test that shows it and the configuration it holds under.
- Observed and inferred are labelled. A statement from reading source or documentation says so and cites repository, tag and path.
- A rejected option got one honest attempt to make it work: the deciding evidence survives a fair configuration of that option, not only the adapter written first.
- A count used as a reason (owned lines, rows, seconds) states its counting rule and is taken from the code that ran.
- Status is `accepted` only when no open question can reverse the decision.
