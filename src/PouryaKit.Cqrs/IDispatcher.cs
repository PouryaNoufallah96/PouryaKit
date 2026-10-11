using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// Runs a command or query through the fixed pipeline: logging, the optional authorization hook, optional validation, then
/// the handler, inside the Unit of Work for a command. A handler sends a nested command through it too; the nested
/// command joins the Operation's Unit of Work.
/// </summary>
public interface IDispatcher
{
    /// <summary>
    /// Runs <paramref name="command"/> through the pipeline in the scope's Operation and returns its Result.
    /// </summary>
    Task<Result> SendAsync(Command command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="command"/> through the pipeline in the scope's Operation and returns its Result, which
    /// holds the response on success.
    /// </summary>
    Task<Result<TResponse>> SendAsync<TResponse>(Command<TResponse> command, CancellationToken cancellationToken = default)
        where TResponse : notnull;

    /// <summary>
    /// Runs <paramref name="query"/> through the pipeline in the scope's Operation and returns its Result, which holds
    /// the answer on success.
    /// </summary>
    Task<Result<TResponse>> QueryAsync<TResponse>(Query<TResponse> query, CancellationToken cancellationToken = default)
        where TResponse : notnull;
}
