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
    /// <summary>
    /// Starts tracking a new aggregate; the Unit of Work inserts it when the Operation commits. Throws
    /// <see cref="InvalidOperationException"/> in code that only reads, and while no Unit of Work is active, where no
    /// commit would save the aggregate.
    /// </summary>
    Task AddAsync(TAggregate aggregate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes an aggregate when the Operation commits: its row is deleted, or, when it is <see cref="ISoftDeletable"/>,
    /// kept and marked deleted. Throws <see cref="InvalidOperationException"/> in code that only reads, and while no
    /// Unit of Work is active, where no commit would save the removal.
    /// </summary>
    void Remove(TAggregate aggregate);

    /// <summary>The aggregate with <paramref name="id"/>, or null when there is none.</summary>
    Task<TAggregate?> FindAsync(TId id, CancellationToken cancellationToken = default);

    /// <summary>The first aggregate <paramref name="specification"/> selects, or null.</summary>
    Task<TAggregate?> FindAsync(
        ISpecification<TAggregate> specification,
        CancellationToken cancellationToken = default
    );

    /// <summary>Every aggregate <paramref name="specification"/> selects, in its order.</summary>
    Task<List<TAggregate>> ListAsync(
        ISpecification<TAggregate> specification,
        CancellationToken cancellationToken = default
    );

    /// <summary>True when an aggregate matches the criteria of <paramref name="specification"/>.</summary>
    Task<bool> AnyAsync(
        ISpecification<TAggregate> specification,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// How many aggregates match the criteria of <paramref name="specification"/>, whatever its paging.
    /// </summary>
    Task<int> CountAsync(
        ISpecification<TAggregate> specification,
        CancellationToken cancellationToken = default
    );
}
