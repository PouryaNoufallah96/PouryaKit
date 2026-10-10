namespace PouryaKit.Cqrs;

/// <summary>
/// One Page of a read: its items, its 1-based <see cref="Number"/>, the <see cref="Size"/> asked for, and the
/// <see cref="Total"/> of the whole filtered selection. A Page beyond the last has no items and still the true total.
/// </summary>
public sealed record Page<T>
{
    /// <summary>
    /// A Page holding <paramref name="items"/>; throws when the number or the size is below 1, the total is negative,
    /// or there are more items than the size.
    /// </summary>
    public Page(IReadOnlyList<T> items, int number, int size, long total)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(total);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(items.Count, size, nameof(items));
        Items = items;
        Number = number;
        Size = size;
        Total = total;
    }

    /// <summary>The items of this Page, in the read's order, no more than its size.</summary>
    public IReadOnlyList<T> Items { get; }

    /// <summary>The 1-based position of this Page in the read.</summary>
    public int Number { get; }

    /// <summary>The most items a Page of this read holds, as the read asked.</summary>
    public int Size { get; }

    /// <summary>The number of items in the whole filtered selection, across every Page.</summary>
    public long Total { get; }

    /// <summary>The number of Pages the whole selection fills at this size; 0 when it is empty.</summary>
    public long TotalPages => Total / Size + (Total % Size == 0 ? 0 : 1);

    /// <summary>True when a later Page of the selection holds items.</summary>
    public bool HasNext => Number < TotalPages;

    /// <summary>True when this Page is not the first.</summary>
    public bool HasPrevious => Number > 1;
}
