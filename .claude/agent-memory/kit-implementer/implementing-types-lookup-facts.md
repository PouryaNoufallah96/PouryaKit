---
name: implementing-types-lookup-facts
description: Ticket #55 facts - Of vs Candidates, GetTypes order is MetadataToken order and a pinned message depends on it, typeof(object) as every class, the type-listing structure rule
metadata:
  type: project
---

Verified in ticket #55 (2026-10-07):

- **`ImplementingTypes.Of` never returns an abstract class or an open generic definition**, so a registration that rejects those (Cqrs's generic handlers, Security's permission on an abstract or generic type, Messaging.Wolverine's generic Notification Handler) cannot use it. The first brief stopped on that; the orchestrator added `ImplementingTypes.Candidates`: every class the contract describes, abstract, open generic and compiler-generated included, interfaces and structs out. Excluding compiler-generated classes was a review high (F2): the old scans never did, so a marked type would have been skipped silently. Match the old enumeration exactly, and record a widened Core contract as a dated note on the plan's I.3 row (F1). `Of` and `Candidates` share private `ThrowIfNotAContract` and `Describes`.
- **`Assembly.GetTypes()` order is `MetadataToken` order (declaration order), and tests depend on it:** `NotificationTests.Startup_fails_on_a_missing_an_invalid_or_a_duplicate_name` pins which of two clashing Domain Events is named first. Full-name order flipped it. Cqrs and Messaging.Wolverine re-sort `Candidates` by `MetadataToken` (Cqrs by assembly position first) to keep the old order; Web accepts full-name order.
- **`Candidates(typeof(object), assemblies)` means every class**: Cqrs and Security use it, because Security must see the attribute on a type that is no Operation.
- An iterator method gives a `[CompilerGenerated]` class implementing `IEnumerable<T>`, a cheap fixture for the compiler-generated case.
- `TypeLookupTests` reads the IL for `Assembly.GetTypes`, `get_DefinedTypes`, `get_ExportedTypes`, `GetExportedTypes`, `get_Modules`, `GetModules`, `Module.GetTypes` and `Module.FindTypes`; ordinal sort puts `GetX` before `get_X` in the expected list.
- Adding a Kit project reference also changes `PackageGraphTests`' inline Dapper nuspec. A Host-killing test, `IntegrationEventTests...died_before_acknowledging_it_is_applied_once`, failed once in validate.py with a delivery turned away twice (the Solo recovery race); the rerun passed.

- **Wolverine's shape rejections had no test** before #55's fix round. Any type added to `ModularMonolith.Tests` itself breaks the fixture Host (the Stub Module scans that assembly), and a new fixture project needs a slnx entry. The stand-ins went into the existing `Misnamed` project, and the two startup tests split its message on the `" is a Notification Handler of "` marker. The naming test's name is cited in ADR 0006, so do not rename it.
- The integration branch moved during the fix round (#56 merged into Security and Modularity). `git merge-tree --write-tree --name-only HEAD <branch>` probes for conflicts without touching the tree.

**Why:** each cost a stop, a run or a review finding in #55.
**How to apply:** when a Kit registration finds types, when a refactor changes the order types are enumerated in, or when adding a Kit project reference. Related: [[core-helpers-facts]], [[architecture-test-facts]], [[dead-letter-and-stale-time-facts]].
