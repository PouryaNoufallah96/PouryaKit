using System.Collections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

// Step 3 of the Unit of Work's commit sequence (ADR 0003), run once per Operation after the Domain Event drain and
// before the save, so nothing changes these fields afterwards: removals of soft-deletable entities become a mark, the
// cascades of the removals that stay deletions are resolved, every changed aggregate's Version advances by exactly
// one, and auditable entities are stamped. The save then has nothing left to change.
internal static class PersistencePreparation
{
    public static void Run(DbContext dbContext, Func<OperationContext> context)
    {
        var entries = dbContext.ChangeTracker.Entries().ToList();

        // The Operation's one time and Actor, read only when something is stamped: an Operation that stamps nothing
        // needs no Operation Context, and one that does fails as reading a missing context fails.
        (DateTimeOffset At, string By)? stamp = null;
        (DateTimeOffset At, string By) Stamp() => stamp ??= (DomainClock.Current.GetUtcNow(), context().Actor.Id);

        // The removals the Operation made, orphans included: PouryaKitDbContext deletes an orphan as soon as changes are
        // detected, which listing the entries above did. A soft-deletable one is kept, so nothing cascades from it.
        foreach (var entry in entries.Where(entry => entry is { State: EntityState.Deleted, Entity: ISoftDeletable }))
        {
            var (at, by) = Stamp();
            entry.State = EntityState.Unchanged;
            entry.Property(nameof(ISoftDeletable.IsDeleted)).CurrentValue = true;
            entry.Property(nameof(ISoftDeletable.DeletedAt)).CurrentValue = at;
            entry.Property(nameof(ISoftDeletable.DeletedBy)).CurrentValue = by;
        }

        // A removal that stays a deletion cascades now, not inside the save: a loaded dependent on a required key is
        // deleted with its principal, soft-deletable or not, since its row cannot outlive it; one on an optional key has
        // its key cleared. Both are then attributed and stamped below like any other change.
        dbContext.ChangeTracker.CascadeChanges();

        // Read before any Version moves, so the bump below is the only change it can see.
        var holders = new Holders(entries);
        var changed = entries
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .SelectMany(entry => AggregatesOf(entry, holders, new HashSet<object>(ReferenceEqualityComparer.Instance)))
            .DistinctBy(entry => entry.Entity, ReferenceEqualityComparer.Instance)
            .ToList();
        foreach (var aggregate in changed)
        {
            // From the version the Operation read, so the bump is one whatever happened to the aggregate before.
            var version = aggregate.Property(nameof(IAggregateRoot.Version));
            version.CurrentValue = (long)version.OriginalValue! + 1;
        }

        foreach (var entry in entries.Where(entry => entry is { Entity: IAuditable, State: EntityState.Added or EntityState.Modified }))
        {
            var (at, by) = Stamp();
            if (entry.State == EntityState.Added)
            {
                entry.Property(nameof(IAuditable.CreatedAt)).CurrentValue = at;
                entry.Property(nameof(IAuditable.CreatedBy)).CurrentValue = by;
            }

            entry.Property(nameof(IAuditable.LastChangedAt)).CurrentValue = at;
            entry.Property(nameof(IAuditable.LastChangedBy)).CurrentValue = by;
        }
    }

    // The aggregates a changed entry changes: the entry itself when it is an aggregate root, otherwise the aggregates of
    // the entities that hold it, reached through each foreign key whose principal navigates to it. Every owned type has
    // such a key, and so does a child entity its parent holds in a navigation; a reference to another aggregate by ID
    // has none. Both holders count: the one the key names now, and the one it named when loaded. So a child moved from
    // one aggregate to another changes both, and one deleted, or detached by setting an optional key to null, changes
    // the aggregate it left. A holder must be tracked: an aggregate whose part changes without the aggregate being
    // loaded cannot advance its version, so concurrent writers would not conflict, and that throws instead. A child the
    // aggregate reaches by no navigation is not part of it.
    private static IEnumerable<EntityEntry> AggregatesOf(EntityEntry entry, Holders holders, HashSet<object> seen)
    {
        if (!seen.Add(entry.Entity))
        {
            return [];
        }

        if (entry.Entity is IAggregateRoot)
        {
            return [entry];
        }

        return entry
            .Metadata.GetForeignKeys()
            .Where(foreignKey => foreignKey.PrincipalToDependent is not null)
            .SelectMany(foreignKey => KeyValues(entry, foreignKey).Select(values => holders.Find(entry, foreignKey, values)))
            .ToList()
            .SelectMany(holder => AggregatesOf(holder, holders, seen));
    }

    // The values of the key now, unless deleted, and when loaded, unless added; a null key names no holder.
    private static IEnumerable<object?[]> KeyValues(EntityEntry entry, IForeignKey foreignKey)
    {
        object?[]? current = entry.State == EntityState.Deleted
            ? null
            : [.. foreignKey.Properties.Select(property => entry.Property(property.Name).CurrentValue)];
        object?[]? original = entry.State == EntityState.Added
            ? null
            : [.. foreignKey.Properties.Select(property => entry.Property(property.Name).OriginalValue)];
        return new[] { current, original }
            .OfType<object?[]>()
            .Where(values => !values.Contains(null))
            .Distinct(Holders.Comparer);
    }

    // The tracked entries by the values of each principal key a foreign key names, indexed once per key.
    private sealed class Holders(List<EntityEntry> entries)
    {
        public static readonly IEqualityComparer<object?[]> Comparer = EqualityComparer<object?[]>.Create(
            (left, right) => StructuralComparisons.StructuralEqualityComparer.Equals(left, right),
            values => StructuralComparisons.StructuralEqualityComparer.GetHashCode(values)
        );

        private readonly Dictionary<IKey, Dictionary<object?[], EntityEntry>> _byKey = [];

        public EntityEntry Find(EntityEntry dependent, IForeignKey foreignKey, object?[] values)
        {
            if (!_byKey.TryGetValue(foreignKey.PrincipalKey, out var byValues))
            {
                // Every entry that has the key, whatever its derived type: keys to sibling derived types share it.
                byValues = new(Comparer);
                foreach (var entry in entries.Where(entry => foreignKey.PrincipalKey.DeclaringEntityType.IsAssignableFrom(entry.Metadata)))
                {
                    byValues.TryAdd(
                        [.. foreignKey.PrincipalKey.Properties.Select(property => entry.Property(property.Name).CurrentValue)],
                        entry
                    );
                }

                _byKey[foreignKey.PrincipalKey] = byValues;
            }

            return byValues.TryGetValue(values, out var holder)
                ? holder
                : throw new InvalidOperationException(
                    $"A {dependent.Metadata.ClrType.Name} changed, and it belongs to the {foreignKey.PrincipalEntityType.ClrType.Name} with key {string.Join(", ", values)}, which this Operation did not load. Load the aggregate through its repository and change it there, so its version advances and a concurrent change conflicts."
                );
        }
    }
}
