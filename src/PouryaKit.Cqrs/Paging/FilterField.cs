using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;

namespace PouryaKit.Cqrs;

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

    /// <summary>The name a filter gives the field: a letter followed by letters and digits.</summary>
    public string Name { get; }

    /// <summary>One of string, Guid, int, long, decimal, bool and DateTimeOffset.</summary>
    public Type ValueType { get; }

    /// <summary>The operators a filter on the field may use, in the order declared.</summary>
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
