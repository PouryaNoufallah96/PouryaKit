# Plan review log (append-only)

- Host / planner: Claude Code (Fable 5.1). Reviewer: Codex CLI 0.160.0, requested model `gpt-6-astra`, effort `high`.
- Plan: `docs/foundation-review.md`. Mode: review. Max rounds: 5. Scope: analysis and planning only; building is not authorized.
- Reviewer context limits: it sees the plan and can read local files; it did not see the eight investigation reports.

## Round 1 — Codex gpt-6-astra (high), plan sha c08bcc7f…

Runner status: **failed validation** ("Invalid limitations list" — an empty string in the list), so this
round is not a valid verdict under the runner's rules. Reply content was otherwise complete:
verdict REVISE, six findings. Artifacts: scratchpad `codex-round1/claudex-pfh_p5kk`.

| Id | Sev | Finding | Host disposition |
|---|---|---|---|
| F01 | medium | Nested command failing in validation/authorization never reaches the unit of work, so rollback-only is not set | Accepted. Failure tracking moved to the dispatcher, outside short-circuiting steps; acceptance case added |
| F02 | medium | `Kit.Messaging` declared dependency-free but its handler returns `Result` | Accepted. Messaging → Domain |
| F03 | medium | `Kit.Persistence.Dapper` needs `NpgsqlDataSource` but may not reference Npgsql | Accepted. Direct Npgsql (driver only) reference |
| F04 | medium | Sample event dropped the aggregate version; receipts do not order two different events | Accepted. Version restored; reverse-order/concurrent acceptance case added |
| F05 | medium | Cybernod has no `stored_messages` table; cutover must reconcile per-module tables and consumer receipts | Accepted. Phase 7 step rewritten |
| F06 | low | Cybernod's connection factory already leases correctly; "every candidate caches or leaks" was wrong | Accepted. Row changed from New to Adapt |

Not challenged by the reviewer: owning the kit, dropping MediatR, one host container, the receipt design.

## Round 2 — Codex gpt-6-astra (high), fresh session, plan sha e4240f90…

Runner status: **failed validation** again on the same formatting check (empty string in limitations);
not a valid verdict. Reply content complete: verdict REVISE, four new findings. Round-1 findings were not
re-raised. Artifacts: scratchpad `codex-round2/claudex-qf2ovu4u`.

| Id | Sev | Finding | Host disposition |
|---|---|---|---|
| FR-001 | high | Combining Ultrabusiness `Version` with Cybernod's interceptor leaves eventless updates without a token bump | Accepted. Unit of work is the single owner of `Version`; acceptance case added |
| FR-002 | high | Legacy internal-command receipts can outlive rolled-back effects; importing them suppresses missing effects | Accepted. Phase 7 treats them as ambiguous and reconciles the effect |
| FR-003 | medium | Sample validator dropped the `ProductId` rule; empty id would throw instead of returning a validation Result | Accepted. Rule and HTTP case restored |
| FR-004 | medium | "MediatR is prohibited" overstated: `IRequest` is in Apache-licensed `MediatR.Contracts`; RPL is governed separately | Partly accepted. Reworded as a dependency policy with both qualifications; recommendation unchanged because the pipeline needs the main package |

## Round 3 — Codex gpt-6-astra (high), fresh session, plan sha 6d061cae…

Runner status: **completed** (valid). Verdict REVISE, two findings. Artifacts: scratchpad `codex-round3`.

| Id | Sev | Finding | Host disposition |
|---|---|---|---|
| FR-001 | high | Version captured when the event is raised is the pre-bump value; several changes in one transaction yield snapshots with equal versions, and the receiver's strictly-newer rule can drop the final one | Accepted. Facts and state snapshots separated; the kit builds one snapshot per changed aggregate after the drain, with the committed version; acceptance case added |
| FR-002 | high | Notification and receiving receipts in Cybernod can prove only a publish attempt or a scheduling step | Accepted. Phase 7 classifies receipts by the effect they prove; two migration cases added |

## Owner decisions (2026-10-04)

One host container · adopt best implementation from any source but keep one consistent design, cite
upstream repo/commit/path not local paths, Ultrabusiness at pattern level · private first, public
later · monolith/modular monolith first with transport-neutral messaging contracts, RabbitMQ in wave 2.

## Round 4 — Codex gpt-6-astra (high), resumed session, plan sha 9bb2e88e…

Runner status: **completed**. Verdict REVISE, one finding; both round-3 findings confirmed resolved.

