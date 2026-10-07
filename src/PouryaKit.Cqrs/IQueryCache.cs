using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// The query cache of the fixed pipeline's second optional slot (ADR 0002). After a query's checks, the dispatcher sends
/// it here when no write Unit of Work is active in its Operation; inside one, the query may see uncommitted state, so it
/// always runs. It also asks which cache tags each command declares, and once the Operation's Unit of Work has committed,
/// evicts what its commands declared; a rollback evicts nothing. <c>PouryaKit.Caching</c> registers it, as a singleton;
/// a Host without one runs every query. Feature code never calls it.
/// </summary>
public interface IQueryCache
{
    /// <summary>
    /// The Result of <paramref name="query"/>, run with <paramref name="context"/>: a stored success of the same query for
    /// the same Actor and tenant, or the Result of <paramref name="handle"/>, which is stored only when it is a success.
    /// </summary>
    Task<Result<TResponse>> QueryAsync<TResponse>(
        Query<TResponse> query,
        OperationContext context,
        Func<CancellationToken, Task<Result<TResponse>>> handle,
        CancellationToken cancellationToken
    )
        where TResponse : notnull;

    /// <summary>The cache tags <paramref name="command"/> declares it invalidates; none when it declares none.</summary>
    IEnumerable<string> TagsInvalidatedBy(object command);

    /// <summary>
    /// Evicts every entry tagged with one of <paramref name="tags"/>, after the Operation whose commands declared them
    /// committed. It takes no cancellation: the commit has happened, so the eviction is not the caller's to cancel.
    /// </summary>
    Task EvictAsync(IReadOnlyCollection<string> tags);
}
