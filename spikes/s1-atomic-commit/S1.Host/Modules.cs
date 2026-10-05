using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Wolverine.EntityFrameworkCore;

namespace S1;

/// <summary>How a Module's DbContext relates to Wolverine's envelope tables.</summary>
public enum EnvelopeMapping
{
    /// <summary>No Wolverine mapping: the outbox writes envelope rows with raw SQL on the DbContext's connection.</summary>
    Raw,

    /// <summary><c>MapWolverineEnvelopeStorage("wolverine")</c>: envelope rows are tracked EF entities in the store's schema.</summary>
    MappedToStoreSchema,

    /// <summary><c>MapWolverineEnvelopeStorage()</c> with no schema: EF maps the envelope tables into the Module's schema.</summary>
    MappedToModuleSchema,
}

public sealed record SpikeModelOptions(EnvelopeMapping Mapping);

public sealed class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class StockItem
{
    public Guid Id { get; set; }
    public int Quantity { get; set; }
}

/// <summary>Notification published by the Catalog Module.</summary>
public sealed record ProductCreated(Guid ProductId);

/// <summary>Notification published by the Inventory Module.</summary>
public sealed record StockItemCreated(Guid StockItemId);

public abstract class ModuleDbContext(DbContextOptions options, SpikeModelOptions model, string schema) : DbContext(options)
{
    public EnvelopeMapping Mapping => model.Mapping;
    public string Schema => schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(schema);
        switch (model.Mapping)
        {
            case EnvelopeMapping.MappedToStoreSchema:
                modelBuilder.MapWolverineEnvelopeStorage(SpikeHost.MessageStoreSchema);
                break;
            case EnvelopeMapping.MappedToModuleSchema:
                modelBuilder.MapWolverineEnvelopeStorage();
                break;
        }
    }
}

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options, SpikeModelOptions model)
    : ModuleDbContext(options, model, "catalog")
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
        });
    }
}

public sealed class InventoryDbContext(DbContextOptions<InventoryDbContext> options, SpikeModelOptions model)
    : ModuleDbContext(options, model, "inventory")
{
    public DbSet<StockItem> StockItems => Set<StockItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<StockItem>(e =>
        {
            e.ToTable("stock_items");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Quantity).HasColumnName("quantity");
        });
    }
}

/// <summary>EF caches one model per context type; the Spike varies the mapping between hosts in one process.</summary>
public sealed class MappingAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is ModuleDbContext m ? (context.GetType(), m.Mapping, designTime) : (context.GetType(), designTime);
}
