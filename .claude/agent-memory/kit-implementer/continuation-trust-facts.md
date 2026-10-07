---
name: continuation-trust-facts
description: Ticket #57 facts - why a Kit-only trust mark needed the one InternalsVisibleTo, why Continue() never grants trust, where trust is set, IL rule for opened internals
metadata:
  type: project
---

Verified in ticket #57 (2026-10-07):

- **Accessibility alone cannot give a cross-package Kit-only setter.** Security reads the mark, so it needs a public getter. Delivery (Messaging.Wolverine) and Web (#48 trusted peer) set it, so they need some entry point. Without `InternalsVisibleTo`, any API they can reach, an application can reach too. An in-process secret does not help, because application code can read configuration and DI. The orchestrator chose Cqrs's one IVT, to Messaging.Wolverine and Web only, for `OperationContext.RestoreTrusted`. `TrustedRestoreTests` pins the list and the internal members the IL may use.
- **`Continue()` never grants trust, even inside a running Operation.** Otherwise any unprotected Operation the application controls could launder a context into a trusted one. Trust keyed on "begun" is weaker still, because `BeginOperation` is public. In-process continuations (Contracts questions) are authorized like nested commands.
- **The dispatcher never calls `Continue()`.** A nested command shares the scope's context. Both #38 continuation tests go through delivery. The trust is set in `OperationContextHeaderExtensions.ContinuedOperationContext`, not in `SubscriptionDelivery`.
- `public bool X { get; private init; }` on a record plus an internal static factory using `{ X = true }`: `with` from outside can only clone it, and STJ ignores a non-public init. `Actor` has a private constructor, so `OperationContext` cannot be deserialized by STJ anyway.
- IL rule for "uses no internal of assembly A": judge a constructed generic type by its definition. `CommandHandler<InternalCommand>` has `IsVisible == false`, and judging it as constructed is a false positive. `Il.CallsIn` covers call, callvirt, newobj and ldftn; it does not cover field access.

**Why:** these facts decided the #57 shape and cost one blocked round.
**How to apply:** for #44 (delayed continuations must come back through `ContinuedOperationContext` to stay trusted), for #48 (call `RestoreTrusted`, and extend `TrustedRestoreTests` only if Web's use differs), and for any later Kit-only cross-package seam. Related: [[security-permission-facts]], [[declared-authorizer-check-facts]], [[architecture-test-facts]].
