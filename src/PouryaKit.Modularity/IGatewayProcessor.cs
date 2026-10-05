using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Modularity;

/// <summary>
/// The Gateway of <typeparamref name="TModule"/>: the only way anything outside the Module runs one of its commands or
/// queries. Each call is one Operation in a new scope of the Host container, tagged with the Module and disposed when
/// the call returns.
/// </summary>
public interface IGatewayProcessor<TModule>
    where TModule : IModuleDefinition
{
    Task<Result> SendAsync(Command command, CancellationToken cancellationToken = default);

    Task<Result<TResponse>> SendAsync<TResponse>(Command<TResponse> command, CancellationToken cancellationToken = default)
        where TResponse : notnull;

    Task<Result<TResponse>> QueryAsync<TResponse>(Query<TResponse> query, CancellationToken cancellationToken = default)
        where TResponse : notnull;
}
