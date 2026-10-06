# The kit owns its dispatcher and takes no mediator dependency

Commands and queries are dispatched by a small dispatcher inside the kit with a fixed pipeline, not by
MediatR. MediatR 13+ is offered under RPL-1.5 or a commercial agreement that restricts redistribution
inside frameworks and wrappers, and the kit's policy is to take no runtime dependency on a reciprocal
or commercially licensed package. Wolverine's own mediator was rejected because it has no
open-generic handlers and no `Result`-aware rollback.

## Considered Options

- MediatR 12.5.0 (last Apache release): frozen, unsupported.
- martinothamar `Mediator` 3.0.2 (source-generated; latest stable on nuget.org, published 2026-03-22,
  checked 2026-10-05; 3.1.0-rc.1 is a prerelease). `Mediator.SourceGenerator` and
  `Mediator.Abstractions` are MIT through the `LICENSE` file inside each package; nuget.org lists no
  licence expression for 3.0.x. It works with internal handlers in Module assemblies, but only by
  opening each Module's internals to the Host or by giving every Module its own generated mediator;
  Spike S3 below kept the owned dispatcher for that reason.

## Confirmed by Spike S3 (2026-10-05)

The Spike code is `spikes/s3-dispatcher/` at commit `cbc64ba6d99148423e1dcf4d5e754298ce728ef6`,
reachable through the tag `spike/s3-dispatcher`; it is not merged. Each option served the same Module
from a second assembly, with internal commands, queries, handlers, validators and authorizers behind one
public Gateway class, in a container built with `ValidateOnBuild` and `ValidateScopes`.
`dotnet test spikes/s3-dispatcher` passed 21 of 21. It needs the Microsoft.Testing.Platform runner
opt-in from the repository's root `global.json`, which is not part of the Spike commit.

Behaviour did not decide it. `OwnedDispatcherTests`, `SourceGeneratedMediatorTests` (generator in the
Host) and `GeneratorInModuleBehaviourTests` (generator in the Module) all pass
`Command_runs_logging_authorization_validation_and_unit_of_work_in_order`,
`Query_runs_the_same_pipeline_without_the_unit_of_work`,
`Ignored_nested_failure_of_any_kind_rolls_the_operation_back` (handler, validation and authorization
failures) and `Successful_nested_command_joins_the_outer_unit_of_work_which_commits_once`.

The deciding evidence is where `Mediator`'s generator has to run when handlers are internal:

- **Generator in the Host** (the placement its documentation describes). The generator writes code
  that names every command, query and handler type, so the Host fails with `error CS0122:
  'CreateThingCommand' is inaccessible due to its protection level` (in generated `Mediator.g.cs`). It
  builds only when every Module grants `InternalsVisibleTo` to the Host, which undoes the isolation
  ADR 0001 relies on.
- **Generator in each Module.** This works: with no `InternalsVisibleTo` and no generator scan in the
  Host, a Module that resolves its own generated concrete `Mediator` class passes all four behaviour
  tests in one strict container beside a second generating Module (`GeneratorInModuleBehaviourTests`).
  Each generated `AddMediator` adds its concrete class with a plain `Add`, and only `IMediator`,
  `ISender` and `IPublisher` with `TryAdd`. The costs: it is not the documented placement; every Module
  carries the generator and its own `AddMediator` call; Module code depends on a generated concrete
  class; any `ISender` or `IMediator` injection silently binds to the first registered Module, whose
  mediator throws `MissingMessageHandlerException` for another Module's commands
  (`GeneratorInModuleTests.Two_modules_each_generating_a_mediator_share_one_ISender_that_knows_only_the_first`),
  and the strict container does not catch it; and the Kit cannot offer one dispatch abstraction of its
  own for Gateways and handlers to depend on.
- **Owned code.** `Mediator` saves little. The pipeline steps, failure tracking and validator and
  authorizer discovery remain Kit code either way. Counting non-blank lines that are not comments or
  lone braces, the owned dispatcher (contracts, pipeline and startup check) is 84 lines and the
  `Mediator` behaviours plus registration are 58. Per-dispatch cost was not measured, because it could
  not outweigh the points above.

The owned dispatcher discovers internal handlers by reflection, with no attribute, no
`InternalsVisibleTo` and one `AddDispatcher(moduleAssembly)` call. With `Mediator`, these wiring costs
also fall on every project that runs the generator:

- **Lifetime.** `ServiceLifetime = Scoped` must be set in its `AddMediator` call. The default Singleton
  lifetime fails `ValidateOnBuild`: "Cannot consume scoped service 'Spike.Common.OperationTrace' from
  singleton". (`Assemblies` may stay unset; the generator then scans every referenced assembly.)
- **Missing handler.** It is build warning `MSG0005` by default; the container still builds, and the
  first send throws `MissingMessageHandlerException`
  (`Missing_handler_is_only_a_build_warning_and_fails_on_first_send`). With
  `<WarningsAsErrors>MSG0005</WarningsAsErrors>` in the generator's project the build fails
  (`CSC : error MSG0005`), which is earlier than the owned registration's check, but each such project
  must opt in. The owned registration throws before the container is built when a command or query has
  no handler (`Missing_handler_fails_at_registration_before_the_container_is_built`); the same check
  rejects several handlers, a case the Spike did not test.
