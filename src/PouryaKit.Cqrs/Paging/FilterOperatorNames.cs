namespace PouryaKit.Cqrs;

/// <summary>
/// Gives each <see cref="FilterOperator"/> the short name a filter writes it by, as part of the Cqrs Capability's
/// paging.
/// </summary>
public static class FilterOperatorNames
{
    private static readonly FilterOperator[] Operators = Enum.GetValues<FilterOperator>();

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
            foreach (var candidate in Operators)
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
