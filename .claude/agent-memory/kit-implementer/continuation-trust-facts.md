---
name: continuation-trust-facts
description: Ticket #57 facts - the one Cqrs InternalsVisibleTo, Continue() trusted only inside its running authorized Operation, how to make ambient-trust tests non-vacuous
metadata:
  type: project
---

Verified in ticket #57 (2026-10-07):

- **Accessibility alone cannot give a cross-package Kit-only setter.** Security reads the mark, so it needs a public getter. Delivery (Messaging.Wolverine) and Web (#48 trusted peer) set it, so they need some entry point. Without `InternalsVisibleTo`, any API they can reach, an application can reach too. An in-process secret does not help, because application code can read configuration and DI. The orchestrator chose Cqrs's one IVT, to Messaging.Wolverine and Web only, for `OperationContext.RestoreTrusted`. `TrustedRestoreTests` pins the list and the internal members the IL may use.
- **G.6 wins over "never trust Continue()".** In the first round I made `Continue()` never trusted. The dual review rated that high: G.6 makes work created by an authorized Operation a trusted continuation, and the sample's `CatalogProducts` question depends on it. The rule now: trusted only on the exact context the dispatcher runs past its checks, called from inside that Operation (`AuthorizedOperation`, an ambient check set in `Dispatcher.Steps`). "Begun" is too weak, because `BeginOperation` is public. A handler continuing into protected work is G.6, a documented limit, not a gap.
- **Testing the ambient check:** checks clearing is vacuous on the outermost Operation, because its checks run before anything is set. Probe from the authorizer of a query *nested* in the authorized one. To prove the end bound, start a `Task.Run` inside the handler that waits on a TCS the test completes after the Gateway returns; `Task.Run` captures the ambient entry. A mutation that leaves a field unread breaks the build (CS0414 under TreatWarningsAsErrors), so mutate the assignment instead.
- **The dispatcher never calls `Continue()`.** A nested command shares the scope's context. Both #38 continuation tests go through delivery. The trust is set in `OperationContextHeaderExtensions.ContinuedOperationContext`, not in `SubscriptionDelivery`.
- `public bool X { get; private init; }` on a record plus an internal factory using `{ X = true }`: `with` from outside can only clone it, and STJ ignores a non-public init. `Actor`'s private constructor makes `OperationContext` non-deserializable anyway.
- IL rule for "uses no internal of assembly A": judge a constructed generic type by its definition. `CommandHandler<InternalCommand>` has `IsVisible == false`, and judging it as constructed is a false positive.
- The worktree guard refuses a long `python3 - <<EOF` heredoc that runs subprocesses; Write the script to the scratchpad and run `python3 <file>`.

**Why:** these facts decided the #57 shape and cost two rounds.
**How to apply:** for #44 (delayed continuations must come back through `ContinuedOperationContext`), for #48 (call `RestoreTrusted`), and for any later Kit-only cross-package seam or ambient check. Related: [[security-permission-facts]], [[declared-authorizer-check-facts]], [[architecture-test-facts]], [[worktree-and-check-gotchas]].
