using Catalog.Products.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Catalog.Products.Data.Configurations;

internal sealed class ProductConversationConfiguration
    : IEntityTypeConfiguration<ProductConversation>
{
    /// <summary>The unique index on the conversation's key, its Actor and tenant; CatalogModule maps its violation.</summary>
    public const string KeyIndex = "ix_product_conversations_key";

    public void Configure(EntityTypeBuilder<ProductConversation> builder)
    {
        builder.ToTable("product_conversations");
        builder.HasIndex(conversation => conversation.Key).IsUnique().HasDatabaseName(KeyIndex);
        // json, not jsonb, which reorders an object's keys: the Agent Framework writes a session's content with its
        // "$type" first, and reads it back only so.
        builder.Property(conversation => conversation.Session).HasColumnType("json");
        builder.Property(conversation => conversation.Memory).HasColumnType("json");
    }
}
