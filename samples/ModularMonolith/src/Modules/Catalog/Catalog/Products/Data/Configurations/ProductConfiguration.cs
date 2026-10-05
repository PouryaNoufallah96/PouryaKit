using Catalog.Products.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Products.Data.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    /// <summary>The unique index on the name of every product not removed; CatalogModule maps its violation.</summary>
    public const string NameIndex = "ix_products_name";

    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("products");
        builder.Property(product => product.Name).HasMaxLength(200);
        // A removed product's name is free again, as the query filter that hides removed products says.
        builder.HasIndex(product => product.Name).IsUnique().HasFilter("is_deleted = false").HasDatabaseName(NameIndex);
    }
}
