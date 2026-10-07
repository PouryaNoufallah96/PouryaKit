using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// The query cache of the fixed pipeline's second optional slot (ADR 0002). After a query's checks, the dispatcher sends
/// it here only when it is its Operation's outermost query and the Operation has not failed; a query inside a command,
/// an authorizer, a validator or another query may read a change they made, so it always runs. It also asks which cache
/// tags each command declares, and once the Operation's Unit of Work has committed, evicts what its commands declared; a
/// rollback evicts nothing. <c>PouryaKit.Caching</c> registers it, as a singleton. A Host without one runs every query,
/// and fails when it is built if it registers a query that asks for the cache (<see cref="IRequiresQueryCache"/>).
/// Feature code never calls it.
/// </summary>
public interface IQueryCache
{
    /// <summary>
    /// The Result of <paramref name="query"/>, run with <paramref name="context"/>: a stored success of the same query for
    /// the same Actor and tenant, or the Result of <paramref name="handle"/>, which is stored only when it is a success.
    /// Before it stores or shares a success, the cache serializes it and then calls <paramref name="endRead"/>, which
    /// closes the query's read and throws when its handler, or serializing its answer, changed tracked state; nothing is
    /// then stored. <paramref name="cancellationToken"/> is observed until the query's run starts or is joined; a run
    /// others may share is not one caller's to cancel, so <paramref name="handle"/> may receive no cancellation.
    /// </summary>
    Task<Result<TResponse>> QueryAsync<TResponse>(
        Query<TResponse> query,
        OperationContext context,
        Func<CancellationToken, Task<Result<TResponse>>> handle,
        Action endRead,
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
