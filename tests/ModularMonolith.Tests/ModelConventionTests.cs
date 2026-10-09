using Microsoft.EntityFrameworkCore;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;

namespace ModularMonolith.Tests;

// EF Core filters only the root of a hierarchy, never an owned type, so an ISoftDeletable mapped as either would be
// marked deleted on removal and still be read. The model refuses to build instead. No database is needed.
public sealed class ModelConventionTests
{
    [Fact]
    public void A_soft_deletable_type_that_no_query_filter_can_reach_fails_the_model()
    {
        // The DbContext base reads its change tracker when constructed, which builds the model.
        var ownedRefused = Assert.Throws<InvalidOperationException>(() => new OwnedSoftDeletableContext());
        var derivedRefused = Assert.Throws<InvalidOperationException>(() => new DerivedSoftDeletableContext());

        Assert.StartsWith("Cover is ISoftDeletable but is mapped as an owned type", ownedRefused.Message);
        Assert.StartsWith("DerivedSoftDeletable is ISoftDeletable but is mapped as a type derived from HierarchyRoot", derivedRefused.Message);
    }

    // A read that must see removed rows names the filter by the public constant, whose value is published.
    [Fact]
    public void Ignoring_the_filter_the_public_constant_names_reads_soft_deleted_rows_too()
    {
        using var context = new SoftDeletableContext();

        Assert.Equal("PouryaKit.SoftDelete", PouryaKitDbContext.SoftDeleteFilter);
        Assert.Contains("WHERE", context.Set<Drawer>().ToQueryString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            "WHERE",
            context.Set<Drawer>().IgnoreQueryFilters([PouryaKitDbContext.SoftDeleteFilter]).ToQueryString(),
            StringComparison.Ordinal
        );
    }
}

internal sealed class Binder : AggregateRoot<Guid>
{
    public Cover Cover { get; private set; } = null!;
}

internal sealed class Cover : ISoftDeletable
{
    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public string? DeletedBy { get; private set; }
}

internal class HierarchyRoot : AggregateRoot<Guid>;

internal sealed class DerivedSoftDeletable : HierarchyRoot, ISoftDeletable
{
    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public string? DeletedBy { get; private set; }
}

internal sealed class Drawer : AggregateRoot<Guid>, ISoftDeletable
{
    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public string? DeletedBy { get; private set; }
}

internal sealed class OwnedSoftDeletableContext()
    : PouryaKitDbContext(new DbContextOptionsBuilder<OwnedSoftDeletableContext>().UseNpgsql("Host=localhost").Options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Binder>().OwnsOne(binder => binder.Cover);
    }
}

internal sealed class DerivedSoftDeletableContext()
    : PouryaKitDbContext(new DbContextOptionsBuilder<DerivedSoftDeletableContext>().UseNpgsql("Host=localhost").Options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<HierarchyRoot>();
        modelBuilder.Entity<DerivedSoftDeletable>().HasBaseType<HierarchyRoot>();
    }
}

internal sealed class SoftDeletableContext()
    : PouryaKitDbContext(new DbContextOptionsBuilder<SoftDeletableContext>().UseNpgsql("Host=localhost").Options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Drawer>();
    }
}
