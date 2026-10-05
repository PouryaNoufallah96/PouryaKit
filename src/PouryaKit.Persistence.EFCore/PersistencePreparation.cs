using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using PouryaKit.Cqrs;
using PouryaKit.Domain;

namespace PouryaKit.Persistence.EFCore;

// Step 3 of the Unit of Work's commit sequence (ADR 0003), run once per Operation after the Domain Event drain and
// before the save, so nothing changes these fields afterwards: removals of soft-deletable entities become a mark,
// every changed aggregate's Version advances by exactly one, and auditable entities are stamped.
internal static class PersistencePreparation
{
    public static void Run(DbContext dbContext, Func<OperationContext> context)
    {
        var entries = dbContext.ChangeTracker.Entries().ToList();

        // The Operation's one time and Actor, read only when something is stamped: an Operation that stamps nothing
        // needs no Operation Context, and one that does fails as reading a missing context fails.
        (DateTimeOffset At, string By)? stamp = null;
        (DateTimeOffset At, string By) Stamp() => stamp ??= (DomainClock.Current.GetUtcNow(), context().Actor.Id);

        foreach (var entry in entries.Where(entry => entry is { State: EntityState.Deleted, Entity: ISoftDeletable }))
        {
            var (at, by) = Stamp();
            entry.State = EntityState.Unchanged;
            entry.Property(nameof(ISoftDeletable.IsDeleted)).CurrentValue = true;
            entry.Property(nameof(ISoftDeletable.DeletedAt)).CurrentValue = at;
            entry.Property(nameof(ISoftDeletable.DeletedBy)).CurrentValue = by;
        }

        // Read before any Version moves, so the bump below is the only change it can see.
        var changed = entries
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => AggregateOf(entry, entries))
            .OfType<EntityEntry>()
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

    // The aggregate an entry belongs to: the entry itself when it is an aggregate root, otherwise the aggregate of the
    // entity that holds it, reached through the foreign key whose principal navigates to it. Every owned type has
    // such a key, and so does a child entity its parent holds in a navigation; a reference to another aggregate by ID
    // has none. Null when no tracked entity holds it.
    private static EntityEntry? AggregateOf(EntityEntry entry, List<EntityEntry> entries)
    {
        HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
        for (EntityEntry? current = entry; current is not null && seen.Add(current.Entity); current = Holder(current, entries))
        {
            if (current.Entity is IAggregateRoot)
            {
                return current;
            }
        }

        return null;
    }

    private static EntityEntry? Holder(EntityEntry entry, List<EntityEntry> entries)
    {
        foreach (var foreignKey in entry.Metadata.GetForeignKeys().Where(key => key.PrincipalToDependent is not null))
        {
            // A deleted child no longer sits in its parent's navigation; the key it was saved with still names it.
            var values = foreignKey
                .Properties.Select(property =>
                    entry.State == EntityState.Deleted
                        ? entry.Property(property.Name).OriginalValue
                        : entry.Property(property.Name).CurrentValue
                )
                .ToList();
            var holder = entries.FirstOrDefault(candidate =>
                foreignKey.PrincipalEntityType.IsAssignableFrom(candidate.Metadata)
                && foreignKey
                    .PrincipalKey.Properties.Select(property => candidate.Property(property.Name).CurrentValue)
                    .SequenceEqual(values)
            );
            if (holder is not null)
            {
                return holder;
            }
        }

        return null;
    }
}
