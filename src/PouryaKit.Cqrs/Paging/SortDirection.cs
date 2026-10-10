namespace PouryaKit.Cqrs;

/// <summary>The direction a Page read's sort key orders its items in.</summary>
public enum SortDirection
{
    /// <summary>The smallest value first.</summary>
    Ascending,
    /// <summary>The largest value first.</summary>
    Descending,
}
