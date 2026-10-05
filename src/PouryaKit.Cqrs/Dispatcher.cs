using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

internal sealed partial class Dispatcher(IServiceProvider services, Operation operation, ILogger<Dispatcher> logger)
    : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> Invokers = new();

    private static readonly Func<ILogger, string, IDisposable?> OperationScope = LoggerMessage.DefineScope<string>(
        "Operation {Operation}"
    );

    private static readonly Error CommandThrew = Error.Unexpected(
        "Operation.CommandThrew",
        "A command failed with an exception, so the Operation was rolled back."
    );

    // The exception last logged in this scope: one that leaves several nested pipelines is logged once, where it began.
    private Exception? _loggedException;

    public Task<Result> SendAsync(Command command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var invoker = Invokers.GetOrAdd(command.GetType(), static type => Create(typeof(CommandInvoker<>), type));
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
    // the handler that sent it ignores the Result. A failed query marks nothing: it has no effect to undo.
    private async Task<TResult> Run<TCommandOrQuery, TResult>(
        TCommandOrQuery commandOrQuery,
        bool isCommand,
        Func<IServiceProvider, TCommandOrQuery, CancellationToken, Task<TResult>> handle,
        Func<ImmutableArray<Error>, TResult> fail,
        CancellationToken cancellationToken
    )
        where TCommandOrQuery : notnull
        where TResult : Result
    {
        try
        {
            var ownsUnitOfWork = false;
            if (isCommand)
            {
                ownsUnitOfWork = operation.EnterCommand();
            }
            else
            {
                operation.EnterQuery();
            }

            try
            {
                var result = await Pipeline(commandOrQuery, ownsUnitOfWork, handle, fail, cancellationToken);
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
                    operation.ExitQuery();
                }
            }
        }
        catch (Exception) when (isCommand)
        {
            operation.MarkRollbackOnly([CommandThrew]);
            throw;
        }
    }

    // The fixed order, written out here and never registered: logging, authorization hook, validation, Unit of Work.
    private async Task<TResult> Pipeline<TCommandOrQuery, TResult>(
        TCommandOrQuery commandOrQuery,
        bool ownsUnitOfWork,
        Func<IServiceProvider, TCommandOrQuery, CancellationToken, Task<TResult>> handle,
        Func<ImmutableArray<Error>, TResult> fail,
        CancellationToken cancellationToken
    )
        where TCommandOrQuery : notnull
        where TResult : Result
    {
        var name = typeof(TCommandOrQuery).Name;
        using var scope = OperationScope(logger, name);
        LogRunning(logger, name);
        var started = Stopwatch.GetTimestamp();

        TResult result;
        try
        {
            result = await Steps(commandOrQuery, ownsUnitOfWork, handle, fail, cancellationToken);
        }
        catch (Exception exception)
            when (!ReferenceEquals(exception, _loggedException)
                && !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
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
        else
        {
            LogFailed(logger, name, string.Join(", ", result.Errors.Select(e => e.Code)), elapsed);
        }

        return result;
    }

    private async Task<TResult> Steps<TCommandOrQuery, TResult>(
        TCommandOrQuery commandOrQuery,
        bool ownsUnitOfWork,
        Func<IServiceProvider, TCommandOrQuery, CancellationToken, Task<TResult>> handle,
        Func<ImmutableArray<Error>, TResult> fail,
        CancellationToken cancellationToken
    )
        where TCommandOrQuery : notnull
        where TResult : Result
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

        // A query, or a command nested in another one: it joins the Operation's Unit of Work, if any.
        if (!ownsUnitOfWork)
        {
            return await handle(services, commandOrQuery, cancellationToken);
        }

        var unitOfWork =
            services.GetService<IUnitOfWork>()
            ?? throw new InvalidOperationException(
                "No IUnitOfWork is registered, so a command cannot run. Register the Unit of Work of a persistence package."
            );

        TResult? returned = null;
        Result outcome;
        operation.StartUnitOfWork();
        try
        {
            outcome = await unitOfWork.RunAsync(
                async ct =>
                {
                    var handled = await handle(services, commandOrQuery, ct);
                    returned = handled.IsSuccess && operation.IsRollbackOnly ? fail(operation.RollbackErrors) : handled;
                    return returned;
                },
                cancellationToken
            );
        }
        finally
        {
            operation.EndUnitOfWork();
        }

        if (ReferenceEquals(outcome, returned))
        {
            return returned!;
        }

        return outcome.IsFailure
            ? outcome as TResult ?? fail(outcome.Errors)
            : throw new InvalidOperationException(
                $"{unitOfWork.GetType().Name} returned a success that is not the command's own Result. An IUnitOfWork returns the command's Result after a commit and a failed Result after a rollback."
            );
    }

    [LoggerMessage(EventName = "Running", Level = LogLevel.Debug, Message = "Running {Operation}")]
    private static partial void LogRunning(ILogger logger, string operation);

    [LoggerMessage(
        EventName = "Succeeded",
        Level = LogLevel.Information,
        Message = "{Operation} succeeded in {ElapsedMilliseconds} ms"
    )]
    private static partial void LogSucceeded(ILogger logger, string operation, double elapsedMilliseconds);

    [LoggerMessage(
        EventName = "Failed",
        Level = LogLevel.Information,
        Message = "{Operation} failed with {ErrorCodes} in {ElapsedMilliseconds} ms"
    )]
    private static partial void LogFailed(ILogger logger, string operation, string errorCodes, double elapsedMilliseconds);

    [LoggerMessage(
        EventName = "Threw",
        Level = LogLevel.Error,
        Message = "{Operation} threw after {ElapsedMilliseconds} ms"
    )]
    private static partial void LogThrew(ILogger logger, Exception exception, string operation, double elapsedMilliseconds);

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
                cancellationToken
            );
    }

    private sealed class CommandInvoker<TCommand, TResponse> : Invoker<Result<TResponse>>
        where TCommand : Command<TResponse>
        where TResponse : notnull
    {
        public override Task<Result<TResponse>> Invoke(
            Dispatcher dispatcher,
            object commandOrQuery,
            CancellationToken cancellationToken
        ) =>
            dispatcher.Run(
                (TCommand)commandOrQuery,
                isCommand: true,
                static (services, command, ct) =>
                    services.GetRequiredService<CommandHandler<TCommand, TResponse>>().Handle(command, ct),
                static errors => errors,
                cancellationToken
            );
    }

    private sealed class QueryInvoker<TQuery, TResponse> : Invoker<Result<TResponse>>
        where TQuery : Query<TResponse>
        where TResponse : notnull
    {
        public override Task<Result<TResponse>> Invoke(
            Dispatcher dispatcher,
            object commandOrQuery,
            CancellationToken cancellationToken
        ) =>
            dispatcher.Run(
                (TQuery)commandOrQuery,
                isCommand: false,
                static (services, query, ct) =>
                    services.GetRequiredService<QueryHandler<TQuery, TResponse>>().Handle(query, ct),
                static errors => errors,
                cancellationToken
            );
    }
}
