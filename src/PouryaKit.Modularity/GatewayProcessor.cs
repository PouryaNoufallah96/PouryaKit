using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Modularity;

internal sealed class GatewayProcessor<TModule>(IServiceScopeFactory scopes, HostModules modules)
    : IGatewayProcessor<TModule>
    where TModule : IModuleDefinition
{
    public Task<Result> SendAsync(
        Command command,
        OperationContext context,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(command, context, dispatcher => dispatcher.SendAsync(command, cancellationToken));
    }

    public Task<Result<TResponse>> SendAsync<TResponse>(
        Command<TResponse> command,
        OperationContext context,
        CancellationToken cancellationToken = default
    )
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(command, context, dispatcher => dispatcher.SendAsync(command, cancellationToken));
    }

    public Task<Result<TResponse>> QueryAsync<TResponse>(
        Query<TResponse> query,
        OperationContext context,
        CancellationToken cancellationToken = default
    )
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync(query, context, dispatcher => dispatcher.QueryAsync(query, cancellationToken));
    }

    private async Task<TResult> RunAsync<TResult>(
        object commandOrQuery,
        OperationContext context,
        Func<IDispatcher, Task<TResult>> run
    )
    {
        ArgumentNullException.ThrowIfNull(context);
        modules.EnsureOwns(typeof(TModule), commandOrQuery.GetType());

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.BeginOperation(context, typeof(TModule));
        return await run(scope.ServiceProvider.GetRequiredService<IDispatcher>());
    }
}
