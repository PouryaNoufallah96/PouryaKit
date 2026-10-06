using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

/// <summary>
/// The EF persistence and messaging packages' Kit seam for comparing tracked state across application code that only
/// reads. It snapshots the current values through EF's comparers, the entry states and membership, and pending Domain
/// Events by identity and order. Complex properties and collections are compared recursively, in collection order.
/// Changes restored before the comparison are not seen. Feature code never uses it.
/// </summary>
public sealed class TrackedState : IDisposable
{
    private readonly DbContext _dbContext;
    private readonly List<EntryState> _entries;
    private readonly HashSet<object> _entities;
    private readonly bool _includeQueryLoads;

    private TrackedState(DbContext dbContext, bool includeQueryLoads)
    {
        _dbContext = dbContext;
        _entries = [.. dbContext.ChangeTracker.Entries().Select(EntryState.Of)];
        _entities = new HashSet<object>(_entries.Select(entry => entry.Entry.Entity), ReferenceEqualityComparer.Instance);
        _includeQueryLoads = includeQueryLoads;
        if (includeQueryLoads)
        {
            dbContext.ChangeTracker.Tracked += TrackRead;
        }
    }

    /// <summary>
    /// Captures what is currently tracked, including a command's pending changes. When <paramref name="includeQueryLoads"/>
    /// is true, an entity loaded by an EF query during the read is captured as it starts tracking, so an innocent read
    /// is permitted and a later mutation still differs. Dispose after the comparison to stop watching new reads.
    /// </summary>
    public static TrackedState Capture(DbContext dbContext, bool includeQueryLoads = false) => new(dbContext, includeQueryLoads);

    /// <summary>Throws when tracked state or pending Domain Events differ, naming the step that only reads.</summary>
    public void ThrowIfChanged(string step)
    {
        var tracked = _dbContext.ChangeTracker.Entries().ToList();
        var changed = _entries.FirstOrDefault(prepared => prepared.HasChanged());
        if (changed is not null || tracked.Count != _entries.Count || tracked.Any(entry => !_entities.Contains(entry.Entity)))
        {
            throw new InvalidOperationException(
                $"{step} changed {(changed is { } state ? $"a {state.Entry.Metadata.ClrType.Name}" : "the entities")} the Operation saves. Code that only reads must leave the tracked state unchanged."
            );
        }

        if (_entries.FirstOrDefault(prepared => !prepared.Events.SequenceEqual(EventsOf(prepared.Entry), ReferenceEqualityComparer.Instance)) is { } raising)
        {
            throw new InvalidOperationException(
                $"{step} raised a Domain Event on a {raising.Entry.Metadata.ClrType.Name}, or changed its pending Domain Events. Code that only reads must leave them unchanged."
            );
        }
    }

    public void Dispose()
    {
        if (_includeQueryLoads)
        {
            _dbContext.ChangeTracker.Tracked -= TrackRead;
        }
    }

    private void TrackRead(object? sender, EntityTrackedEventArgs tracked)
    {
        if (tracked.FromQuery)
        {
            _entries.Add(EntryState.Of(tracked.Entry));
            _entities.Add(tracked.Entry.Entity);
        }
    }

    private static IReadOnlyList<DomainEvent> EventsOf(EntityEntry entry) =>
        entry.Entity is IAggregateRoot aggregate ? aggregate.DomainEvents : [];

    private sealed record EntryState(EntityEntry Entry, EntityState State, ValueState Values, DomainEvent[] Events)
    {
        public static EntryState Of(EntityEntry entry) =>
            new(
                entry,
                entry.State,
                ValueState.Of(entry, copy: true),
                [.. EventsOf(entry)]
            );

        public bool HasChanged() =>
            Entry.State != State
            || !Values.Matches(ValueState.Of(Entry, copy: false));
    }

    private sealed record ScalarState(IProperty Property, object? Value)
    {
        public static ScalarState Of(PropertyEntry property, bool copy) => new(property.Metadata, copy ? property.Metadata.GetValueComparer().Snapshot(property.CurrentValue) : property.CurrentValue);
    }

    private sealed record ValueState(bool IsNull, ScalarState[] Properties, ValueState[] Children)
    {
        public static ValueState Of(EntityEntry entry, bool copy) => new(
            false,
            [.. entry.Properties.Select(property => ScalarState.Of(property, copy))],
            [.. entry.Metadata.GetComplexProperties().Select(property => property.IsCollection ? Of(entry.ComplexCollection(property), copy) : Of(entry.ComplexProperty(property), copy))]
        );

        private static ValueState Of(ComplexPropertyEntry entry, bool copy) => entry.CurrentValue is null ? new(true, [], []) : new(
            false,
            [.. entry.Properties.Select(property => ScalarState.Of(property, copy))],
            [.. entry.Metadata.ComplexType.GetComplexProperties().Select(property => property.IsCollection ? Of(entry.ComplexCollection(property), copy) : Of(entry.ComplexProperty(property), copy))]
        );

        private static ValueState Of(ComplexElementEntry entry, bool copy) => new(
            false,
            [.. entry.Properties.Select(property => ScalarState.Of(property, copy))],
            [.. entry.Metadata.ComplexType.GetComplexProperties().Select(property => property.IsCollection ? Of(entry.ComplexCollection(property), copy) : Of(entry.ComplexProperty(property), copy))]
        );

        private static ValueState Of(ComplexCollectionEntry entry, bool copy) => new(
            entry.CurrentValue is null,
            [],
            [.. Enumerable.Range(0, entry.CurrentValue?.Cast<object>().Count() ?? 0).Select(index => Of(entry[index], copy))]
        );

        public bool Matches(ValueState current) =>
            IsNull == current.IsNull
            && Properties.Length == current.Properties.Length
            && Properties.Zip(current.Properties).All(pair => pair.First.Property.GetValueComparer().Equals(pair.First.Value, pair.Second.Value))
            && Children.Length == current.Children.Length
            && Children.Zip(current.Children).All(pair => pair.First.Matches(pair.Second));
    }
}
