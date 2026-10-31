using Asp.Versioning;
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
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Web;
using PouryaKit.Web.OpenApi;

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
        module.Subscribe<ProductCreatedV1, OpenStockItemIntegrationEventHandler>(
            "inventory.product-created"
        );
        module.Subscribe<ProductCreatedV2, FileProductCategoryIntegrationEventHandler>(
            "inventory.product-categories"
        );
        module.Subscribe<ProductSnapshotV1, CopyProductIntegrationEventHandler>(
            "inventory.catalog-products"
        );
        // Recurring work no person asks for, so it runs as the system Actor named here, at the cadence the Host's
        // configuration sets (Inventory:ReconcileStock:Every).
        module
            .AddPouryaKitRecurringCommand(
                "inventory.reconcile-stock",
                systemActor: "inventory.stock-reconciler",
                occurrence => new ReconcileStockCommand(occurrence)
            )
            .BindConfiguration("Inventory:ReconcileStock");
        module.AddPouryaKitDapper(connectionName: "app");
        module.AddPouryaKitEndpoints("/inventory");
        // 2.0, Catalog's current version: a Host's documents and sunsets go by version number, so in a Host beside Catalog a
        // 1.0 here would share Catalog's deprecated 1.0, leave its document undeprecated and announce its sunset here.
        module.AddPouryaKitApiVersions(
            "/inventory",
            versions => versions.HasApiVersion(new ApiVersion(2, 0))
        );
    }
}
