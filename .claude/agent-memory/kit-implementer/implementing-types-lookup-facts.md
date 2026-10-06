---
name: implementing-types-lookup-facts
description: Ticket #55 facts - which Kit assembly scans ImplementingTypes.Of can replace, and the rejection paths it cannot serve (abstract and open generic types)
metadata:
  type: project
---

Found in ticket #55 (2026-10-07), first brief, blocked before any edit:

- **`ImplementingTypes.Of` never returns an abstract class or an open generic definition**, whatever the contract. Three scans need exactly those types for their startup rejections, so they cannot move to `Of` plus a filter without dropping a check:
  - Cqrs `AddPouryaKitCqrs`: "X is generic" problems over `classes` (generic definitions included); pinned by `PouryaKit.Cqrs.Tests/RegistrationTests.A_generic_query_handler_or_authorizer_fails_registration`.
  - Security `AddPouryaKitPermissions`: every `[RequiresPermission]` type, then rejects abstract, generic and non-operation ones; pinned by `PouryaKit.Security.Tests/RegistrationTests` (asserts `AbstractCommand`, `GenericCommand<>`, `NotAnOperation`).
  - Messaging.Wolverine `AddPouryaKitNotificationHandlers`: rejects a generic handler definition; only its XML doc pins it, no test.
- **The other three map cleanly:** Web endpoints = `Of(typeof(IEndpoint), assembly)` (order becomes FullName-ordinal); Dapper and EFCore typed IDs = `Of(typeof(TypedId<>), ...)` filtered by `TypedId.GetValueType` (typed IDs are record classes; a type nested in a generic is itself a generic definition, so `ContainsGenericParameters` adds nothing). EFCore's `ApplyConfigurationsFromAssembly` is EF's own scan, not a Kit `GetTypes()` call.

**Why:** the brief assumed a filter on top of `Of` suffices everywhere; it does for registrations, not for the rejection paths.
**How to apply:** when #55 is re-briefed, check which option the owner picked (a Core method that includes abstract and open generic classes, dropping the checks, or an exemption) before editing. Related: [[core-helpers-facts]], [[architecture-test-facts]].
