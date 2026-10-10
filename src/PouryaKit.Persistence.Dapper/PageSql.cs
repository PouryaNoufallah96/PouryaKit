using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using PouryaKit.Cqrs;

namespace PouryaKit.Persistence.Dapper;

/// <summary>
/// The SQL of one Feature's Page read, which the Feature writes whole: a <c>SELECT</c> of the items and a <c>COUNT</c> of
/// the same selection, each with the same <c>FROM</c> and <c>WHERE</c>, and the column each declared sort key orders
/// by. Each filter its <see cref="PageDeclaration"/> declares arrives as the parameter <c>@{field}_{operator}</c>, such
/// as <c>@name_contains</c>, holding the filter's value or null when the request does not filter on it, so the
/// <c>WHERE</c> tests it the way the Feature chooses: <c>(@name_contains is null or strpos(p.name, @name_contains) &gt; 0)</c>.
/// <see cref="DapperPageExtensions"/> appends only <c>order by</c> the mapped columns and <c>limit</c>/<c>offset</c>
/// parameters; nothing a caller sends is written into the SQL.
/// </summary>
/// <example>
/// <code>
/// private static readonly PageSql Sql = new PageSql(ListProductsQuery.Paging, select: $"select p.id, p.name {From}", count: $"select count(*) {From}")
///     .OrderBy("name", "p.name")
///     .OrderBy("created", "p.created_at")
///     .ThenBy("p.id");
/// </code>
/// </example>
public sealed class PageSql
{
    internal const string SizeParameter = "page_size";
    internal const string OffsetParameter = "page_offset";

    private static readonly FrozenDictionary<Type, DbType> DbTypes = new Dictionary<Type, DbType>
    {
        [typeof(string)] = DbType.String,
        [typeof(Guid)] = DbType.Guid,
        [typeof(int)] = DbType.Int32,
        [typeof(long)] = DbType.Int64,
        [typeof(decimal)] = DbType.Decimal,
        [typeof(bool)] = DbType.Boolean,
        [typeof(DateTimeOffset)] = DbType.DateTimeOffset,
    }.ToFrozenDictionary();

    private readonly ImmutableDictionary<string, string> _orderBy;
    private readonly string? _thenBy;

    /// <summary>
    /// Throws when <paramref name="select"/> or <paramref name="count"/> does not use the parameter of every filter
    /// <paramref name="declaration"/> declares, or when <paramref name="select"/> ends with a semicolon.
    /// </summary>
    public PageSql(PageDeclaration declaration, string select, string count)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentException.ThrowIfNullOrWhiteSpace(select);
        ArgumentException.ThrowIfNullOrWhiteSpace(count);
        Select = select.TrimEnd();
        if (Select.EndsWith(';'))
        {
            throw new ArgumentException("The SELECT of a Page read ends without a semicolon: the page helper appends its ORDER BY and LIMIT to it.", nameof(select));
        }

        foreach (var (name, _, _) in Parameters(declaration))
        {
            foreach (var (text, argument) in new[] { (Select, nameof(select)), (count, nameof(count)) })
            {
                if (!Regex.IsMatch(text, $"@{Regex.Escape(name)}(?![A-Za-z0-9_])"))
                {
                    throw new ArgumentException(
                        $"The {argument} of a Page read does not use @{name}, so that declared filter would be ignored.",
                        argument
                    );
                }
            }
        }

        Declaration = declaration;
        Count = count;
        _orderBy = [];
    }

    private PageSql(PageSql from, ImmutableDictionary<string, string> orderBy, string? thenBy)
    {
        Declaration = from.Declaration;
        Select = from.Select;
        Count = from.Count;
        _orderBy = orderBy;
        _thenBy = thenBy;
    }

    public PageDeclaration Declaration { get; }

    public string Select { get; }

    public string Count { get; }

    /// <summary>
    /// A copy that orders by <paramref name="column"/>, the Feature's own SQL, when the request sorts by
    /// <paramref name="sortKey"/>. Throws when the key is not declared or is already mapped.
    /// </summary>
    public PageSql OrderBy(string sortKey, string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sortKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        if (!Declaration.Sorts.Contains(sortKey, StringComparer.Ordinal))
        {
            throw new ArgumentException($"{sortKey} is not a sort key the declaration declares.", nameof(sortKey));
        }

        return _orderBy.ContainsKey(sortKey)
            ? throw new ArgumentException($"The sort key {sortKey} is already mapped.", nameof(sortKey))
            : new PageSql(this, _orderBy.Add(sortKey, column), _thenBy);
    }

    /// <summary>
    /// A copy that orders by <paramref name="column"/> after the sort key's column, in the same direction. Name a
    /// unique column, such as the id, so that rows with equal sort values keep one order from Page to Page.
    /// </summary>
    public PageSql ThenBy(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return new PageSql(this, _orderBy, column);
    }

    internal string SelectFor(PageRequest request)
    {
        if (_thenBy is null || _orderBy.Count != Declaration.Sorts.Count)
        {
            throw new InvalidOperationException(
                $"The Page read maps the sort keys [{string.Join(", ", _orderBy.Keys.Order(StringComparer.Ordinal))}] of [{string.Join(", ", Declaration.Sorts)}]{(_thenBy is null ? " and has no ThenBy column" : "")}; map every declared key with OrderBy and name a unique ThenBy column."
            );
        }

        var direction = request.Direction == SortDirection.Descending ? "desc" : "asc";
        return $"{Select}\norder by {_orderBy[Declaration.SortOf(request)]} {direction}, {_thenBy} {direction}\nlimit @{SizeParameter} offset @{OffsetParameter}";
    }

    internal DynamicParameters ParametersFor(PageRequest request, object? parameters)
    {
        var bound = new DynamicParameters(parameters);
        var filters = request.Filters ?? [];
        foreach (var (name, field, filterOperator) in Parameters(Declaration))
        {
            var filter = filters.FirstOrDefault(given => given.Field == field.Name && given.Operator == filterOperator);
            var value = filter is null ? null : Declaration.ValueOf(filter);
            // PostgreSQL types a parameter it sees as null only from the type it is sent with.
            bound.Add(name, value, DbTypes[field.ValueType]);
        }

        bound.Add(SizeParameter, request.Size, DbType.Int32);
        bound.Add(OffsetParameter, (request.Number - 1) * request.Size, DbType.Int32);
        return bound;
    }

    private static IEnumerable<(string Name, FilterField Field, FilterOperator Operator)> Parameters(PageDeclaration declaration) =>
        declaration.Filters.SelectMany(field =>
            field.Operators.Select(filterOperator => ($"{field.Name}_{filterOperator.Name}", field, filterOperator))
        );
}
