using System.Collections.Frozen;
using System.Collections.Immutable;
using PouryaKit.Domain;

namespace PouryaKit.Cqrs;

/// <summary>
/// What a Feature's Page read allows: the sort keys a caller may name (the first is the default), the fields a caller
/// may filter on with their operators and value type, and the largest Page size. It holds names only; the Dapper and EF
/// Core page helpers map each name to the Feature's own column or expression, so no text a caller sends becomes SQL. A
/// Feature declares it once, beside its query, and checks the query's <see cref="PageRequest"/> with
/// <see cref="Validate"/> in its validator, the pipeline's one validation step.
/// </summary>
/// <example>
/// <code>
/// public static readonly PageDeclaration Paging = new(
///     sorts: ["name", "created"],
///     filters: [FilterField.Of&lt;string&gt;("name", FilterOperator.Equal, FilterOperator.Contains), FilterField.Of&lt;Guid&gt;("category", FilterOperator.Equal)],
///     maxSize: 50
/// );
/// </code>
/// </example>
public sealed class PageDeclaration
{
    public const int DefaultMaxSize = 100;

    private readonly FrozenDictionary<string, FilterField> _fields;

    /// <summary>
    /// Throws when a name is not a letter followed by letters and digits, or is declared twice, and when
    /// <paramref name="maxSize"/> is below <see cref="PageRequest.DefaultSize"/>, which would refuse every request that
    /// states no size.
    /// </summary>
    public PageDeclaration(IReadOnlyList<string> sorts, IReadOnlyList<FilterField>? filters = null, int maxSize = DefaultMaxSize)
    {
        ArgumentNullException.ThrowIfNull(sorts);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSize, PageRequest.DefaultSize);
        if (sorts.Count == 0)
        {
            throw new ArgumentException("A Page read declares at least one sort key; the first is its default.", nameof(sorts));
        }

        foreach (var sort in sorts)
        {
            FilterField.EnsureName(sort, nameof(sorts));
        }

        if (sorts.Distinct(StringComparer.Ordinal).Count() != sorts.Count)
        {
            throw new ArgumentException("A sort key is declared twice.", nameof(sorts));
        }

        filters ??= [];
        if (filters.Any(filter => filter is null))
        {
            throw new ArgumentNullException(nameof(filters));
        }

        if (filters.Select(filter => filter.Name).Distinct(StringComparer.Ordinal).Count() != filters.Count)
        {
            throw new ArgumentException("A filter field is declared twice.", nameof(filters));
        }

        Sorts = [.. sorts];
        Filters = [.. filters];
        MaxSize = maxSize;
        _fields = filters.ToFrozenDictionary(filter => filter.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<string> Sorts { get; }

    public IReadOnlyList<FilterField> Filters { get; }

    public int MaxSize { get; }

    /// <summary>The sort key <paramref name="request"/> asks for, or the declaration's first.</summary>
    public string SortOf(PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.Sort ?? Sorts[0];
    }

    /// <summary>
    /// A success when <paramref name="request"/> stays inside the declaration; otherwise one failed Result with a
    /// validation Error for each thing outside it: a number below 1 or past the last Page an offset can reach, a size
    /// outside 1 to <see cref="MaxSize"/>, an undeclared sort key or direction, an undeclared filter field, an operator
    /// the field does not declare, a value that is not of the field's type, and a field and operator given twice. No
    /// message repeats what the caller sent.
    /// </summary>
    public Result Validate(PageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = ImmutableArray.CreateBuilder<Error>();
        if (request.Size < 1 || request.Size > MaxSize)
        {
            errors.Add(Error.Validation("Page.SizeOutOfRange", $"The page size is from 1 to {MaxSize}.", "Size"));
        }

        // The offset of the Page's first item must fit an int, which EF Core's Skip takes.
        if (request.Number < 1 || (request.Size >= 1 && request.Number - 1 > int.MaxValue / request.Size))
        {
            errors.Add(Error.Validation("Page.NumberOutOfRange", "The page number is 1 or more, within the reachable pages.", "Number"));
        }

        if (request.Sort is { } sort && !Sorts.Contains(sort, StringComparer.Ordinal))
        {
            errors.Add(Error.Validation("Page.SortUnknown", $"Sort by one of: {string.Join(", ", Sorts)}.", "Sort"));
        }

        if (!Enum.IsDefined(request.Direction))
        {
            errors.Add(Error.Validation("Page.DirectionUnknown", "Sort ascending or descending.", "Direction"));
        }

        var filters = request.Filters ?? [];
        HashSet<(string, FilterOperator)> given = [];
        for (var index = 0; index < filters.Count; index++)
        {
            var target = $"Filters[{index}]";
            if (filters[index] is not { Field: { } name } filter || !_fields.TryGetValue(name, out var field))
            {
                errors.Add(
                    Error.Validation(
                        "Page.FilterUnknown",
                        Filters.Count == 0
                            ? "This read has no filters."
                            : $"Filter on one of: {string.Join(", ", Filters.Select(declared => declared.Name))}.",
                        target
                    )
                );
            }
            else if (!field.Operators.Contains(filter.Operator))
            {
                errors.Add(
                    Error.Validation(
                        "Page.FilterOperatorUnsupported",
                        $"Filter {field.Name} with one of: {string.Join(", ", field.Operators.Select(supported => supported.Name))}.",
                        target
                    )
                );
            }
            else if (field.Read(filter.Value) is null)
            {
                errors.Add(
                    Error.Validation("Page.FilterValueInvalid", $"Filter {field.Name} with a {field.TypeName} value.", target)
                );
            }
            else if (!given.Add((field.Name, filter.Operator)))
            {
                errors.Add(
                    Error.Validation(
                        "Page.FilterRepeated",
                        $"Filter {field.Name} with {filter.Operator.Name} once.",
                        target
                    )
                );
            }
        }

        return errors.Count == 0 ? Result.Success() : errors.ToImmutable();
    }

    /// <summary>
    /// The value of <paramref name="filter"/> as its field's declared type, parsed as <see cref="Validate"/> parsed it;
    /// a DateTimeOffset is in UTC.
    /// Throws when the filter is not one <see cref="Validate"/> accepts.
    /// </summary>
    public object ValueOf(PageFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return filter.Field is { } name
            && _fields.TryGetValue(name, out var field)
            && field.Operators.Contains(filter.Operator)
            && field.Read(filter.Value) is { } value
            ? value
            : throw new InvalidOperationException("The filter is not one this declaration accepts; validate the request first.");
    }
}
