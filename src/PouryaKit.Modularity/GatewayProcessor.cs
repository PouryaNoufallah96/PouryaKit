using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Modularity;

internal sealed class GatewayProcessor<TModule>(IServiceScopeFactory scopes) : IGatewayProcessor<TModule>
    where TModule : IModuleDefinition
{
    public Task<Result> SendAsync(Command command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(dispatcher => dispatcher.SendAsync(command, cancellationToken));
    }

    public Task<Result<TResponse>> SendAsync<TResponse>(
        Command<TResponse> command,
        CancellationToken cancellationToken = default
    )
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        return RunAsync(dispatcher => dispatcher.SendAsync(command, cancellationToken));
    }

    public Task<Result<TResponse>> QueryAsync<TResponse>(
        Query<TResponse> query,
        CancellationToken cancellationToken = default
    )
        where TResponse : notnull
    {
        ArgumentNullException.ThrowIfNull(query);
        return RunAsync(dispatcher => dispatcher.QueryAsync(query, cancellationToken));
    }

    private async Task<TResult> RunAsync<TResult>(Func<IDispatcher, Task<TResult>> run)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ModuleTag>().Module = typeof(TModule);
        return await run(scope.ServiceProvider.GetRequiredService<IDispatcher>());
    }
}
