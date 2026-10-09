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
/// authorizer, a validator or a step of the Unit of Work that only reads. They throw one too where
/// <see cref="Operation.IsUnitOfWorkActive"/> is false, such as a scope that sends no command or one whose command has
/// ended: no commit would save the change, so it would be dropped with the scope. Every other member reads, and needs
/// no Unit of Work.
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
        EnsureMayWrite();
        await _aggregates.AddAsync(aggregate, cancellationToken);
    }

    public void Remove(TAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        EnsureMayWrite();
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

    /// <summary>
    /// One Page of what the projection <paramref name="specification"/> selects: its criteria and the request's filters
    /// from <paramref name="expressions"/> select the rows, which are counted, then ordered by the request's sort key
    /// and the <c>ThenBy</c> key, skipped and taken to the Page, and projected. A Page beyond the last has no items and
    /// the true total. The specification brings criteria and its <c>Select</c> only; paging owns ordering, so one with
    /// ordering, <c>Skip</c> or <c>Take</c> throws, as does a request that fails its declaration.
    /// </summary>
    protected async Task<Page<TResult>> PageAsync<TResult>(
        ISpecification<TAggregate, TResult> specification,
        PageExpressions<TAggregate> expressions,
        PageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(expressions);
        expressions.EnsureReads(request);
        // Ardalis marks a Skip or Take that was never set with -1; any other value, 0 included, is the specification's own.
        if (
            specification.Selector is not { } selector
            || specification.OrderExpressions.Any()
            || specification is not { Skip: -1, Take: -1 }
        )
        {
            throw new ArgumentException(
                "A Page read's specification has criteria and a Select only; the Page orders, skips and takes.",
                nameof(specification)
            );
        }

        var selection = expressions.Filtered(
            SpecificationEvaluator.Default.GetQuery(_aggregates, (ISpecification<TAggregate>)specification, evaluateCriteriaOnly: true),
            request
        );
        var total = await selection.LongCountAsync(cancellationToken);
        var items = await expressions
            .Ordered(selection, request)
            .Skip((request.Number - 1) * request.Size)
            .Take(request.Size)
            .Select(selector)
            .ToListAsync(cancellationToken);
        return new Page<TResult>(items, request.Number, request.Size, total);
    }

    // A query nested in a command runs inside the command's Unit of Work, so what it added would commit with the
    // command, though a failed query Result alone marks nothing rollback-only. Outside a Unit of Work the DbContext
    // would track the change with no commit of its own: it would be dropped with the scope, without a word, or
    // saved by a command the scope sent after it.
    private void EnsureMayWrite()
    {
        if (!_operation.MayChangeState)
        {
            throw new InvalidOperationException(
                "Code that only reads, such as a query, an authorizer, a validator or a step of the Unit of Work that only reads, asked to write through the Unit of Work. Write from a command's handler or a Domain Event Handler."
            );
        }

        if (!_operation.IsUnitOfWorkActive)
        {
            throw new InvalidOperationException(
                $"The repository of {typeof(TAggregate).Name} was asked to write while no Unit of Work is active, so no commit would save the change and it would be lost. Write from a command's handler, a Domain Event Handler, or a Notification or Integration Event Handler."
            );
        }
    }

    private IQueryable<TAggregate> Query(ISpecification<TAggregate> specification, bool evaluateCriteriaOnly = false)
    {
        ArgumentNullException.ThrowIfNull(specification);
        return SpecificationEvaluator.Default.GetQuery(_aggregates, specification, evaluateCriteriaOnly);
    }
}
