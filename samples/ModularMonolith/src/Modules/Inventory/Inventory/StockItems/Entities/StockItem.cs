using Inventory.StockItems.ValueObjects;
using PouryaKit.Domain;

namespace Inventory.StockItems.Entities;

// The stock Inventory keeps of one catalog product. It holds the product's id only: Inventory knows the product
// through what Catalog publishes, never through Catalog's data.
internal sealed class StockItem : AggregateRoot<StockItemId>, IAuditable
{
    private StockItem() { }

    public Guid ProductId { get; private set; }

    public int QuantityOnHand { get; private set; }

    /// <summary>When stock keeping began: the moment the product was created in the catalog.</summary>
    public DateTimeOffset OpenedOn { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = "";

    public DateTimeOffset LastChangedAt { get; private set; }

    public string LastChangedBy { get; private set; } = "";

    public static StockItem Open(Guid productId, DateTimeOffset openedOn) =>
        new()
        {
            Id = new StockItemId(Guid.CreateVersion7()),
            ProductId = productId,
            OpenedOn = openedOn,
        };
}
