---
name: observability-facts
description: Ticket #47 facts - ActivityListener misses a lazily created static source, dispatcher decorator ordering, why the trace parent sits on Operation, one-shot delivery failure probe, mutation results
metadata:
  type: project
---

Verified in ticket #47 (2026-10-07), WolverineFx 6.46.0, .NET 10.0.12. Check the merged code before you rely on any of this:

- **A listener never gets a static `ActivitySource` that its own `ShouldListenTo` creates first.** `source == SomeClass.Upstream` in `ShouldListenTo` runs the class's static initializer while `AddActivityListener` is still iterating, and the new source never asks this listener. So `StartActivity` returned null, but only in the first test of a run. Match sources by name.
- **The decorator must be registered after the dispatcher.** `AddPouryaKitCqrs` uses `TryAddScoped<IDispatcher, Dispatcher>`. `AddPouryaKitObservability` replaces the last `IDispatcher` descriptor with a factory that wraps `ActivatorUtilities.CreateFactory(implementationType, [])`, and throws when there is none. Idempotence: the factory is a method on a private class, so check `descriptor.ImplementationFactory?.Target is ObservedDispatcherFactory`. AppFixture's second `AddPouryaKitModules` (StubModule) keeps the wrapper, because `TryAdd` sees it.
- **Why `Operation.TraceParent` and not a scoped holder:** Observability cannot reference Messaging.Wolverine. A holder in Messaging would make the Kit's transport-neutral contracts package register DI, and the brief left Messaging out. `ContinuedOperationContext` returns `(Context, TraceParent)`, so a new delivery path that still expects an `OperationContext` fails to compile. That is the point: #45 has to pass the parent too.
- **`Operation.Context` went public.** Resolving `OperationContext` from a scope that has not begun throws, so the decorator reads `operation.Context?.Actor.Kind`.
- **Two test seams:** `DeliveryProbes.ActivitiesOf(subscription)` records `Activity.Current` in `HeldReceipts.RecordAsync`, which runs inside the Deliver Operation activity. `FailOnceAfterReceipt` uses `Interlocked.CompareExchange` for a race-free single failure. Disposing `FailAfterReceipt` after the first attempt races the throw check.
- **Mutations** (`scratchpad/mutate47.py`): disposing the activity right after its start, which ends it before the Outbox write, makes all 4 tests red. Dropping the header makes 3 red. Removing the fallback makes the Kit-only trace test and the retry test red; with `Wolverine` sampled the test stays green by design. Recording `.Seconds` makes the outcome test red.
- validate.py at #47: 564 runtime tests, `PASS 16 packages, 18 clean consumers`, about 6 min.

**Why:** each of these cost a retry or settled a design call in #47.
**How to apply:** for #45, which must pass the parsed parent, and for #52, the RabbitMQ propagation probe, or any test that listens to activities. Related: [[w3c-propagation-facts]], [[messaging-delivery-facts]], [[worktree-and-check-gotchas]].
