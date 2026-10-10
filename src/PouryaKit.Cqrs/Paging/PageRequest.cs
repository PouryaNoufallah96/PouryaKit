namespace PouryaKit.Cqrs;

/// <summary>
/// Which Page of a read a caller asks for: its 1-based <see cref="Number"/> and <see cref="Size"/>, the declared sort key
/// and direction, and filters on declared fields. A Feature's query carries it and checks it against its
/// <see cref="PageDeclaration"/> in its validator; the Dapper and EF Core page helpers read only a request that passes.
/// </summary>
public sealed record PageRequest
{
    /// <summary>The <see cref="Size"/> of a request that states none.</summary>
    public const int DefaultSize = 20;

    /// <summary>The 1-based number of the Page asked for; 1 when none is stated.</summary>
    public int Number { get; init; } = 1;

    /// <summary>The most items the Page holds; <see cref="DefaultSize"/> when none is stated.</summary>
    public int Size { get; init; } = DefaultSize;

    /// <summary>One of the declaration's sort keys; null sorts by its first.</summary>
    public string? Sort { get; init; }

    /// <summary>The direction the sort key orders the items in; ascending when none is stated.</summary>
    public SortDirection Direction { get; init; }

    /// <summary>
    /// The filters on declared fields, each a field, an operator and a value; none when none is stated.
    /// </summary>
    public IReadOnlyList<PageFilter> Filters { get; init; } = [];
}
