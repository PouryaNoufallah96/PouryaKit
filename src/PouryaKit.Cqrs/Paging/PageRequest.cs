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

    public int Number { get; init; } = 1;

    public int Size { get; init; } = DefaultSize;

    /// <summary>One of the declaration's sort keys; null sorts by its first.</summary>
    public string? Sort { get; init; }

    public SortDirection Direction { get; init; }

    public IReadOnlyList<PageFilter> Filters { get; init; } = [];
}
