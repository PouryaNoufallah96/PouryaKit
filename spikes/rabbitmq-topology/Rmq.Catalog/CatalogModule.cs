using Microsoft.EntityFrameworkCore;
using Rmq.Contracts;
using Rmq.Kit;

namespace Rmq.Catalog;

/// <summary>Catalog's Composition Root, as far as messaging goes: it publishes one Integration Event and subscribes to none.</summary>
public static class CatalogModule
{
    public static void Configure(KitMessaging messaging) => messaging.Publish<ProductCreatedV1>(ProductCreatedV1.WireName);

    /// <summary>
    /// Stand-in Operation: adds a product and publishes <see cref="ProductCreatedV1"/> through the Unit of Work. Returns
    /// the Domain Event id the Integration Event follows from, which is the Receipt's identity at every receiver.
    /// </summary>
    public static async Task<Guid> CreateProductAsync(
        UnitOfWork<CatalogDbContext> unitOfWork,
        Guid productId,
        Func<Task>? afterCommitBeforeFlush = null
    )
    {
        var domainEventId = Guid.CreateVersion7();
        await unitOfWork.ExecuteAsync(
            (db, published) =>
            {
                db.Products.Add(new Product { Id = productId, Name = "spike" });
                published.Add(new Published(new ProductCreatedV1(productId, "spike"), domainEventId));
                return Task.CompletedTask;
            },
            afterCommitBeforeFlush
        );
        return domainEventId;
    }

    public const string SchemaSql = """
        create schema if not exists catalog;
        create table if not exists catalog.products (id uuid primary key, name text not null);
        """;
}

internal sealed class Product
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    internal DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("products");
            entity.Property(product => product.Id).HasColumnName("id");
            entity.Property(product => product.Name).HasColumnName("name");
        });
    }
}
