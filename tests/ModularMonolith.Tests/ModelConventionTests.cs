using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Domain;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Testing;

namespace ModularMonolith.Tests;

// EF Core filters only the root of a hierarchy, never an owned type, so an ISoftDeletable mapped as either would be
// marked deleted on removal and still be read, and an ITenantScoped one read by every tenant. The model refuses to build
// instead, as it does for a part of a tenant-scoped aggregate no filter can reach (ticket #153). No database is needed.
public sealed class ModelConventionTests
{
    [Fact]
    public void A_soft_deletable_type_that_no_query_filter_can_reach_fails_the_model()
    {
        // The DbContext base reads its change tracker when constructed, which builds the model.
        var ownedRefused = Assert.Throws<InvalidOperationException>(() =>
            new OwnedSoftDeletableContext()
        );
        var derivedRefused = Assert.Throws<InvalidOperationException>(() =>
            new DerivedSoftDeletableContext()
        );

        Assert.StartsWith(
            "Cover is ISoftDeletable but is mapped as an owned type",
            ownedRefused.Message
        );
        Assert.StartsWith(
            "DerivedSoftDeletable is ISoftDeletable but is mapped as a type derived from HierarchyRoot",
            derivedRefused.Message
        );
    }

    [Fact]
    public void A_tenant_scoped_type_that_no_query_filter_can_reach_fails_the_model()
    {
        var ownedRefused = Assert.Throws<InvalidOperationException>(() =>
            new OwnedTenantScopedContext()
        );
        var derivedRefused = Assert.Throws<InvalidOperationException>(() =>
            new DerivedTenantScopedContext()
        );

        Assert.StartsWith(
            "Seal is ITenantScoped but is mapped as an owned type",
            ownedRefused.Message
        );
        Assert.StartsWith(
            "DerivedTenantScoped is ITenantScoped but is mapped as a type derived from HierarchyRoot",
            derivedRefused.Message
        );
    }

    [Fact]
    public void A_part_of_a_tenant_scoped_aggregate_that_no_query_filter_can_reach_fails_the_model()
    {
        var derivedRefused = Assert.Throws<InvalidOperationException>(() =>
            new DerivedPartContext()
        );
        var cycleRefused = Assert.Throws<InvalidOperationException>(() => new HolderCycleContext());
        var selfHeldRefused = Assert.Throws<InvalidOperationException>(() =>
            new SelfHeldPartContext()
        );

        Assert.StartsWith(
            "Latch is held by Crate in a tenant-scoped aggregate but is mapped as a type derived from Fitting",
            derivedRefused.Message
        );
        Assert.StartsWith(
            "Bay -> Tier -> Bay: these parts of a tenant-scoped aggregate hold one another",
            cycleRefused.Message
        );
        Assert.StartsWith(
            "Graft holds parts of its own type in a tenant-scoped aggregate but has no required key to another holder",
            selfHeldRefused.Message
        );
    }

    // EF Core reads a memo's own set unfiltered in its filter, so the reply's key to its memo judges nothing, and a memo is
    // read only where its ledger is.
    [Fact]
    public void A_self_held_part_is_judged_by_its_key_to_another_holder_alone()
    {
        using var context = LedgerContext.For("north");

        var read = context.Set<Memo>().ToQueryString();

        Assert.Contains(
            "l.\"Tenant\" = @ef_filter__Tenant AND l.\"Id\" = m.\"LedgerId\")",
            read,
            StringComparison.Ordinal
        );
        Assert.Equal(2, read.Split("EXISTS").Length);
        Assert.DoesNotContain("FROM \"Memo\" AS m0", read, StringComparison.Ordinal);
    }

