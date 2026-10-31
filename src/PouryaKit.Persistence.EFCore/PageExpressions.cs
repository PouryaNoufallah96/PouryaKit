using System.Collections.Immutable;
using System.Linq.Expressions;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The expressions of one Page read through EF Core: the key each declared sort key orders by, a unique key that
/// orders rows with equal sort values, and the predicate of each declared filter field and operator, made from the
/// filter's typed value. <see cref="EfRepository{TAggregate, TId}"/>'s <c>PageAsync</c> applies them; a value reaches
/// the database only as a parameter of the predicate, and nothing a caller sends becomes an expression.
/// </summary>
/// <example>
/// <code>
/// private static readonly PageExpressions&lt;Product&gt; Paging = new PageExpressions&lt;Product&gt;(ListProductsQuery.Paging)
///     .OrderBy("name", product => product.Name)
///     .ThenBy(product => product.Id)
///     .Filter&lt;string&gt;("name", FilterOperator.Contains, name => product => product.Name.Contains(name));
/// </code>
/// </example>
public sealed class PageExpressions<TAggregate>
    where TAggregate : class
{
    private readonly ImmutableDictionary<
        string,
        Func<IQueryable<TAggregate>, bool, IOrderedQueryable<TAggregate>>
    > _orderBy;
    private readonly Func<
        IOrderedQueryable<TAggregate>,
        bool,
        IOrderedQueryable<TAggregate>
    >? _thenBy;
    private readonly ImmutableDictionary<
        (string, FilterOperator),
        Func<object, Expression<Func<TAggregate, bool>>>
    > _filters;

    /// <summary>
    /// The expressions of a Page read <paramref name="declaration"/> allows, with no sort key or filter mapped yet.
    /// </summary>
    public PageExpressions(PageDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        Declaration = declaration;
        _orderBy = [];
        _filters = [];
    }

    private PageExpressions(
        PageExpressions<TAggregate> from,
        ImmutableDictionary<
            string,
            Func<IQueryable<TAggregate>, bool, IOrderedQueryable<TAggregate>>
        > orderBy,
        Func<IOrderedQueryable<TAggregate>, bool, IOrderedQueryable<TAggregate>>? thenBy,
        ImmutableDictionary<
            (string, FilterOperator),
            Func<object, Expression<Func<TAggregate, bool>>>
        > filters
    )
    {
        Declaration = from.Declaration;
        _orderBy = orderBy;
        _thenBy = thenBy;
        _filters = filters;
    }

    /// <summary>The declaration whose sort keys and filters these expressions map.</summary>
    public PageDeclaration Declaration { get; }

    /// <summary>A copy that orders by <paramref name="key"/> when the request sorts by <paramref name="sortKey"/>.</summary>
    public PageExpressions<TAggregate> OrderBy<TKey>(
        string sortKey,
        Expression<Func<TAggregate, TKey>> key
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sortKey);
        ArgumentNullException.ThrowIfNull(key);
        if (!Declaration.Sorts.Contains(sortKey, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                $"{sortKey} is not a sort key the declaration declares.",
                nameof(sortKey)
            );
        }

        return _orderBy.ContainsKey(sortKey)
            ? throw new ArgumentException(
                $"The sort key {sortKey} is already mapped.",
                nameof(sortKey)
            )
            : new PageExpressions<TAggregate>(
                this,
                _orderBy.Add(
                    sortKey,
                    (query, descending) =>
                        descending ? query.OrderByDescending(key) : query.OrderBy(key)
                ),
                _thenBy,
                _filters
            );
    }

    /// <summary>
    /// A copy that orders by <paramref name="key"/> after the sort key, in the same direction. Name a unique key, such
    /// as the id, so that rows with equal sort values keep one order from Page to Page.
    /// </summary>
    public PageExpressions<TAggregate> ThenBy<TKey>(Expression<Func<TAggregate, TKey>> key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new PageExpressions<TAggregate>(
            this,
            _orderBy,
            (query, descending) => descending ? query.ThenByDescending(key) : query.ThenBy(key),
            _filters
        );
    }

    /// <summary>
    /// A copy that filters with the predicate <paramref name="predicate"/> makes of the value when the request filters
    /// <paramref name="field"/> with <paramref name="filterOperator"/>. Throws when that field and operator are not
    /// declared, are already mapped, or the field's value type is not <typeparamref name="TValue"/>.
    /// </summary>
    public PageExpressions<TAggregate> Filter<TValue>(
        string field,
        FilterOperator filterOperator,
        Func<TValue, Expression<Func<TAggregate, bool>>> predicate
    )
        where TValue : notnull
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(predicate);
        var declared = Declaration.Filters.FirstOrDefault(filter => filter.Name == field);
        if (declared is null || !declared.Operators.Contains(filterOperator))
        {
            throw new ArgumentException(
                $"The declaration does not declare {field} with {filterOperator.Name}.",
                nameof(field)
            );
        }

        if (declared.ValueType != typeof(TValue))
        {
            throw new ArgumentException(
                $"The filter field {field} takes {declared.ValueType.Name} values, not {typeof(TValue).Name}.",
                nameof(predicate)
            );
        }

        return _filters.ContainsKey((field, filterOperator))
            ? throw new ArgumentException(
                $"The filter {field} {filterOperator.Name} is already mapped.",
                nameof(field)
            )
            : new PageExpressions<TAggregate>(
                this,
                _orderBy,
                _thenBy,
                _filters.Add((field, filterOperator), value => predicate((TValue)value))
            );
    }

    // Throws, before any query runs, for a request the query's validator should have refused or a mapping that misses
    // part of the declaration.
    internal void EnsureReads(PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Declaration.Validate(request) is { IsFailure: true } invalid)
        {
            throw new InvalidOperationException(
                $"The Page request fails its declaration ({string.Join(", ", invalid.Errors.Select(error => error.Code))}); check it with the AllowedBy(declaration) rule of PouryaKit.Validation in the query's validator."
            );
        }

        var filters = Declaration
            .Filters.SelectMany(field => field.Operators.Select(op => (field.Name, op)))
            .ToList();
        if (
            _thenBy is null
            || _orderBy.Count != Declaration.Sorts.Count
            || _filters.Count != filters.Count
        )
        {
            throw new InvalidOperationException(
                "The Page expressions miss part of their declaration: map every declared sort key with OrderBy, every declared filter field and operator with Filter, and name a unique ThenBy key."
            );
        }
    }

    internal IQueryable<TAggregate> Filtered(IQueryable<TAggregate> query, PageRequest request)
    {
        foreach (var filter in request.Filters ?? [])
        {
            query = query.Where(
                _filters[(filter.Field, filter.Operator)](Declaration.ValueOf(filter))
            );
        }

        return query;
    }

    internal IQueryable<TAggregate> Ordered(IQueryable<TAggregate> query, PageRequest request)
    {
        var descending = request.Direction == SortDirection.Descending;
        return _thenBy!(_orderBy[Declaration.SortOf(request)](query, descending), descending);
    }
}
