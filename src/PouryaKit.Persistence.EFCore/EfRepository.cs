using Ardalis.Specification;
using Ardalis.Specification.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The generic repository over EF Core. It tracks changes in the DbContext of the scope's Unit of Work, so what a
/// handler adds is what that Unit of Work saves. An aggregate-specific repository derives from it and builds its extra
/// members on the inherited ones and on <see cref="ListAsync{TResult}"/>, which return loaded results. It gets no query
/// and no DbContext from this class. <see cref="AddAsync"/> and <see cref="Remove"/> throw an
/// <see cref="InvalidOperationException"/> where <see cref="Operation.MayChangeState"/> is false: in a query, an
/// authorizer, a validator or a step of the Unit of Work that only reads.
/// </summary>
public class EfRepository<TAggregate, TId>(IUnitOfWork unitOfWork) : IRepository<TAggregate, TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    private readonly DbSet<TAggregate> _aggregates = unitOfWork is EfUnitOfWork ef
        ? ef.DbContext.Set<TAggregate>()
        : throw new InvalidOperationException(
            $"The repository of {typeof(TAggregate).Name} needs the EF Core Unit of Work, but the scope's Unit of Work is {unitOfWork.GetType().Name}."
        );

    private readonly Operation _operation = ((EfUnitOfWork)unitOfWork).Operation;

    public async Task AddAsync(TAggregate aggregate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        EnsureMayChangeState();
        await _aggregates.AddAsync(aggregate, cancellationToken);
    }

    public void Remove(TAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        EnsureMayChangeState();
        _aggregates.Remove(aggregate);
    }

    public async Task<TAggregate?> FindAsync(TId id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return await _aggregates.FindAsync([id], cancellationToken);
    }

    public Task<TAggregate?> FindAsync(
        ISpecification<TAggregate> specification,
        CancellationToken cancellationToken = default
    ) => Query(specification).FirstOrDefaultAsync(cancellationToken);

    public Task<List<TAggregate>> ListAsync(
        ISpecification<TAggregate> specification,
        CancellationToken cancellationToken = default
    ) => Query(specification).ToListAsync(cancellationToken);

    public Task<bool> AnyAsync(ISpecification<TAggregate> specification, CancellationToken cancellationToken = default) =>
        Query(specification, evaluateCriteriaOnly: true).AnyAsync(cancellationToken);

    public Task<int> CountAsync(ISpecification<TAggregate> specification, CancellationToken cancellationToken = default) =>
        Query(specification, evaluateCriteriaOnly: true).CountAsync(cancellationToken);

    /// <summary>
    /// What the projection <paramref name="specification"/> selects, loaded into a list. The whole specification
    /// applies, its ordering and paging included, before its <c>Select</c>.
    /// </summary>
    protected Task<List<TResult>> ListAsync<TResult>(
        ISpecification<TAggregate, TResult> specification,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(specification);
        return SpecificationEvaluator.Default.GetQuery(_aggregates, specification).ToListAsync(cancellationToken);
    }

    // A query nested in a command runs inside the command's Unit of Work, so what it added would commit with the
    // command, though a failed query Result alone marks nothing rollback-only.
    private void EnsureMayChangeState()
    {
        if (!_operation.MayChangeState)
        {
            throw new InvalidOperationException(
                "Code that only reads, such as a query, an authorizer, a validator or a step of the Unit of Work that only reads, asked to write through the Unit of Work. Write from a command's handler or a Domain Event Handler."
            );
        }
    }

    private IQueryable<TAggregate> Query(ISpecification<TAggregate> specification, bool evaluateCriteriaOnly = false)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return SpecificationEvaluator.Default.GetQuery(_aggregates, specification, evaluateCriteriaOnly);
    }
}
