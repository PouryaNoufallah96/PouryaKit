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
  licence expression for 3.0.x. Rejected by Spike S3 below.

## Confirmed by Spike S3 (2026-10-05)

The Spike code is `spikes/s3-dispatcher/` at commit `926e434379dac9d26ff7c59b9cbd5348a2762334`. It is
not merged. Both options served the same Module from a second assembly, with internal commands,
queries, handlers, validators and authorizers behind one public Gateway class, in a container built
with `ValidateOnBuild` and `ValidateScopes`. `dotnet test spikes/s3-dispatcher` passed 15 of 15.

Behaviour did not decide it. `OwnedDispatcherTests` and `SourceGeneratedMediatorTests` both pass
`Command_runs_logging_authorization_validation_and_unit_of_work_in_order`,
`Query_runs_the_same_pipeline_without_the_unit_of_work`,
`Ignored_nested_failure_of_any_kind_rolls_the_operation_back` (handler, validation and authorization
failures) and `Successful_nested_command_joins_the_outer_unit_of_work_which_commits_once`.

The deciding evidence, all against `Mediator`:

- **Internal handlers in a Module assembly.** The generator runs in the Host and writes code that names
  every command, query and handler type, so the Host fails with `error CS0122: 'CreateThingCommand' is
  inaccessible due to its protection level` (in generated `Mediator.g.cs`). It builds only when every
  Module grants `InternalsVisibleTo` to the Host, which undoes the isolation ADR 0001 relies on. The
  other placement, the generator inside each Module, fails in one container: each generated
  `AddMediator` adds `ISender` with `TryAdd`, so the first Module's mediator serves all of them and the
  second Module's commands throw `MissingMessageHandlerException`
  (`GeneratorInModuleTests.Two_modules_each_generating_a_mediator_share_one_ISender_that_knows_only_the_first`).
  The owned dispatcher discovers internal handlers by reflection, with no attribute and no
  `InternalsVisibleTo`.
- **Missing handler.** With `Mediator` it is build warning `MSG0005`; the container still builds, and
  the first send throws `MissingMessageHandlerException`
  (`Missing_handler_is_only_a_build_warning_and_fails_on_first_send`). The owned registration throws
  before the container is built when a command or query has no handler
  (`Missing_handler_fails_at_registration_before_the_container_is_built`); the same check rejects
  several handlers, a case the Spike did not test.
- **Host wiring.** `AddMediator` is generated into the Host, so the Kit cannot ship the call. Every Host
  must reference the generator and call `AddMediator` itself, setting `Assemblies` and
  `ServiceLifetime = Scoped`. The default Singleton lifetime fails `ValidateOnBuild`: "Cannot consume
  scoped service 'Spike.Common.OperationTrace' from singleton". With the owned dispatcher, a Host makes
  one `AddDispatcher(moduleAssembly)` call.
- **Owned code.** `Mediator` saves little. The pipeline steps, failure tracking and validator and
  authorizer discovery remain Kit code either way. Counting non-blank lines that are not comments or
  lone braces, the owned dispatcher (contracts, pipeline and startup check) is 84 lines and the
  `Mediator` behaviours plus registration are 58. Per-dispatch cost was not measured, because it could
  not outweigh the points above.

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
        operation.Depth++;
        Result<T> result;
        try { result = await Pipeline(op, isCommand, handle, ct); }
        finally { operation.Depth--; }

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
        // the outermost command owns the Unit of Work; a nested one (Depth > 1) joins it. The owner
        // rolls back on a failed Result or when the Operation is rollback-only.
        return await unitOfWork.Run(handle, ct);
    }
}
// AddDispatcher(params Assembly[] moduleAssemblies) registers internal handlers, validators and
// authorizers by reflection, then throws when any command or query has zero or several handlers.
```

The Spike marked rollback-only for failed nested commands only. Ticket #7 decides whether a failed
nested query should mark it too.

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
