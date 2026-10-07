using Catalog.Contracts.Products.Events.V1;
using Catalog.Contracts.Products.Events.V2;
using Inventory.CatalogProducts.Features.CopyProduct;
using Inventory.CatalogProducts.Features.FileProductCategory;
using Inventory.Infrastructure.Data.Context;
using Inventory.StockItems.Features.OpenStockItem;
using Inventory.StockItems.Features.ReconcileStock;
using Microsoft.Extensions.DependencyInjection;
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
        module.Subscribe<ProductCreatedV2, FileProductCategoryIntegrationEventHandler>(
            "inventory.product-categories",
            typeof(InventoryModule)
        );
        module.Subscribe<ProductSnapshotV1, CopyProductIntegrationEventHandler>(
            "inventory.catalog-products",
            typeof(InventoryModule)
        );
        // Recurring work no person asks for, so it runs as the system Actor named here, at the cadence the Host's
        // configuration sets (Inventory:ReconcileStock:Every).
        module
            .AddPouryaKitRecurringCommand(
                "inventory.reconcile-stock",
                systemActor: "inventory.stock-reconciler",
                occurrence => new ReconcileStockCommand(occurrence),
                typeof(InventoryModule)
            )
            .BindConfiguration("Inventory:ReconcileStock");
    }
}
