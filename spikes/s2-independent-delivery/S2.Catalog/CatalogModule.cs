using Microsoft.EntityFrameworkCore;
using S2.Catalog.Contracts;
using S2.Kit;

namespace S2.Catalog;

/// <summary>Catalog's Composition Root, as far as messaging goes: it publishes one Integration Event.</summary>
public static class CatalogModule
{
    public const string ProductCreatedWireName = "catalog.product-created.v1";

    public static void Configure(KitMessaging messaging) => messaging.Publish<ProductCreatedV1>(ProductCreatedWireName);

    /// <summary>
    /// Stand-in Operation: adds a product and publishes <see cref="ProductCreatedV1"/> through the Unit of Work. In the
    /// Kit a Notification Handler publishes the Integration Event; the per-handler fan-out is the same either way.
    /// </summary>
    public static Task CreateProductAsync(UnitOfWork<CatalogDbContext> uow, Guid productId, Func<Task>? afterCommitBeforeFlush = null) =>
        uow.ExecuteAsync((db, outgoing) =>
        {
            db.Products.Add(new Product { Id = productId, Name = "spike" });
            outgoing.Add(new ProductCreatedV1(Guid.CreateVersion7(), productId, "spike"));
            return Task.CompletedTask;
        }, afterCommitBeforeFlush);
}

public sealed class Product
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");
        modelBuilder.Entity<Product>(e =>
        {
            e.ToTable("products");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
        });
    }
}