    // A leaf reads its volume's set, which reads the ledger's, filtered by tenant and by removal; setting the soft-delete
    // filter aside keeps the tenant's at every level.
    [Fact]
    public void A_part_of_a_part_is_read_through_each_holder_up_to_the_roots_filters()
    {
        using var context = LedgerContext.For("north");

        var read = context.Set<Leaf>().ToQueryString();
        var removedToo = context
            .Set<Leaf>()
            .IgnoreQueryFilters([PouryaKitDbContext.SoftDeleteFilter])
            .ToQueryString();

        Assert.Contains("FROM \"Volume\" AS v\n    WHERE EXISTS (", read, StringComparison.Ordinal);
        Assert.Contains(
            "WHERE NOT (l0.\"IsDeleted\") AND l0.\"Tenant\" = @ef_filter__Tenant AND l0.\"Id\" = v.\"LedgerId\")",
            read,
            StringComparison.Ordinal
        );
        Assert.Contains("v.\"Id\" = l.\"VolumeId\"", read, StringComparison.Ordinal);
        Assert.Contains(
            "FROM \"Volume\" AS v\n    WHERE EXISTS (",
            removedToo,
            StringComparison.Ordinal
        );
        Assert.Contains(
            "WHERE l0.\"Tenant\" = @ef_filter__Tenant AND l0.\"Id\" = v.\"LedgerId\")",
            removedToo,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain("IsDeleted", removedToo, StringComparison.Ordinal);
    }

    // A tab on optional keys to a ledger and a volume is judged by each holder its keys name, and by none a key leaves null;
    // one with neither belongs to no tenant, and no tenant reads it.
    [Fact]
    public void A_part_on_optional_keys_is_judged_only_by_the_holders_they_name()
    {
        using var context = LedgerContext.For("north");

        var read = context.Set<Tab>().ToQueryString();

        Assert.Contains(
            "WHERE (t.\"LedgerId\" IS NOT NULL OR t.\"VolumeId\" IS NOT NULL) AND ",
            read,
            StringComparison.Ordinal
        );
        Assert.Contains("(t.\"LedgerId\" IS NULL OR EXISTS (", read, StringComparison.Ordinal);
        Assert.Contains("(t.\"VolumeId\" IS NULL OR EXISTS (", read, StringComparison.Ordinal);
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
            context
                .Set<Drawer>()
                .IgnoreQueryFilters([PouryaKitDbContext.SoftDeleteFilter])
                .ToQueryString(),
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

internal sealed class Folio : AggregateRoot<Guid>
{
    public Seal Seal { get; private set; } = null!;
}

internal sealed class Seal : ITenantScoped
{
    public string Tenant { get; private set; } = "";
}

internal sealed class DerivedTenantScoped : HierarchyRoot, ITenantScoped
{
    public string Tenant { get; private set; } = "";
}

internal sealed class Drawer : AggregateRoot<Guid>, ISoftDeletable
{
    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public string? DeletedBy { get; private set; }
}

internal sealed class Crate : AggregateRoot<Guid>, ITenantScoped
{
    public string Tenant { get; private set; } = "";

    public List<Latch> Latches { get; } = [];
}

internal class Fitting : Entity<Guid>;

internal sealed class Latch : Fitting;

internal sealed class Rack : AggregateRoot<Guid>, ITenantScoped
{
    public string Tenant { get; private set; } = "";

    public List<Bay> Bays { get; } = [];
}

internal sealed class Bay : Entity<Guid>
{
    public List<Tier> Tiers { get; } = [];
}

internal sealed class Tier : Entity<Guid>
{
    public List<Bay> Bays { get; } = [];
}

internal sealed class Ledger : AggregateRoot<Guid>, ITenantScoped, ISoftDeletable
{
    public string Tenant { get; private set; } = "";

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public string? DeletedBy { get; private set; }

    public List<Volume> Volumes { get; } = [];

    public List<Tab> Tabs { get; } = [];

    public List<Memo> Memos { get; } = [];
}

internal sealed class Volume : Entity<Guid>
{
    public List<Leaf> Leaves { get; } = [];

    public List<Tab> Tabs { get; } = [];
}

internal sealed class Leaf : Entity<Guid>;

internal sealed class Tab : Entity<Guid>;

internal sealed class Memo : Entity<Guid>
{
    public List<Memo> Replies { get; } = [];
}

internal sealed class Orchard : AggregateRoot<Guid>, ITenantScoped
{
    public string Tenant { get; private set; } = "";

    public List<Graft> Grafts { get; } = [];
}

internal sealed class Graft : Entity<Guid>
{
    public List<Graft> Grafts { get; } = [];
}

internal sealed class OwnedSoftDeletableContext()
    : PouryaKitDbContext(
        new DbContextOptionsBuilder<OwnedSoftDeletableContext>().UseNpgsql("Host=localhost").Options
    )
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Binder>().OwnsOne(binder => binder.Cover);
    }
}

internal sealed class DerivedSoftDeletableContext()
    : PouryaKitDbContext(
        new DbContextOptionsBuilder<DerivedSoftDeletableContext>()
            .UseNpgsql("Host=localhost")
            .Options
    )
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<HierarchyRoot>();
        modelBuilder.Entity<DerivedSoftDeletable>().HasBaseType<HierarchyRoot>();
    }
}