- **Licence.** MIT, so not a differentiator.

### Shape for ticket #7

Adapt the following to `IUnitOfWork` (ADR 0003) and a real logger:

```csharp
internal sealed class Dispatcher(IServiceProvider services, OperationState operation, IUnitOfWork unitOfWork) : IDispatcher
{
    // Send/Query: one cached generic invoker per operation type resolves ICommandHandler<TCommand, T>
    // or IQueryHandler<TQuery, T> and calls Run.

    // Failure tracking wraps the whole pipeline, outside every short-circuiting step.
    private async Task<Result<T>> Run<TOp, T>(TOp op, bool isCommand, Func<CancellationToken, Task<Result<T>>> handle, CancellationToken ct)
    {
        var result = await Pipeline(op, isCommand, handle, ct);
        if (isCommand && result.IsFailure) operation.MarkRollbackOnly();
        return result;
    }

    // The fixed order is written out here, never registered: logging, authorization hook, validation, Unit of Work.
    private async Task<Result<T>> Pipeline<TOp, T>(TOp op, bool isCommand, Func<CancellationToken, Task<Result<T>>> handle, CancellationToken ct)
    {
        // logging scope opens here and closes in a finally
        foreach (var authorizer in services.GetServices<IAuthorizer<TOp>>())   // none registered: the step does nothing
            if (authorizer.Authorize(op) is { } denied) return denied;
        var errors = services.GetServices<IValidator<TOp>>().SelectMany(v => v.Validate(op)).ToList();
        if (errors.Count > 0) return Result<T>.Fail(errors);
        if (!isCommand) return await handle(ct);
        // A command starts the Unit of Work, or joins it when a Unit of Work is already active. The
        // owner rolls back on a failed Result or when the Operation is rollback-only.
        return await unitOfWork.Run(handle, ct);
    }
}
// AddDispatcher(params Assembly[] moduleAssemblies) registers internal handlers, validators and
// authorizers by reflection, then throws when any command or query has zero or several handlers.
```

The Spike decided "join" by nesting depth, which also counts queries, so a command sent from a query
handler would join a Unit of Work nobody owns. Open points for ticket #7:

- whether a failed nested query marks the Operation rollback-only too (the Spike marked failed nested
  commands only); *answered by ticket #7: it does not, see the amendment below;*
- whether a query handler may send a command at all. *Answered by ticket #7: it may not, see the amendment below.*

### Invariants to keep as permanent tests (ticket #7)

Ticket #7 proves these through dispatch with a stand-in Unit of Work, with handlers as internal types
in a separate test Module assembly:

1. A command passes logging, authorization, validation and the Unit of Work in that order; a query
   passes the same steps without the Unit of Work.
2. A nested command that fails in its handler, its validation or its authorization marks the Operation
   rollback-only, and the outer Unit of Work rolls back even though the outer handler ignored the
   failure. A successful nested command joins the outer Unit of Work, which commits once. The Gateway
   seam proves the same against PostgreSQL, data and Outbox included, in Phase 2 (plan section E).
3. Handlers that are internal types in another assembly are found without `InternalsVisibleTo`, and a
   command or query with zero or several handlers fails registration at startup.

## Amendment (2026-10-06, Wave 1 as built)

The decision above stands. Ticket #7 closed its two open points, and these rules are now behaviour Feature code relies
on (`src/PouryaKit.Cqrs/Dispatcher.cs`, `src/PouryaKit.Cqrs/Operation.cs`):

- **A failed nested query marks nothing.** A command that fails, at any step or by an exception, or a failed Domain Event Handler,
  marks the Operation rollback-only; a query marks nothing, because it has no effect to undo
  (`DispatchTests.A_failed_nested_query_leaves_the_operation_free_to_commit`).
- **A query handler that sends a command throws** `InvalidOperationException`: a query runs without a Unit of Work, so
  it cannot change state (`DispatchTests.A_query_handler_that_sends_a_command_throws`).
- **Authorizers and validators may run queries, never commands, at any depth**, a nested command's own checks included:
  they run before their command may proceed, so a command they sent would change state for one that may still be
  refused (`DispatchTests.An_authorizer_that_sends_a_command_throws_before_the_nested_handler_runs`,
  `DispatchTests.A_nested_commands_authorizer_that_sends_a_command_throws_inside_the_unit_of_work`,
  `DispatchTests.An_authorizer_may_run_a_query_before_the_unit_of_work_starts`).
- **The authorization hook runs for queries as well as commands.** Every `IAuthorizer<T>` registered for a query runs
  before its validation, as invariant 1 above already states
  (`DispatchTests.A_query_runs_the_same_steps_without_the_unit_of_work`). Plan G.6 calls it a "command-level"
  authorization hook; the hook covers queries too, and this was reported to the owner when ticket #7 closed. G.6 is
  unchanged. Narrowing the hook to commands would leave every query authorizer an application registered silently
  unrun.
