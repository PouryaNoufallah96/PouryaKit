using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

internal sealed partial class Dispatcher(
    IServiceProvider services,
    Operation operation,
    TimeProvider timeProvider,
    ILogger<Dispatcher> logger,
    IQueryCache? queryCache = null
) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> Invokers = new();

    // The Operation's ids and Module on every entry it logs, so its logs join their request without a tracing exporter.
    private static readonly Func<
        ILogger,
        string,
        Guid?,
        Guid?,
        string?,
        IDisposable?
    > OperationScope = LoggerMessage.DefineScope<string, Guid?, Guid?, string?>(
        "Operation {Operation} {OperationId} correlation {CorrelationId} in {Module}"
    );

    // An idempotent command's recorded response, in the one JSON representation the Kit writes everywhere.
    private static readonly JsonSerializerOptions ResponseJson =
        new JsonSerializerOptions().UsePouryaKitJson();

    // The exception last logged in this scope: one that leaves several nested pipelines is logged once, where it began.
    private Exception? _loggedException;

    public Task<Result> SendAsync(Command command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var invoker = Invokers.GetOrAdd(
            command.GetType(),
            static type => Create(typeof(CommandInvoker<>), type)
        );
        return ((Invoker<Result>)invoker).Invoke(this, command, cancellationToken);
    }

    public Task<Result<TResponse>> SendAsync<TResponse>(
        Command<TResponse> command,
        CancellationToken cancellationToken = default
    )
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        var invoker = Invokers.GetOrAdd(
            command.GetType(),
            static type => Create(typeof(CommandInvoker<,>), type, typeof(TResponse))
        );
        return ((Invoker<Result<TResponse>>)invoker).Invoke(this, command, cancellationToken);
    }

    public Task<Result<TResponse>> QueryAsync<TResponse>(
        Query<TResponse> query,
        CancellationToken cancellationToken = default
    )
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        var invoker = Invokers.GetOrAdd(
            query.GetType(),
            static type => Create(typeof(QueryInvoker<,>), type, typeof(TResponse))
        );
        return ((Invoker<Result<TResponse>>)invoker).Invoke(this, query, cancellationToken);
    }

    private static object Create(Type invoker, params Type[] arguments) =>
        Activator.CreateInstance(invoker.MakeGenericType(arguments))!;

    // Failure tracking wraps the whole pipeline, outside every step that can short-circuit, so a command that fails
    // anywhere (authorization, validation, its handler, or an exception) marks the Operation rollback-only even when
    // the handler that sent it ignores the Result. A failed query that left tracked state unchanged marks nothing.
    private async Task<TResult> Run<TCommandOrQuery, TResult>(
        TCommandOrQuery commandOrQuery,
        bool isCommand,
        Func<IServiceProvider, TCommandOrQuery, CancellationToken, Task<TResult>> handle,
        Func<ImmutableArray<Error>, TResult> fail,
        IdempotentResponses<TResult>? idempotent,
        CachedQuery<TCommandOrQuery, TResult>? cached,
        CancellationToken cancellationToken
    )
        where TCommandOrQuery : notnull
        where TResult : Result
    {
        // Everything the Operation stamps with a time, Domain Events included, reads the Host's one clock.
        using var clock = DomainClock.Use(timeProvider);
        try
        {
            var ownsUnitOfWork = false;
            IDisposable? reading = null;
            if (isCommand)
            {
                ownsUnitOfWork = operation.EnterCommand();
            }
            else
            {
                reading = operation.EnterQuery();
            }

            try
            {
                var result = await Pipeline(
                    commandOrQuery,
                    ownsUnitOfWork,
                    reading,
                    handle,
                    fail,
                    idempotent,
                    cached,
                    cancellationToken
                );
                if (isCommand && result.IsFailure)
                {
                    operation.MarkRollbackOnly(result.Errors);
                }

                return result;
            }
            finally
            {
                if (isCommand)
                {
                    operation.ExitCommand();
                }
                else
                {
                    reading!.Dispose();
                }
            }
        }
        catch (Exception) when (isCommand)
        {
            operation.MarkRollbackOnly([OperationErrors.CommandThrew]);
            throw;
        }
    }

    // The fixed order, written out here and never registered: logging, authorization hook, validation, Unit of Work.
    private async Task<TResult> Pipeline<TCommandOrQuery, TResult>(
        TCommandOrQuery commandOrQuery,
        bool ownsUnitOfWork,
        IDisposable? reading,
        Func<IServiceProvider, TCommandOrQuery, CancellationToken, Task<TResult>> handle,
        Func<ImmutableArray<Error>, TResult> fail,
        IdempotentResponses<TResult>? idempotent,
        CachedQuery<TCommandOrQuery, TResult>? cached,
        CancellationToken cancellationToken
    )
        where TCommandOrQuery : notnull
        where TResult : Result
    {
        var name = typeof(TCommandOrQuery).Name;
        var context = operation.Context;
        using var scope = OperationScope(
            logger,
            name,
            context?.OperationId,
            context?.CorrelationId,
            operation.Module?.Name
        );
        LogRunning(logger, name);
        var started = Stopwatch.GetTimestamp();

        TResult result;
        try
        {
            using (reading)
            {
                result = await Steps(
                    commandOrQuery,
                    ownsUnitOfWork,
                    reading,
                    handle,
                    fail,
                    idempotent,
                    cached,
                    cancellationToken
                );
            }
        }
        catch (Exception exception)
            when (!ReferenceEquals(exception, _loggedException)
                && !(
                    exception is OperationCanceledException
                    && cancellationToken.IsCancellationRequested
                )
            )
        {
            _loggedException = exception;
            LogThrew(logger, exception, name, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }

        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (result.IsSuccess)
        {
            LogSucceeded(logger, name, elapsed);
        }
        else if (logger.IsEnabled(LogLevel.Information))
        {
            LogFailed(logger, name, string.Join(", ", result.Errors.Select(e => e.Code)), elapsed);
        }

        return result;
    }

    private async Task<TResult> Steps<TCommandOrQuery, TResult>(
        TCommandOrQuery commandOrQuery,
        bool ownsUnitOfWork,
        IDisposable? reading,
        Func<IServiceProvider, TCommandOrQuery, CancellationToken, Task<TResult>> handle,
        Func<ImmutableArray<Error>, TResult> fail,
        IdempotentResponses<TResult>? idempotent,
        CachedQuery<TCommandOrQuery, TResult>? cached,
        CancellationToken cancellationToken
    )
        where TCommandOrQuery : notnull
        where TResult : Result
    {
        using (operation.EnterChecks())
        using (AuthorizedOperation.Checking())
        {
            foreach (var authorizer in services.GetServices<IAuthorizer<TCommandOrQuery>>())
            {
                var authorized = await authorizer.AuthorizeAsync(commandOrQuery, cancellationToken);
                if (authorized.IsFailure)
                {
                    return fail(authorized.Errors);
                }
            }

            if (services.GetService<IValidationStep<TCommandOrQuery>>() is { } validation)
            {
                var validated = await validation.ValidateAsync(commandOrQuery, cancellationToken);
                if (validated.IsFailure)
                {
                    return fail(validated.Errors);
                }
            }
        }

        // From here the Operation is authorized: Continue() on its context is a trusted continuation.
        using var running = AuthorizedOperation.Running(operation.Context);

        // The second of the fixed pipeline's two optional slots (ADR 0002): query caching, after the checks. Only the
        // Operation's outermost query goes through the query cache, keyed by its Operation's Actor and tenant, and only
        // while the Operation has not failed. A query inside a command, a check or another query may read a change they
        // made, which its own reading boundary began from, and a failed Operation may still track what it rolled back:
        // such a query neither reads nor stores an entry. A command's declared tags are evicted once the Operation commits.
        if (cached is not null)
        {
            return
                queryCache is not null
                && operation.IsOutermostQuery
                && !operation.IsRollbackOnly
                && operation.Context is { } context
                ? await cached(
                    queryCache,
                    commandOrQuery,
                    context,
                    ct => handle(services, commandOrQuery, ct),
                    // A stored answer outlives this Operation, so the cache closes the query's reading boundary once it
                    // has serialized a success, before storing it: a change then throws and nothing is stored. Its
                    // second close, when the pipeline ends, does nothing.
                    reading!.Dispose,
                    cancellationToken
                )
                : await handle(services, commandOrQuery, cancellationToken);
        }

        if (queryCache is not null)
        {
            operation.EvictAfterCommit(queryCache.TagsInvalidatedBy(commandOrQuery));
        }

        // A command nested in another one: it joins the Operation's Unit of Work.
        if (!ownsUnitOfWork)
        {
            return await handle(services, commandOrQuery, cancellationToken);
        }

        var unitOfWork =
            services.GetService<IUnitOfWork>()
            ?? throw new InvalidOperationException(
                "No IUnitOfWork is registered, so a command cannot run. Register the Unit of Work of a persistence package."
            );

        // The first of the fixed pipeline's two optional slots (ADR 0002): request idempotency, whose key and recorded
        // success are written in the Unit of Work's own transaction.
        var reservation = Reserving<TCommandOrQuery, TResult>(idempotent);

        TResult? returned = null;
        Result outcome;
        operation.StartUnitOfWork();
        try
        {
            outcome = await unitOfWork.RunAsync(
                async ct =>
                {
                    if (
                        reservation is not null
                        && await reservation.ReplayAsync(ct) is { } replayed
                    )
                    {
                        returned = replayed;
                        return returned;
                    }

                    var handled = await handle(services, commandOrQuery, ct);
                    returned =
                        handled.IsSuccess && operation.IsRollbackOnly
                            ? fail(operation.RollbackErrors)
                            : handled;
                    if (reservation is not null)
                    {
                        await reservation.RecordAsync(returned, ct);
                    }

                    return returned;
                },
                cancellationToken
            );
        }
        finally
        {
            operation.EndUnitOfWork();
        }

        // A step after the handler, such as a Domain Event Handler's nested command, may have failed the Operation; a
        // success now would report a commit the Unit of Work was bound not to make.
        if (outcome.IsSuccess && operation.IsRollbackOnly)
        {
            throw new InvalidOperationException(
                $"{unitOfWork.GetType().Name} returned a success for an Operation that is rollback-only. An IUnitOfWork checks Operation.IsRollbackOnly again after every step that can run nested commands, and rolls back."
            );
        }

        if (ReferenceEquals(outcome, returned))
        {
            // After the commit only: a rolled-back Operation evicts nothing, so a valid entry survives it.
            if (
                outcome.IsSuccess
                && queryCache is not null
                && operation.TagsToEvict is { Count: > 0 } tags
            )
            {
                await queryCache.EvictAsync(tags);
            }

            return returned!;
        }

        return outcome.IsFailure
            ? outcome as TResult ?? fail(outcome.Errors)
            : throw new InvalidOperationException(
                $"{unitOfWork.GetType().Name} returned a success that is not the command's own Result. An IUnitOfWork returns the command's Result after a commit and a failed Result after a rollback."
            );
    }

    // The outermost command's reservation: null when its Operation carries no request id, and an exception when the
    // request id cannot be honoured, since running the command anyway would silently drop the promised guarantee.
    private Reservation<TResult>? Reserving<TCommand, TResult>(
        IdempotentResponses<TResult>? idempotent
    )
        where TResult : Result
    {
        if (operation.Context is not { RequestId: { } requestId } context)
        {
            return null;
        }

        if (idempotent is null)
        {
            throw new InvalidOperationException(
                $"{typeof(TCommand).Name} runs with a request id but does not opt in with [Idempotent], so a repeat would run it again. Opt the command in, or send it with a context that has no request id."
            );
        }

        var store =
            services.GetService<IIdempotencyStore>()
            ?? throw new InvalidOperationException(
                $"{typeof(TCommand).Name} opts in with [Idempotent] and runs with a request id, but no IIdempotencyStore is registered to keep its key. Register a Unit of Work that keeps keys, such as PouryaKit.Persistence.EFCore's."
            );
        return new Reservation<TResult>(
            store,
            new IdempotencyKey(
                context.Tenant,
                context.Actor,
                OperationTypes.KeyName(typeof(TCommand)),
                requestId
            ),
            idempotent
        );
    }

    [LoggerMessage(EventName = "Running", Level = LogLevel.Debug, Message = "Running {Operation}")]
    private static partial void LogRunning(ILogger logger, string operation);

    [LoggerMessage(
        EventName = "Succeeded",
        Level = LogLevel.Information,
        Message = "{Operation} succeeded in {ElapsedMilliseconds} ms"
    )]
    private static partial void LogSucceeded(
        ILogger logger,
        string operation,
        double elapsedMilliseconds
    );

    // Its caller checks the level, so the error codes are joined only when the entry is written.
    [LoggerMessage(
        EventName = "Failed",
        Level = LogLevel.Information,
        Message = "{Operation} failed with {ErrorCodes} in {ElapsedMilliseconds} ms",
        SkipEnabledCheck = true
    )]
    private static partial void LogFailed(
        ILogger logger,
        string operation,
        string errorCodes,
        double elapsedMilliseconds
    );

    [LoggerMessage(
        EventName = "Threw",
        Level = LogLevel.Error,
        Message = "{Operation} threw after {ElapsedMilliseconds} ms"
    )]
    private static partial void LogThrew(
        ILogger logger,
        Exception exception,
        string operation,
        double elapsedMilliseconds
    );

    // How an [Idempotent] command's success is recorded and replayed: its value as JSON, or no response for a command
    // without a value.
    private sealed record IdempotentResponses<TResult>(
        Func<TResult, string?> Record,
        Func<string?, TResult> Replay
    )
        where TResult : Result;

    private sealed class Reservation<TResult>(
        IIdempotencyStore store,
        IdempotencyKey key,
        IdempotentResponses<TResult> responses
    )
        where TResult : Result
    {
        // Null once this Operation holds the key; otherwise the success the committed holder recorded.
        public async Task<TResult?> ReplayAsync(CancellationToken cancellationToken) =>
            await store.ReserveAsync(key, cancellationToken) is { } recorded
                ? responses.Replay(recorded.Response)
                : null;

        // A failure records nothing: the Unit of Work rolls back, and the key with it.
        public async Task RecordAsync(TResult result, CancellationToken cancellationToken)
        {
            if (result.IsSuccess && responses.Record(result) is { } response)
            {
                await store.RecordAsync(key, response, cancellationToken);
            }
        }
    }

    private static bool IsIdempotent(Type command) =>
        command.IsDefined(typeof(IdempotentAttribute), inherit: false);

    // How a query's Result goes through the query cache: its invoker knows the TResponse the steps do not.
    private delegate Task<TResult> CachedQuery<in TQuery, TResult>(
        IQueryCache cache,
        TQuery query,
        OperationContext context,
        Func<CancellationToken, Task<TResult>> handle,
        Action endRead,
        CancellationToken cancellationToken
    )
        where TResult : Result;

    private abstract class Invoker<TResult>
        where TResult : Result
    {
        public abstract Task<TResult> Invoke(
            Dispatcher dispatcher,
            object commandOrQuery,
            CancellationToken cancellationToken
        );
    }

    private sealed class CommandInvoker<TCommand> : Invoker<Result>
        where TCommand : Command
    {
        private static readonly IdempotentResponses<Result>? Idempotent = IsIdempotent(
            typeof(TCommand)
        )
            ? new(static _ => null, static _ => Result.Success())
            : null;

        public override Task<Result> Invoke(
            Dispatcher dispatcher,
            object commandOrQuery,
            CancellationToken cancellationToken
        ) =>
            dispatcher.Run(
                (TCommand)commandOrQuery,
                isCommand: true,
                static (services, command, ct) =>
                    services.GetRequiredService<CommandHandler<TCommand>>().Handle(command, ct),
                static errors => errors,
                Idempotent,
                cached: null,
                cancellationToken
            );
    }

    private sealed class CommandInvoker<TCommand, TResponse> : Invoker<Result<TResponse>>
        where TCommand : Command<TResponse>
        where TResponse : notnull
    {
        // A recorded success always has a value: replaying a default in its place is what this slot exists to prevent.
        private static readonly IdempotentResponses<Result<TResponse>>? Idempotent = IsIdempotent(
            typeof(TCommand)
        )
            ? new(
                static result => JsonSerializer.Serialize(result.Value, ResponseJson),
                static response =>
                    response is not null
                    && JsonSerializer.Deserialize<TResponse>(response, ResponseJson) is { } value
                        ? value
                        : throw new InvalidOperationException(
                            $"The success recorded for this request id of {typeof(TCommand).Name} has no value to replay."
                        )
            )
            : null;

        public override Task<Result<TResponse>> Invoke(
            Dispatcher dispatcher,
            object commandOrQuery,
            CancellationToken cancellationToken
        ) =>
            dispatcher.Run(
                (TCommand)commandOrQuery,
                isCommand: true,
                static (services, command, ct) =>
                    services
                        .GetRequiredService<CommandHandler<TCommand, TResponse>>()
                        .Handle(command, ct),
                static errors => errors,
                Idempotent,
                cached: null,
                cancellationToken
            );
    }

    private sealed class QueryInvoker<TQuery, TResponse> : Invoker<Result<TResponse>>
        where TQuery : Query<TResponse>
        where TResponse : notnull
    {
        private static readonly CachedQuery<TQuery, Result<TResponse>> Cached = static (
            cache,
            query,
            context,
            handle,
            endRead,
            ct
        ) => cache.QueryAsync(query, context, handle, endRead, ct);

        public override Task<Result<TResponse>> Invoke(
            Dispatcher dispatcher,
            object commandOrQuery,
            CancellationToken cancellationToken
        ) =>
            dispatcher.Run(
                (TQuery)commandOrQuery,
                isCommand: false,
                static (services, query, ct) =>
                    services
                        .GetRequiredService<QueryHandler<TQuery, TResponse>>()
                        .Handle(query, ct),
                static errors => errors,
                idempotent: null,
                Cached,
                cancellationToken
            );
    }
}
