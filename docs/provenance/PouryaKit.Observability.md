# PouryaKit.Observability provenance

Adopted implementations in `PouryaKit.Observability`. Format and rules: [README](README.md).

The trace through the Outbox is new Kit code, decided in [ADR 0009](../adr/0009-w3c-trace-context-through-the-outbox.md), so it has no record here. That covers the activity's placement around the whole dispatch, the tracestate header the Outbox writes (`src/PouryaKit.Messaging.Wolverine.EFCore/WolverineOutbox.cs`), the parent each delivery restores (`src/PouryaKit.Messaging.Wolverine/OperationContextHeaderExtensions.cs`, recorded as `Operation.TraceParent` in `src/PouryaKit.Cqrs/Operations/Operation.cs`) and the parent fallback in `src/PouryaKit.Observability/ObservedDispatcher.cs`. Wolverine's own `parent-id` handling, which the Kit relies on, is cited there from `jasperfx/wolverine` at tag `V6.46.0`.

## Package dependencies

None shipped. `PouryaKit.Observability` depends on `PouryaKit.Cqrs` only and uses `System.Diagnostics` and `System.Diagnostics.Metrics` from the BCL, with `ActivatorUtilities` from `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.12 (MIT), which arrives through Cqrs. Plan section C first listed `PouryaKit.Messaging` too. Nothing from it is used, because a delivery records the parent it restored on Cqrs's `Operation`, so the reference was left out (ticket #47). No package was added or bumped. Its private MinVer build reference is recorded in [shared private build tools](README.md#shared-private-build-tools).

## Operation activity and duration

- **Kit files:** `src/PouryaKit.Observability/ObservedDispatcher.cs`, `src/PouryaKit.Observability/OperationTelemetry.cs`, `src/PouryaKit.Observability/ObservabilityServiceCollectionExtensions.cs`
- **Upstream:** https://github.com/PouryaNoufallah96/cybernod
- **Commit:** bae8ac3b7e46f09e9375647786d03391a500876a
- **Upstream paths:** `src/BuildingBlocks/BuildingBlocks.Core/Observability/Behaviors/ObservabilityPipelineBehavior.cs`, `src/BuildingBlocks/BuildingBlocks.Core/Observability/CoreDiagnostics/Commands/CommandHandlerMetrics.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #47
- **Intentional differences:**
  - One wrapper around the Kit's scoped dispatcher, not a MediatR pipeline behaviour with separate command and query activity and metric classes. The Kit has no mediator (ADR 0002), and the wrapper covers the Unit of Work, so the activity is current while the Outbox is written.
  - The duration is a `double` histogram of `Stopwatch` seconds in unit `s`, as upstream's `TotalSeconds` (`CommandHandlerMetrics.cs:33-37,118-122`). The Kit records it for every outcome, with an outcome tag, where upstream records only successes and counts failures apart. Recording whole seconds, as other references do, is rejected.
  - Tags are the Module, the type name, the Actor kind and the outcome. Upstream's handler-type tags come from scanning the command's assembly on every call (`CommandHandlerMetrics.cs:48-57`), and the Kit has no such scan.
  - No active, total, success or failure counters: the histogram's count per outcome gives them.
  - A failed Result is an outcome `failure` with status `Error`. Upstream sees only exceptions.

## Operation duration bucket boundaries

- **Kit files:** `src/PouryaKit.Observability/ObservedDispatcher.cs`
- **Upstream:** https://github.com/dotnet/aspnetcore
- **Commit:** 7387de91234d3ef751fa50b3d1bfede4130213ff
- **Upstream paths:** `src/Shared/Metrics/MetricsConstants.cs`, `src/Hosting/Hosting/src/Internal/HostingMetrics.cs`
- **Licence:** MIT
- **Adoption:** pattern
- **Ticket:** #84
- **Intentional differences:**
  - The values only: the fourteen second-scale boundaries of `MetricsConstants.ShortSecondsBucketBoundaries`, which ASP.NET Core declares as the `InstrumentAdvice<double>` of `http.server.request.duration` and which follow OpenTelemetry's semantic conventions for that histogram. The Kit declares them as the advice of `pouryakit.operation.duration`; no upstream code is taken.