internal sealed class OwnedTenantScopedContext()
    : PouryaKitDbContext(
        new DbContextOptionsBuilder<OwnedTenantScopedContext>().UseNpgsql("Host=localhost").Options
    )
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Folio>().OwnsOne(folio => folio.Seal);
    }
}

internal sealed class DerivedTenantScopedContext()
    : PouryaKitDbContext(
        new DbContextOptionsBuilder<DerivedTenantScopedContext>()
            .UseNpgsql("Host=localhost")
            .Options
    )
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<HierarchyRoot>();
        modelBuilder.Entity<DerivedTenantScoped>().HasBaseType<HierarchyRoot>();
    }
}

internal sealed class SoftDeletableContext()
    : PouryaKitDbContext(
        new DbContextOptionsBuilder<SoftDeletableContext>().UseNpgsql("Host=localhost").Options
    )
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Drawer>();
    }
}

internal sealed class DerivedPartContext()
    : PouryaKitDbContext(
        new DbContextOptionsBuilder<DerivedPartContext>().UseNpgsql("Host=localhost").Options
    )
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Fitting>();
        modelBuilder.Entity<Latch>().HasBaseType<Fitting>();
        modelBuilder.Entity<Crate>().HasMany(crate => crate.Latches).WithOne().IsRequired();
    }
}

internal sealed class HolderCycleContext()
    : PouryaKitDbContext(
        new DbContextOptionsBuilder<HolderCycleContext>().UseNpgsql("Host=localhost").Options
    )
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Rack>().HasMany(rack => rack.Bays).WithOne().IsRequired();
        modelBuilder.Entity<Bay>().HasMany(bay => bay.Tiers).WithOne().IsRequired();
        modelBuilder.Entity<Tier>().HasMany(tier => tier.Bays).WithOne().IsRequired(false);
    }
}

internal sealed class SelfHeldPartContext()
    : PouryaKitDbContext(
        new DbContextOptionsBuilder<SelfHeldPartContext>().UseNpgsql("Host=localhost").Options
    )
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder
            .Entity<Orchard>()
            .HasMany(orchard => orchard.Grafts)
            .WithOne()
            .IsRequired(false);
        modelBuilder.Entity<Graft>().HasMany(graft => graft.Grafts).WithOne().IsRequired(false);
    }
}

// Built in a scope whose Operation runs for the tenant named: with no tenant the filter reads no row, and EF Core folds
// every EXISTS over the ledger into FALSE.
internal sealed class LedgerContext(IServiceProvider scope)
    : PouryaKitDbContext(
        new DbContextOptionsBuilder<LedgerContext>()
            .UseNpgsql("Host=localhost")
            .UseApplicationServiceProvider(scope)
            .Options
    )
{
    public static LedgerContext For(string tenant)
    {
        var scope = new ServiceCollection()
            .AddPouryaKitCqrs()
            .BuildServiceProvider()
            .CreateScope()
            .ServiceProvider;
        scope.BeginOperation(new OperationContext(GatewayTestExtensions.TestActor, tenant));
        return new LedgerContext(scope);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Ledger>().HasMany(ledger => ledger.Volumes).WithOne().IsRequired();
        modelBuilder.Entity<Ledger>().HasMany(ledger => ledger.Tabs).WithOne().IsRequired(false);
        modelBuilder.Entity<Volume>().HasMany(volume => volume.Leaves).WithOne().IsRequired();
        modelBuilder.Entity<Volume>().HasMany(volume => volume.Tabs).WithOne().IsRequired(false);
        modelBuilder.Entity<Ledger>().HasMany(ledger => ledger.Memos).WithOne().IsRequired();
        modelBuilder.Entity<Memo>().HasMany(memo => memo.Replies).WithOne().IsRequired(false);
    }
}
