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
        module.UsePouryaKitPostgres<InventoryDbContext>(connectionName: "app", schema: "inventory");
        // Stock counts take gap-free numbers per year; the StockCounts migration creates the table.
        module.AddPouryaKitNumberSequences();
        module.AddPouryaKitCqrs();
        module.Subscribe<ProductCreatedV1, OpenStockItemIntegrationEventHandler>("inventory.product-created");
        module.Subscribe<ProductCreatedV2, FileProductCategoryIntegrationEventHandler>("inventory.product-categories");
        module.Subscribe<ProductSnapshotV1, CopyProductIntegrationEventHandler>("inventory.catalog-products");
        // Recurring work no person asks for, so it runs as the system Actor named here, at the cadence the Host's
        // configuration sets (Inventory:ReconcileStock:Every).
        module
            .AddPouryaKitRecurringCommand(
                "inventory.reconcile-stock",
                systemActor: "inventory.stock-reconciler",
                occurrence => new ReconcileStockCommand(occurrence)
            )
            .BindConfiguration("Inventory:ReconcileStock");
    }
}
