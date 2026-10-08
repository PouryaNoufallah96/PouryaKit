---
name: messaging-checks-at-build-facts
description: Final review package B facts - routing refusals as RoutingCheck at the build, WireNaming.Declare is load-bearing, outbound write wraps JsonException, Stub owns every test-assembly handler, a python runner for several test classes
metadata:
  type: project
---

Verified in the Wave 2 final review package B (2026-10-08), WolverineFx 6.46.0; check the merged code first:

- **`RoutingCheck` (an `ICompositionCheck` from `UsePouryaKitWolverine`) holds the routing refusals and calls `WireNaming.Declare`** at `Build()`, only when its own lines are empty. Declare is load-bearing: with the check unregistered, four delivery tests failed (wire names never declared), not just the refusal tests. A Host that fails another check still declares its names for the process; tests must keep one wire name per contract type across Hosts (they do; `tests.folder.v1` is declaration-time only).
- **Adding a line to the check gates Declare:** a test that proves a Declare refusal (named-before-declared) must have no other routing line, so a `Receive` there needs a subscription.
- **`SubscriptionUnitOfWorkCheck`** adds a line to any plain test Host that subscribes without a Unit of Work (`StandInOutbox` Hosts): assert `Contains(line, Message.Split(NewLine))`. EF Core-overload Hosts without one already fail `ValidateOnBuild` first (`EfReceipts` needs `IUnitOfWork`).
- **Outbound strict write:** the null throws `JsonException` inside `Envelope.Data`, which Wolverine wraps in `Wolverine.WolverineSerializationException` ("Error trying to serialize message of type …"); the dead letter's `exception_type` is the wrapper. Stack: `WolverineOutbox.WriteAsync` (in `EfUnitOfWork.RunAsync` before the save) -> `MessageBus.PersistOrSendAsync` -> `EfCoreEnvelopeTransaction.PersistIncomingAsync` (unmapped) -> `DatabasePersistence.BuildIncomingStorageCommand` -> `EnvelopeSerializer.Serialize`. Red by `json.UsePouryaKitJson()` in `UsePouryaKitWolverine`, inside the package, instead of touching Domain. The dead-letter table keeps no stack: read it from `Fixture.Logs` entries' exceptions.
- **The Stub owns every handler in `ModularMonolith.Tests`** (its `AddPouryaKitCqrs()` scans the assembly), so `QueryOnlyModule` and similar cannot be composed beside the Stub in `AppFixture.ComposeHost` ("registers a handler for …, which belongs to StubModule"). A subscription-only Module can (`Subscribe` registers no command handler).
- **Several test classes in one command:** a zsh `for` over class names is refused by the isolation check; a scratchpad Python script running `dotnet test --filter-class` per class through `subprocess` with `cwd=<worktree>` passes. Same for mutation runs (copy, mutate, build, run, restore in `finally`).
- The coordinator may move the integration branch mid-package and ask for a merge: `git merge --ff-only <branch>` worked with uncommitted edits the tip did not touch; apply doc edits after it so anchors match the new tip.

**Why:** each cost a run or decided a test of this package.
**How to apply:** for any further composition check in Messaging.Wolverine, any test of the Outbox write path, or a test Module beside the Stub. Related: [[misconfigured-host-facts]], [[strict-wire-reading-facts]], [[integration-event-facts]], [[module-seam-and-composition-check-facts]].
