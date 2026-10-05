using Catalog.Contracts.Products.Events.V1;
using Inventory.Infrastructure.Data.Context;
using Inventory.StockItems.Features.OpenStockItem;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;

namespace Inventory;

/// <summary>The Inventory Module's Composition Root.</summary>
public sealed class InventoryModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePostgres<InventoryDbContext>(connectionName: "app", schema: "inventory");
        module.AddPouryaKitCqrs(typeof(InventoryModule).Assembly);
        module.Subscribe<ProductCreatedV1, OpenStockItemIntegrationEventHandler>(
            "inventory.product-created",
            typeof(InventoryModule)
        );
    }
}
