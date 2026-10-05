using Catalog.Contracts.Products;
using Catalog.Contracts.Products.Events.V1;
using Catalog.Infrastructure.Data.Context;
using Catalog.Products.Data.Configurations;
using Catalog.Products.Data.Repository;
using Catalog.Products.Entities;
using Catalog.Products.Errors;
using Catalog.Products.Events;
using Catalog.Products.Features.GetProduct;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Validation;
using PouryaKit.Web;

namespace Catalog;

/// <summary>The Catalog Module's Composition Root.</summary>
public sealed class CatalogModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
        module.MapUniqueConstraint<CatalogDbContext>(ProductConfiguration.NameIndex, ProductErrors.NameInUse);
        module.AddPouryaKitDapper(connectionName: "app", typeof(CatalogModule).Assembly);
        module.AddPouryaKitCqrs(typeof(CatalogModule).Assembly);
        module.AddPouryaKitValidation(typeof(CatalogModule).Assembly);
        module.AddPouryaKitNotificationHandlers(typeof(CatalogModule).Assembly, typeof(CatalogModule));
        module.Publish<ProductCreatedV1>("catalog.product-created.v1", typeof(CatalogModule));
        module.PublishSnapshot<Product, ProductSnapshotV1, ProductSnapshotMapper>("catalog.product-snapshot.v1", typeof(CatalogModule));
        module.AddScoped<IProductRepository, ProductRepository>();
        module.AddScoped<ICatalogProducts, CatalogProducts>();
        module.AddPouryaKitEndpoints("/catalog", typeof(CatalogModule).Assembly);
    }
}
