using Billing.PriceLists.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Billing.PriceLists.Data.Configurations;

internal sealed class PriceListConfiguration : IEntityTypeConfiguration<PriceList>
{
    public void Configure(EntityTypeBuilder<PriceList> builder)
    {
        builder.ToTable("price_lists");
        builder.HasIndex(priceList => priceList.ProductId);
        builder.Property(priceList => priceList.UnitPrice).HasPrecision(18, 2);
    }
}
