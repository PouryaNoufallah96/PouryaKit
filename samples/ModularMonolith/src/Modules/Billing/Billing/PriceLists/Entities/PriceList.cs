using Billing.PriceLists.ValueObjects;
using PouryaKit.Domain;

namespace Billing.PriceLists.Entities;

// The prices Billing charges for one catalog product. It holds the product's id only: Billing knows the product
// through what Catalog publishes, never through Catalog's data.
internal sealed class PriceList : AggregateRoot<PriceListId>, IAuditable
{
    private PriceList() { }

    public Guid ProductId { get; private set; }

    /// <summary>The price per unit, none until the product is priced.</summary>
    public decimal? UnitPrice { get; private set; }

    /// <summary>When billing began: the moment the product was created in the catalog.</summary>
    public DateTimeOffset OpenedOn { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset LastChangedAt { get; private set; }

    public string LastChangedBy { get; private set; } = "";

    public static PriceList Open(Guid productId, DateTimeOffset openedOn) =>
        new()
        {
            Id = new PriceListId(Guid.CreateVersion7()),
            ProductId = productId,
            OpenedOn = openedOn,
        };
}
