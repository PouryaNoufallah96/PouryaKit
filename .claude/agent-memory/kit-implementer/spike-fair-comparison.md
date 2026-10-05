---
name: spike-fair-comparison
description: In a Spike comparison, reject an option only for a property of its mechanism, after trying the fairest adapter for it; and cover every reaction kind the ticket names
metadata:
  type: feedback
---

When comparing options in a Spike, a failure caused by my own adapter's shape is not a property of the option. Before rejecting, try the fairest adapter (e.g. key types instead of the handler type, a pre-built handler instead of generated code) and count every option's owned code with one rule.

**Why:** the first S2 record (ADR 0006, 2026-10-05) rejected Wolverine's per-handler queues because my adapter closed generated code over an internal handler type; the review found a code-generation-free variant that won. The same record claimed Notification Handlers worked "by construction" when the chosen option dead-lettered every internal Domain Event.

**How to apply:** for each deciding claim, ask "does this follow from the option, or from my adapter?" and test each reaction kind the ticket names (Notification Handler and Integration Event Handler, with internal types) instead of inferring.
