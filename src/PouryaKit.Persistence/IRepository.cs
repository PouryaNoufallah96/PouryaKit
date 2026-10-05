using Ardalis.Specification;
using PouryaKit.Domain;

namespace PouryaKit.Persistence;

/// <summary>
/// Write-side access to one kind of aggregate. Changes join the Operation's Unit of Work, which saves and commits them
/// once; nothing here saves. Reads for display go through Dapper, not this.
/// </summary>
public interface IRepository<TAggregate, in TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    /// <summary>Starts tracking a new aggregate; the Unit of Work inserts it when the Operation commits.</summary>
    Task AddAsync(TAggregate aggregate, CancellationToken cancellationToken = default);

    /// <summary>Marks an aggregate for deletion when the Operation commits.</summary>
    void Remove(TAggregate aggregate);

    Task<TAggregate?> FindAsync(TId id, CancellationToken cancellationToken = default);

    /// <summary>The first aggregate <paramref name="specification"/> selects, or null.</summary>
    Task<TAggregate?> FindAsync(ISpecification<TAggregate> specification, CancellationToken cancellationToken = default);

    Task<List<TAggregate>> ListAsync(ISpecification<TAggregate> specification, CancellationToken cancellationToken = default);

    Task<bool> AnyAsync(ISpecification<TAggregate> specification, CancellationToken cancellationToken = default);

    Task<int> CountAsync(ISpecification<TAggregate> specification, CancellationToken cancellationToken = default);
}
