namespace PouryaKit.Cqrs;

/// <summary>
/// What a <see cref="PageFilter"/> asks of its field. Each has one short name, used wherever an operator is written as
/// text: the HTTP query string (<c>filter[name][contains]=lamp</c>) and the Dapper parameter of a filter
/// (<c>@name_contains</c>).
/// </summary>
public enum FilterOperator
{
    /// <summary><c>eq</c></summary>
    Equal,

    /// <summary><c>ne</c></summary>
    NotEqual,

    /// <summary><c>contains</c>, for text: the field holds the value, case-sensitively.</summary>
    Contains,

    /// <summary><c>startswith</c>, for text, case-sensitively.</summary>
    StartsWith,

    /// <summary><c>gt</c></summary>
    GreaterThan,

    /// <summary><c>gte</c></summary>
    GreaterThanOrEqual,

    /// <summary><c>lt</c></summary>
    LessThan,

    /// <summary><c>lte</c></summary>
    LessThanOrEqual,
}

public static class FilterOperatorNames
{
    extension(FilterOperator filterOperator)
    {
        /// <summary>The operator's short name, such as <c>eq</c> or <c>contains</c>.</summary>
        public string Name =>
            filterOperator switch
            {
                FilterOperator.Equal => "eq",
                FilterOperator.NotEqual => "ne",
                FilterOperator.Contains => "contains",
                FilterOperator.StartsWith => "startswith",
                FilterOperator.GreaterThan => "gt",
                FilterOperator.GreaterThanOrEqual => "gte",
                FilterOperator.LessThan => "lt",
                FilterOperator.LessThanOrEqual => "lte",
                _ => throw new ArgumentOutOfRangeException(nameof(filterOperator), filterOperator, "Not a filter operator."),
            };
    }

    extension(FilterOperator)
    {
        /// <summary>The operator whose short name is exactly <paramref name="name"/>.</summary>
        public static bool TryParseName(string? name, out FilterOperator filterOperator)
        {
            foreach (var candidate in Enum.GetValues<FilterOperator>())
            {
                if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
                {
                    filterOperator = candidate;
                    return true;
                }
            }

            filterOperator = default;
            return false;
        }
    }
}
