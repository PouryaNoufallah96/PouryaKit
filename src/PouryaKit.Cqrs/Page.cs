namespace PouryaKit.Cqrs;

/// <summary>
/// One Page of a read: its items, its 1-based <see cref="Number"/>, the <see cref="Size"/> asked for, and the
/// <see cref="Total"/> of the whole filtered selection. A Page beyond the last has no items and still the true total.
/// </summary>
public sealed record Page<T>
{
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

    public IReadOnlyList<T> Items { get; }

    public int Number { get; }

    public int Size { get; }

    public long Total { get; }

    public long TotalPages => Total / Size + (Total % Size == 0 ? 0 : 1);

    public bool HasNext => Number < TotalPages;

    public bool HasPrevious => Number > 1;
}
