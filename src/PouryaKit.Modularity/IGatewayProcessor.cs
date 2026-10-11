using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Modularity;

/// <summary>
/// The Gateway of <typeparamref name="TModule"/>: the only way anything outside the Module runs one of its commands or
/// queries. Each call is one Operation in a new scope of the Host container, begun with the Module as its
/// <see cref="Operation.Module"/> and the <see cref="OperationContext"/> the caller passes, and disposed when the call
/// returns. A command or query that
/// belongs to another Module is refused with an exception.
/// </summary>
/// <remarks>
/// The context is required: an Operation never runs as an anonymous or default Actor. A caller that starts work passes
/// a new context with its Actor, <see cref="Actor.System"/> for independent system work. A Module answering another
/// Module's question passes the asking Operation's <see cref="OperationContext.Continue"/>.
/// </remarks>
public interface IGatewayProcessor<TModule>
    where TModule : IModuleDefinition
{
    /// <summary>
    /// Runs <paramref name="command"/> as one Operation of the Module with <paramref name="context"/> and returns its
    /// Result.
    /// </summary>
    Task<Result> SendAsync(Command command, OperationContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="command"/> as one Operation of the Module with <paramref name="context"/> and returns its
    /// Result, which holds the response on success.
    /// </summary>
    Task<Result<TResponse>> SendAsync<TResponse>(
        Command<TResponse> command,
        OperationContext context,
        CancellationToken cancellationToken = default
    )
        where TResponse : notnull;

    /// <summary>
    /// Runs <paramref name="query"/> as one Operation of the Module with <paramref name="context"/> and returns its
    /// Result, which holds the answer on success.
    /// </summary>
    Task<Result<TResponse>> QueryAsync<TResponse>(
        Query<TResponse> query,
        OperationContext context,
        CancellationToken cancellationToken = default
    )
        where TResponse : notnull;
}
