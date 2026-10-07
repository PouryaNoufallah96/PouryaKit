using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
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

/// <summary>A field a Page read may be filtered on: its name, the operators it supports and the type of its value.</summary>
public sealed class FilterField
{
    private static readonly FrozenDictionary<Type, (string Name, Func<string, object?> Parse)> Types = new Dictionary<
        Type,
        (string, Func<string, object?>)
    >
    {
        [typeof(string)] = ("text", text => text),
        [typeof(Guid)] = ("uuid", text => Guid.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : null),
        [typeof(int)] = (
            "whole number",
            text => int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null
        ),
        [typeof(long)] = (
            "whole number",
            text => long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null
        ),
        [typeof(decimal)] = (
            "number",
            text => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null
        ),
        [typeof(bool)] = ("true or false", text => bool.TryParse(text, out var value) ? value : null),
        // A time without an offset is UTC, so the same text means the same instant on every Host.
        [typeof(DateTimeOffset)] = (
            "date and time",
            text =>
                DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
                    ? value
                    : null
        ),
    }.ToFrozenDictionary();

    private FilterField(string name, Type valueType, ImmutableArray<FilterOperator> operators)
    {
        Name = name;
        ValueType = valueType;
        Operators = operators;
        TypeName = Types[valueType].Name;
    }

    public string Name { get; }

    /// <summary>One of string, Guid, int, long, decimal, bool and DateTimeOffset.</summary>
    public Type ValueType { get; }

    public IReadOnlyList<FilterOperator> Operators { get; }

    internal string TypeName { get; }

    /// <summary>
    /// A field filtered with <paramref name="operators"/> on values of <typeparamref name="TValue"/>. Throws for another
    /// value type, for <c>contains</c> or <c>startswith</c> on a value that is not text, and for an ordering operator
    /// on text, a Guid or a bool.
    /// </summary>
    public static FilterField Of<TValue>(string name, params ReadOnlySpan<FilterOperator> operators)
        where TValue : notnull
    {
        EnsureName(name, nameof(name));
        var type = typeof(TValue);
        if (!Types.ContainsKey(type))
        {
            throw new ArgumentException(
                $"A filter value is a string, Guid, int, long, decimal, bool or DateTimeOffset, not {type.Name}.",
                nameof(TValue)
            );
        }

        if (operators.IsEmpty)
        {
            throw new ArgumentException($"The filter field {name} declares no operator.", nameof(operators));
        }

        foreach (var filterOperator in operators)
        {
            var allowed = filterOperator switch
            {
                FilterOperator.Equal or FilterOperator.NotEqual => true,
                FilterOperator.Contains or FilterOperator.StartsWith => type == typeof(string),
                FilterOperator.GreaterThan
                or FilterOperator.GreaterThanOrEqual
                or FilterOperator.LessThan
                or FilterOperator.LessThanOrEqual => type != typeof(string) && type != typeof(Guid) && type != typeof(bool),
                _ => throw new ArgumentOutOfRangeException(nameof(operators), filterOperator, "Not a filter operator."),
            };
            if (!allowed)
            {
                throw new ArgumentException(
                    $"The filter field {name} cannot use {filterOperator.Name} on a {type.Name} value.",
                    nameof(operators)
                );
            }
        }

        ImmutableArray<FilterOperator> declared = [.. operators];
        return declared.Distinct().Count() == declared.Length
            ? new FilterField(name, type, declared)
            : throw new ArgumentException($"The filter field {name} declares an operator twice.", nameof(operators));
    }

    // A declared value, or the text of one; null for anything else.
    // A time is read as UTC, the only offset Npgsql sends to timestamptz, so every helper gets the same instant.
    internal object? Read(object? value) =>
        (
            value switch
            {
                null => null,
                string text => Types[ValueType].Parse(text),
                _ => value.GetType() == ValueType ? value : null,
            }
        ) switch
        {
            DateTimeOffset time => time.ToUniversalTime(),
            var read => read,
        };

    // Names become query-string keys and Dapper parameter names, so they are kept to plain letters and digits.
    internal static void EnsureName(string? name, string parameter)
    {
        if (string.IsNullOrEmpty(name) || !char.IsAsciiLetter(name[0]) || !name.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException(
                $"'{name}' is not a sort key or filter field name: a letter followed by letters and digits.",
                parameter
            );
        }
    }
}
