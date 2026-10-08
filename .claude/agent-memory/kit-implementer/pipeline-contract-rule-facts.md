---
name: pipeline-contract-rule-facts
description: Ticket #54 slot rule - open-generic implements check, which contracts the set holds and why, Sample.Application scope with both Hosts, source-file reporting differs local vs CI, dumping a long xUnit list
metadata:
  type: project
---

Verified in ticket #54's slot-rule package (2026-10-08), `PipelineContractTests` and `ModuleRules.ImplementsPipelineContracts`:

- **`type.IsAssignableTo(typeof(IValidationStep<>))` is always false.** Match an open generic contract with `type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == contract)`; an open generic class (`FluentValidationStep`1`) is caught that way, and its `FullName` keeps the backtick.
- **The set is the pipeline's entries and seams**, 13 contracts. The orchestrator decided (2026-10-08) that `IDispatcher` and `IGatewayProcessor<T>` belong: Feature code calls them, but an application decorator is a pipeline step by another name; `IDispatcher` has two Kit implementations (Cqrs's `Dispatcher`, Observability's `ObservedDispatcher`), the rest one each. Left out on purpose: `IRequiresQueryCache` (every `ICacheableQuery` of the sample implements it transitively, so an assignability rule flags legitimate queries), composition-time contracts (`ICompositionCheck`, `IStartupCheck`, `IModuleServices`, `IRequiresAuthorizer`: they add nothing to the pipeline), `HostComposition` (sealed, internal constructor), sealed classes of the calling rule (`DomainEventDrain`, `TrackedState`).
- **Both Hosts since #54.** The arch-test csproj references `Inventory.Host` too, and `Sample.Application` is both Hosts plus every repository project they reach whose name does not start with `PouryaKit` (Modules, Contracts, `ModularMonolith.ServiceDefaults`); the rule judges that. `Sample.Modules` still comes from `ModularMonolith.Host` only, so the older rules were unaffected. A static field initializer that uses another static field must come after it in the class.
- **A rule that names the source file would differ between builds:** sample and test projects have a portable PDB beside the dll, Kit packages an embedded one, and `validate.py` builds with `ContinuousIntegrationBuild=true`, so document paths become `/_/…`. The rule reports the assembly's file name instead.
- **xUnit truncates a long string list in a failure.** To see a rule's full output once, temporarily `Assert.Fail(string.Join("\n", list))`, then restore from a scratchpad copy.
- ADR amendments here add an italic pointer inside the earlier amendment's sentence (`*Since <date> (ticket #n) …; see the amendment below.*`), as #61 did, and never rewrite it.

**Why:** each decided a design point or cost a run in #54.
**How to apply:** when adding a structure rule over implemented types, or extending the slot set. Related: [[architecture-test-facts]], [[implementing-types-lookup-facts]], [[core-helpers-facts]].
