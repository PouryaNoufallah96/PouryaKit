using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Spike.Common;

namespace OptionA.Dispatcher;

public interface ICommand<TResult>;

public interface IQuery<TResult>;

public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    Task<Result<TResult>> Handle(TCommand command, CancellationToken ct);
}

public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    Task<Result<TResult>> Handle(TQuery query, CancellationToken ct);
}

public interface IDispatcher
{
    Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default);
    Task<Result<TResult>> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default);
}

internal sealed class Dispatcher(
    IServiceProvider services, OperationState state, OperationTrace trace, UnitOfWorkStandIn unitOfWork) : IDispatcher
{
    private static readonly ConcurrentDictionary<Type, object> Invokers = new();

    public Task<Result<TResult>> Send<TResult>(ICommand<TResult> command, CancellationToken ct = default) =>
        ((Invoker<TResult>)Invokers.GetOrAdd(command.GetType(), static t =>
            Activator.CreateInstance(typeof(CommandInvoker<,>).MakeGenericType(t, typeof(TResult)))!))
        .Invoke(this, command, ct);

    public Task<Result<TResult>> Query<TResult>(IQuery<TResult> query, CancellationToken ct = default) =>
        ((Invoker<TResult>)Invokers.GetOrAdd(query.GetType(), static t =>
            Activator.CreateInstance(typeof(QueryInvoker<,>).MakeGenericType(t, typeof(TResult)))!))
        .Invoke(this, query, ct);

    // Failure tracking wraps the whole fixed pipeline, so a failure from any step (authorization,
    // validation or the handler) of a nested command marks the Operation rollback-only.
    private async Task<Result<TResult>> Run<TOperation, TResult>(
        TOperation operation, bool isCommand, Func<CancellationToken, Task<Result<TResult>>> handle, CancellationToken ct)
        where TOperation : notnull
    {
        state.Depth++;
        Result<TResult> result;
        try { result = await Pipeline(operation, isCommand, handle, ct); }
        finally { state.Depth--; }

        if (isCommand && result.IsFailure) state.MarkRollbackOnly();
        return result;
    }

    // The fixed pipeline: logging, optional authorization, validation, Unit of Work (commands only).
    private async Task<Result<TResult>> Pipeline<TOperation, TResult>(
        TOperation operation, bool isCommand, Func<CancellationToken, Task<Result<TResult>>> handle, CancellationToken ct)
        where TOperation : notnull
    {
        var name = typeof(TOperation).Name;
        trace.Add($"log:start {name}");
        try
        {
            foreach (var authorizer in services.GetServices<IAuthorizer<TOperation>>())
                if (authorizer.Authorize(operation) is { } denied) return denied;

            var errors = services.GetServices<IValidator<TOperation>>().SelectMany(v => v.Validate(operation)).ToList();
            if (errors.Count > 0) return Result<TResult>.Fail(errors);

            if (!isCommand) return await handle(ct);

            var owner = unitOfWork.Begin();
            var result = await handle(ct);
            if (owner) unitOfWork.Complete(result);
            return result;
        }
        finally
        {
            trace.Add($"log:end {name}");
        }
    }

    private T Resolve<T>() where T : notnull => services.GetRequiredService<T>();

    private abstract class Invoker<TResult>
    {
        public abstract Task<Result<TResult>> Invoke(Dispatcher dispatcher, object operation, CancellationToken ct);
    }

    private sealed class CommandInvoker<TCommand, TResult> : Invoker<TResult> where TCommand : ICommand<TResult>
    {
        public override Task<Result<TResult>> Invoke(Dispatcher d, object operation, CancellationToken ct) =>
            d.Run((TCommand)operation, isCommand: true,
                c => d.Resolve<ICommandHandler<TCommand, TResult>>().Handle((TCommand)operation, c), ct);
    }

    private sealed class QueryInvoker<TQuery, TResult> : Invoker<TResult> where TQuery : IQuery<TResult>
    {
        public override Task<Result<TResult>> Invoke(Dispatcher d, object operation, CancellationToken ct) =>
            d.Run((TQuery)operation, isCommand: false,
                c => d.Resolve<IQueryHandler<TQuery, TResult>>().Handle((TQuery)operation, c), ct);
    }
}

public static class DispatcherRegistration
{
    private static readonly Type[] Discovered =
        [typeof(ICommandHandler<,>), typeof(IQueryHandler<,>), typeof(IValidator<>), typeof(IAuthorizer<>)];

    /// <summary>
    /// Registers the dispatcher and every handler, validator and authorizer found in the Module assemblies,
    /// internal types included. Throws here, at startup, when a command or query has no handler or several.
    /// </summary>
    public static IServiceCollection AddDispatcher(this IServiceCollection services, params Assembly[] moduleAssemblies)
    {
        services.AddOperationStandIns().AddScoped<IDispatcher, Dispatcher>();

        var types = moduleAssemblies.SelectMany(a => a.GetTypes())
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }).ToList();

        var handlerCounts = new Dictionary<Type, int>();
        foreach (var type in types)
        foreach (var service in type.GetInterfaces().Where(i => i.IsGenericType && Discovered.Contains(i.GetGenericTypeDefinition())))
        {
            services.AddScoped(service, type);
            if (service.GetGenericTypeDefinition() is var d && (d == typeof(ICommandHandler<,>) || d == typeof(IQueryHandler<,>)))
                handlerCounts[service] = handlerCounts.GetValueOrDefault(service) + 1;
        }

        foreach (var operation in types)
        foreach (var contract in operation.GetInterfaces().Where(i => i.IsGenericType))
        {
            var handler = contract.GetGenericTypeDefinition() == typeof(ICommand<>) ? typeof(ICommandHandler<,>)
                : contract.GetGenericTypeDefinition() == typeof(IQuery<>) ? typeof(IQueryHandler<,>) : null;
            if (handler is null) continue;

            var count = handlerCounts.GetValueOrDefault(handler.MakeGenericType(operation, contract.GenericTypeArguments[0]));
            if (count != 1)
                throw new InvalidOperationException($"{operation.FullName} has {count} handlers; exactly one is required.");
        }

        return services;
    }
}
