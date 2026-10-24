using Asp.Versioning;
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
using PouryaKit.Grpc;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Security;
using PouryaKit.Validation;
using PouryaKit.Web;
using PouryaKit.Web.OpenApi;

namespace Catalog;

/// <summary>The Catalog Module's Composition Root.</summary>
public sealed class CatalogModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        // First, so the tenant requirement runs before every other authorizer of Catalog's commands and queries.
        module.AddPouryaKitTenancy();
        module.UsePouryaKitPostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
        module.MapPouryaKitUniqueConstraint<CatalogDbContext>(ProductConfiguration.NameIndex, ProductErrors.NameInUse);
        module.MapPouryaKitUniqueConstraint<CatalogDbContext>(ProductConversationConfiguration.KeyIndex, UnitOfWorkErrors.ConcurrencyConflict);
        module.AddPouryaKitDapper(connectionName: "app");
        module.AddPouryaKitCqrs();
        module.AddPouryaKitValidation();
        module.AddPouryaKitPermissions();
        module.AddPouryaKitNotificationHandlers();
        module.Publish<ProductCreatedV1>("catalog.product-created.v1");
        module.Publish<Contracts.Products.Events.V2.ProductCreatedV2>("catalog.product-created.v2");
        module.PublishSnapshot<Product, ProductSnapshotV1, ProductSnapshotMapper>("catalog.product-snapshot.v1");
        module.AddScoped<IProductRepository, ProductRepository>();
        module.AddScoped<ICatalogProducts, CatalogProducts>();
        module.AddPouryaKitEndpoints("/catalog");
        // Another Host asks for a product over gRPC, through the same Gateway as the HTTP endpoints.
        module.AddPouryaKitGrpcService<ProductGrpcService>();
        // v2 adds renaming a product; v1 is deprecated, and the Host configures its sunset.
        module.AddPouryaKitApiVersions(
            "/catalog",
            versions => versions.HasDeprecatedApiVersion(new ApiVersion(1, 0)).HasApiVersion(new ApiVersion(2, 0))
        );
    }
}
