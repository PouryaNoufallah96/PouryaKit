---
name: versioning-facts
description: Ticket #49 facts - Wolverine type-route name matching, decompiling Wolverine with ilspycmd, asking main to widen allowed_files, Receipt-key mutation scope
metadata:
  type: project
---

Ticket #49 (2026-10-07), WolverineFx 6.46.0; check the merged code before relying on the design:

- **Wolverine's type route matches by name, not by Type.** `PublishMessage(type)` adds `Subscription.ForType` (`RoutingScope.Type`, `Match = type.FullName`), and `Matches(m)` is true when `m.Name`, `m.FullName` or `m.ToMessageTypeName()` equals that FullName ignoring case. A record deriving from another is NOT routed to the base's subscriptions; a type whose wire name equals another subscribed type's full name IS (probe: routed to `local://tests.routed/`). The Kit refuses it in `Subscriptions.RoutingCollisions`, run by the extension before `WireNaming.Declare`.
- **Reading Wolverine source:** no sources ship in the nupkg; `ilspycmd -p -o <scratch dir> ~/.nuget/packages/wolverinefx/6.46.0/lib/net10.0/Wolverine.dll` (global tool installed) decompiles it in seconds. Inspect routes at runtime with `IWolverineRuntime.RoutingFor(type).Routes.OfType<MessageRoute>().Select(r => r.Uri)`.
- **A brief whose allowed_files miss a needed file:** `SendMessage` to `main` reached the coordinator mid-task, and approval came back as a course correction while work went on. The Publish declaration lives in the Module's Composition Root (`CatalogModule.cs`), outside the Notifications folder; the skill's `SKILL.md` row is often missing from allowed_files too (step 7): leave it and report it.
- **Red for "Receipts are per subscription":** keying every Receipt by Module also turns away Catalog's second Notification Handler (one Domain Event id), so nothing is published at all; scope the mutation to Integration Events (`IsNotification ? Name : Module.Name`) to see Inventory's v1 or v2 effect go missing.
- Publishing a second Integration Event type changes the exact delivery list in `NotificationTests.A_new_product_is_announced...`; `IndependentDeliveryTests` already filters by wire name. Any new sample table changes the exact table list in `PersistenceTests.Each_module_has_its_own_schema_and_migrations_history_with_snake_case_names`.

- #49 fix round: version-migration guidance is easy to get wrong. Receipts are keyed by subscription name, so a v1 and a v2 subscription applying the same effect both apply it. Removing the v1 subscription before the v2 one is live loses effects, because a type with no subscription is not stored. Stopping v1 starves every remaining v1 subscription, and a v1 subscription kept to drain still needs v1's `Publish` declaration or startup fails. Safe rule: replace them all in the release that stops publishing v1. Reviewers also flag "subscriber" (GLOSSARY avoid term); write "subscription".
- `validate.py --no-fetch` on #49 passed the first time (~6 min, 421 runtime tests). "Image is too small" in `control-missing-sourcelink.log` is the negative control, not a failure.

**Why:** each decided the version-isolation proof or cost a round.
**How to apply:** for #51 (RabbitMQ routes from publications, remote wire names) and any change to routes or the wire-name registry. Related: [[integration-event-facts]], [[messaging-delivery-facts]], [[snapshot-facts]].
