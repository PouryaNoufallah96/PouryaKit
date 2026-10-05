using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>The one handler of a <typeparamref name="TQuery"/>. It reads and returns a Result; it never sends a command.</summary>
public abstract class QueryHandler<TQuery, TResponse>
    where TQuery : Query<TResponse>
    where TResponse : notnull
{
    public abstract Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken);
}
