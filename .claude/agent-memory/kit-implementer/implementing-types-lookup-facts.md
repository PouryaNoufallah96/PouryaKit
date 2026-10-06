---
name: implementing-types-lookup-facts
description: Ticket #55 facts - Of vs Candidates, GetTypes order is MetadataToken order and a pinned message depends on it, typeof(object) as every class, the type-listing structure rule
metadata:
  type: project
---

Verified in ticket #55 (2026-10-07):

- **`ImplementingTypes.Of` never returns an abstract class or an open generic definition**, so a registration that rejects those (Cqrs's generic handlers, Security's permission on an abstract or generic type, Messaging.Wolverine's generic Notification Handler) cannot use it. The first brief stopped on that; the orchestrator added `ImplementingTypes.Candidates` (every class the contract describes, abstract and open generic included, compiler-generated, interfaces and structs out). `Of` and `Candidates` share private `ThrowIfNotAContract` and `Describes`.
- **`Assembly.GetTypes()` order is `MetadataToken` order (declaration order), and tests depend on it:** `NotificationTests.Startup_fails_on_a_missing_an_invalid_or_a_duplicate_name` pins which of two clashing Domain Events is named first. Full-name order flipped it. Cqrs and Messaging.Wolverine re-sort `Candidates` by `MetadataToken` (Cqrs by assembly position first) to keep the old order; Web accepts full-name order.
- **`Candidates(typeof(object), assemblies)` means every class**: Cqrs and Security use it, because Security must see the attribute on a type that is no Operation.
- An iterator method gives a `[CompilerGenerated]` class implementing `IEnumerable<T>`: `Of` returns it and `Candidates` does not, which makes a cheap fixture.
- `TypeLookupTests` reads the IL for `Assembly.GetTypes`, `get_DefinedTypes`, `get_ExportedTypes`, `GetExportedTypes`, `get_Modules`, `GetModules`, `Module.GetTypes` and `Module.FindTypes`; ordinal sort puts `GetX` before `get_X` in the expected list.
- Adding a Kit project reference also changes `PackageGraphTests`' inline Dapper nuspec. A Host-killing test, `IntegrationEventTests...died_before_acknowledging_it_is_applied_once`, failed once in validate.py with a delivery turned away twice (the Solo recovery race); the rerun passed.

**Why:** each cost a stop or a run in #55.
**How to apply:** when a Kit registration finds types, when a refactor changes the order types are enumerated in, or when adding a Kit project reference. Related: [[core-helpers-facts]], [[architecture-test-facts]], [[dead-letter-and-stale-time-facts]].
