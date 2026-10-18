namespace PouryaKit.AI.Postgres;

// A collection's name is its table's in the package's schema, written into SQL unquoted, so it is held to a grammar of
// plain PostgreSQL identifiers, and every other name is refused: its primary key's name, pk_ and the collection's, stays
// within PostgreSQL's 63 bytes.
internal static class VectorCollection
{
    private const int MaximumLength = 60;

    public static void ThrowIfInvalid(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (
            name is not [var first, ..]
            || name.Length > MaximumLength
            || !char.IsAsciiLetterLower(first)
            || !name.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '_')
        )
        {
            throw new ArgumentException(
                $"The vector collection '{name}' is not a lower-case ASCII letter followed by up to {MaximumLength - 1} lower-case ASCII letters, digits and underscores. Name it so.",
                nameof(name)
            );
        }
    }

    public static string Table(string name)
    {
        ThrowIfInvalid(name);
        return $"{VectorCollectionTable.Schema}.{name}";
    }
}