| Id | Sev | Finding | Host disposition |
|---|---|---|---|
| FR-005 | medium | Audit and soft-delete interceptors change state during `SaveChanges`, after snapshots are built; a deleted aggregate would export live state | Accepted. Fixed five-step commit sequence with a prepare step before snapshot construction; tombstone snapshots; acceptance cases added |

## Round 5 — Codex gpt-6-astra (high), resumed session, plan sha 4b107b43…

Runner status: **completed**. Verdict REVISE, one finding; FR-005 confirmed resolved. Round budget (5) exhausted.

| Id | Sev | Finding | Host disposition |
|---|---|---|---|
| FR-006 | medium | Sample Dapper query ignores soft delete; Dapper bypasses EF query filters | Accepted. Predicate added to the sample; acceptance case added to Phase 2 |

## Outcome

No round returned APPROVED. Every finding raised (6 + 4 + 2 + 1 + 1) was accepted and applied; none is
outstanding in the host's view. The plan changed after round 5, so the current file has **not** been
reviewed in its final form and must not be described as Codex-approved. Core decisions (owned kit, owned
dispatcher, one container, Result-aware unit of work, per-consumer receipt, Wolverine + PostgreSQL) were
not challenged in any round. Findings from round 3 onward concerned the sample and the Cybernod migration.

## Second loop — final plan including owner decisions (section G) and workflow (section H)

Reviewer: Codex gpt-6-astra, effort high, fresh session. Budget: 3 rounds. Plan now excludes any
migration of existing projects.

### Round 1 — completed, REVISE, plan sha 0767fe99…

| Id | Sev | Finding | Host disposition |
|---|---|---|---|
| PK-001 | high | Rollback-only is checked before the drain; a nested command failing inside a domain reaction could still commit | Accepted. Failed reaction Results stop the drain; flag rechecked after the drain; acceptance case added |
| PK-002 | medium | A Dapper write to an aggregate table joins the transaction but bypasses versioning and snapshots | Accepted. Dapper writes limited to non-aggregate tables; architecture test added |

### Round 2 — completed, **APPROVED**, plan sha e3d99098…

PK-001 and PK-002 confirmed resolved; no further material defects. Approval is bound to that exact
plan hash and covers the plan only: upstream source claims, package compatibility, licensing and
Wolverine runtime behaviour were not independently verified by the reviewer, and the Phase 0 spikes
and runtime acceptance checks remain outstanding.

## Third loop — section I, the catalogue amendment (2026-10-06)

Reviewer: Codex gpt-6-astra, effort high, fresh session, one round (a second only for a high finding).
Scope of interest: section I only; sections A–H were reviewed in the earlier loops.

### Round 1 — completed, REVISE, plan sha 8af09e94…

| Id | Sev | Finding | Host disposition |
|---|---|---|---|
| I-001 | high | EF filter alone cannot isolate tenants: sample reads are Dapper and projection writes key on product id only | Accepted. Tenant parameter on every Dapper read/count (architecture test), tenant-keyed projections, tenant check in the prepare step, cross-tenant acceptance cases |
| I-002 | high | Idempotency key excluded tenant | Accepted. Key is (tenant, Actor, command, request id) from the start; null tenant as sentinel |
| I-003 | high | `EfReceipts` is global and throws for a non-EF Unit of Work, so a Stream Module's Notifications cannot be delivered | Accepted. `MartenReceipts` selected per receiving Module; spike extended with three delivery checks |
| I-004 | medium | A query nested in a write Operation could cache uncommitted state | Accepted. Cache bypass inside an active write Unit of Work; invalidation after outermost commit |
| I-005 | medium | `RemoveProducts` also changes `GetProduct` but was not in the invalidation contract | Accepted. Resource tags across Actor keys; every writer declares its tag |
| I-006 | high | All scheduled work ran as a system Actor, contradicting G.6/ADR 0007 for delayed continuations | Accepted. Delayed continuation keeps its Operation Context; recurring work needs a named system Actor |
| I-007 | medium | "Unchanged Module code" overstated: Composition Root and endpoints depend on Modularity | Accepted. Promise limited to business code; template generates the composition-specific adapters; monolith smoke test |

High findings present → one second round, as the owner's brief allows.

### Round 2 — completed, **APPROVED**, plan sha b33eed72…

All seven findings confirmed resolved. Approval covers the plan; the Marten, RabbitMQ and tracing
spikes and the three owner questions in I.6 remain prerequisites for the affected capabilities.
