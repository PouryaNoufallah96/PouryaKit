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

## Amendment (2026-10-07, tickets #42 and #43: the optional idempotency and caching slots)

The decision above stands. Plan I.6 decided that the fixed pipeline has two optional slots, idempotency before the
Unit of Work and caching around a query, filled only by the Kit, and that applications still register no steps.
Ticket #42 implemented the idempotency slot at commit `829e7ad` (`src/PouryaKit.Cqrs/Dispatcher.cs`, `Steps`), and
ticket #43 the caching slot, in the same method, at commit `4aa98b7`.

The idempotency slot (ticket #42):

- **Position.** The slot belongs to the outermost command of an Operation only, after logging, the authorization hook
  and validation, and around its handler inside the Unit of Work: once `IUnitOfWork.RunAsync` has begun the
  transaction, the slot reserves the command's key before the handler runs, and records the handler's success in the
  same transaction after it returns. The Domain Event drain, the Outbox write, the save and the commit then follow as
  before. A Domain Event Handler cannot change the recorded success, but its failure rolls it back with everything else.
  A replay returns the recorded Result in place of the handler's, so the Unit of Work then has nothing to drain, write,
  save or send.
- **The Unit of Work owns the writes (ADR 0003).** The key and the success are written by an `IIdempotencyStore` in the
  Unit of Work's own transaction, and committed or rolled back with the command's effect. The dispatcher and Feature
  code never save or commit. `PouryaKit.Persistence.EFCore` implements the store with a table in each Module's schema,
  `insert … on conflict do nothing`, so a concurrent repeat waits on the uncommitted key and then replays the committed
  success, or runs when the first transaction rolled back. A key is kept for its retention, 24 hours unless
  `PouryaKitIdempotencyOptions` says otherwise, then removed by one cleanup outside every request and Unit of Work, so a
  repeat after that runs as a new request (ticket #42 follow-up, [architecture](../architecture/README.md#pouryakitpersistenceefcore)).
- **When it runs.** A command opts in with `[Idempotent]`. The slot runs when such a command is outermost and its
  `OperationContext.RequestId` is set; the key is the tenant (a null tenant stored as one fixed sentinel, the empty
  string, which no tenant can equal because `OperationContext` refuses a blank one), the Actor, the command's full
  type name and the request id. A nested command never reserves, a query never does, and an opted-in command without
  a request id runs as any other. A request id on an outermost command that does not opt in,
  or in a Host with no `IIdempotencyStore`, throws `InvalidOperationException` at dispatch, so the promised guarantee is
  never silently dropped.
- **The stored outcome is the Result, not a response.** A `Result<T>` success is kept as its value in the Kit's one JSON
  representation (`UsePouryaKitJson`), a `Result` success as the key itself. A replay returns an equal Result, never a
  default success; HTTP answers it through the same `ToHttpResult` mapping, so the status and body repeat. A failed
  Result, a failed nested command or Domain Event Handler, an exception or a refused save leaves no key, so a repeat
  runs again.
- **The request id is Operation Context data (ADR 0007).** A Gateway opens the Operation's own scope and receives only
  the context, so the request id rides on it: `PouryaKit.Web` sets it from the `Idempotency-Key` header of an endpoint
  that calls `RequireIdempotencyKey()`, and a Gateway's other callers set it with an object initializer. `Continue()`
  leaves it out and no message header carries it, so a Notification, an Integration Event or a delayed command is
  never taken for a repeat.
- **Applications still register no steps.** The slot is written out in the dispatcher like every other step;
  `[Idempotent]` and a request id switch it on, and the store is a persistence package's seam, not a step.

Proven against PostgreSQL by `IdempotencyTests` in `tests/ModularMonolith.Tests`, among them
`A_repeated_request_id_replays_the_first_success_without_running_writing_or_sending_anything`,
`A_concurrent_repeat_waits_for_the_first_operation_then_replays_its_committed_success` and
`A_concurrent_repeat_waits_for_the_first_operation_then_runs_when_it_rolled_back`, and by
`PouryaKit.Cqrs.Tests.IdempotencyTests.Only_an_outermost_command_with_a_request_id_reserves_its_key_inside_the_unit_of_work_before_its_handler`.

The caching slot (ticket #43):

- **Position.** The slot belongs to a query, after logging, the authorization hook and validation, around its handler,
  so a stored answer never skips a check. The dispatcher sends the query to the `IQueryCache` seam with its Operation's
  context, a delegate that runs the handler and one that ends its read.
- **Only the outermost query of an Operation that has not failed** (fix of #43 re-review F5 at commit `9dfa861`). The
  slot takes a query only when it is its Operation's outermost step, with no command, authorizer, validator, other
  query or step that only reads open around it (`Operation.IsOutermostQuery`), and only while the Operation is not
  rollback-only. A nested query's reading boundary begins from the tracked state the enclosing steps left, so a change
  they made passes its check, and the enclosing boundary that refuses the change closes only after the answer would be
  stored. So a query from a command's handler or a Domain Event Handler, inside the write Unit of Work where it may see
  uncommitted state, from any authorizer or validator, the outermost command's included, or from another query's
  handler runs as any query, neither read from nor stored in the cache. An Operation that has already failed, such as
  one whose command was refused before a query in the same scope, may still track what it rolled back, so its queries
  run uncached too. The trade-off: nested reads, such as a permission lookup in an authorizer, are never cached.
- **The Kit's Caching package supplies the one implementation.** `IQueryCache` is a Cqrs seam that exists for the
  Kit, as `IIdempotencyStore` exists for persistence packages; `PouryaKit.Caching` registers its implementation over
  `HybridCache`, and `PouryaKit.Cqrs` never references Caching. The dispatcher takes whatever `IQueryCache` is
  registered, so a Host registration that replaces it is possible, but outside what this ADR supports. A query opts in
  with Caching's `ICacheableQuery` (key, duration, tags). Its implementation makes one `HybridCache.GetOrCreateAsync`
  call, so concurrent misses in one Host share a handler run; another Host runs its own. The effective key adds the
  query's full type name, the tenant (a null tenant as `OperationContext.NoTenant`, the empty string #42's key table
  stores too) and the Actor's kind and id to the declared key, while tags name resources and span Actors.
- **Never a failed Result.** The factory throws a private exception that carries a failure, so `HybridCache` stores
  nothing, in memory or in a distributed cache, and every caller that joined the run receives the failure. The
  get-then-set shape that stores whatever the handler returned is the one the plan rejected (I.3 Caching).
- **Nothing a query changed.** A query's tracked-state check otherwise runs when its reading boundary closes, after its
  answer would already be stored. On the cached path the shared run handles, serializes, checks, then publishes: the
  cache serializes a success, calls the dispatcher's delegate that closes the boundary, and only then returns the
  serialized answer to `HybridCache` (fix of #43 re-review F6 at commit `9dfa861`), so the check also sees a change
  that serializing made, such as a deferred enumerable's. A change then throws, marks the Operation rollback-only, and
  is stored nowhere: every caller that joined the run receives the exception, and the next query runs the handler again.
  The serialization stays in Caching and the check in Cqrs, which never references Caching.
- **A shared run is nobody's to cancel.** Other callers may be waiting on a handler, so it runs to completion once
  started: the slot calls `GetOrCreateAsync` with no cancellation, which makes `HybridCache` run the handler on the call
  that started the run, and that call, with its scope, Operation and authorized context, waits for it. A caller's
  cancellation is observed only before the run starts or is joined. A run the starting caller could leave would go on
  for its joiners on a disposed scope, without its Operation's trust.
- **Eviction after the outermost commit only.** A command declares tags with Caching's `IInvalidatesCache`. The
  dispatcher asks the seam for the tags of every command that passes its checks, outermost and nested, collects them on
  the Operation, and evicts them only when `IUnitOfWork.RunAsync` returned the command's own success, after the
  rollback-only check. A rollback evicts nothing, so a valid entry survives a failed command. The commit has happened by
  then, so a distributed cache that refuses an eviction is logged and never fails the command.
- **Optional, at the cost of a null check.** A Host without `AddPouryaKitCaching()` has no `IQueryCache`, and every
  query runs as before. Unlike a request id, a cacheable query in such a Host promises nothing the Host then breaks, so
  nothing throws.
- **Applications still register no steps.** The slot is written out in the dispatcher; the cache is a seam the Kit
  fills, not a step.

Proven against PostgreSQL by `CachingTests` in `tests/ModularMonolith.Tests`, among them
`An_entry_answers_only_its_own_actor_and_tenant`, `A_failed_result_is_never_stored`,
`A_query_nested_in_a_write_unit_of_work_that_rolls_back_leaves_no_entry` and
`A_rolled_back_rename_evicts_nothing_so_the_valid_entry_survives`,
`A_caller_that_leaves_the_run_it_started_keeps_its_operation_so_a_joiner_gets_a_trusted_success_of_one_run`,
`A_query_that_changes_tracked_state_stores_nothing_and_its_joiner_receives_the_failure`,
`A_query_whose_answer_changes_tracked_state_when_serialized_stores_nothing_and_its_joiner_receives_the_failure`,
`A_query_nested_in_one_that_changed_tracked_state_stores_nothing_so_a_later_read_answers_the_committed_value`,
`A_query_sent_by_an_authorizer_that_changed_tracked_state_stores_nothing_so_a_later_read_answers_the_committed_value`,
`A_query_nested_in_another_is_answered_but_never_cached` and
`A_query_in_an_operation_that_already_failed_stores_nothing_so_a_later_read_answers_the_committed_value`, against a real Redis shared by two Hosts by
`With_redis_a_second_host_reads_the_entry_the_first_stored_without_running_its_handler`, and by
`PouryaKit.Cqrs.Tests.QueryCacheTests`. Neither single flight across Hosts nor eviction of another Host's in-memory
copies is claimed.
