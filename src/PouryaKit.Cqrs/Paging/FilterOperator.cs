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
