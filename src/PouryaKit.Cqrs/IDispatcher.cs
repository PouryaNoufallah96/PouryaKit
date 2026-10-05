using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// Runs a command or query through the fixed pipeline: logging, the optional authorization hook, validation, then
/// the handler, inside the Unit of Work for a command. A handler sends a nested command through it too; the nested
/// command joins the Operation's Unit of Work.
/// </summary>
public interface IDispatcher
{
    Task<Result> SendAsync(Command command, CancellationToken cancellationToken = default);

    Task<Result<TResponse>> SendAsync<TResponse>(Command<TResponse> command, CancellationToken cancellationToken = default)
        where TResponse : notnull;

    Task<Result<TResponse>> QueryAsync<TResponse>(Query<TResponse> query, CancellationToken cancellationToken = default)
        where TResponse : notnull;
}
