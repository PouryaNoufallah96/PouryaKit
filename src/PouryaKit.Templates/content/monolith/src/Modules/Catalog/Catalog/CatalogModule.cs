using Asp.Versioning;
using Catalog.Contracts.Products.Events.V1;
using Catalog.Contracts.Products.Events.V2;
using Catalog.Infrastructure.Data.Context;
using Catalog.Products.Data.Configurations;
using Catalog.Products.Data.Repository;
using Catalog.Products.Entities;
using Catalog.Products.Errors;
using Catalog.Products.Events;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Persistence.Dapper;
using PouryaKit.Persistence.EFCore;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Security;
using PouryaKit.Validation;
using PouryaKit.Web;
using PouryaKit.Web.OpenApi;

namespace Catalog;

/// <summary>
/// The Catalog Module's Composition Root in a Host without Modularity: a plain registration on the Host's own services.
/// Catalog is the Host's one Module in all but name, so its Unit of Work is the Host's, its endpoints send through the
/// Host's <see cref="IDispatcher"/>, and it publishes with no Module.
/// </summary>
public static class CatalogModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddCatalog()
        {
            var catalog = typeof(CatalogModule).Assembly;
            services.UsePouryaKitPostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
            services.MapPouryaKitUniqueConstraint<CatalogDbContext>(ProductConfiguration.NameIndex, ProductErrors.NameInUse);
            services.AddPouryaKitDapper(connectionName: "app", catalog);
            services.AddPouryaKitCqrs(catalog);
            services.AddPouryaKitValidation(catalog);
            services.AddPouryaKitPermissions(catalog);
            services.AddPouryaKitNotificationHandlers(catalog);
            services.Publish<ProductCreatedV1>("catalog.product-created.v1");
            services.Publish<ProductCreatedV2>("catalog.product-created.v2");
            services.PublishSnapshot<Product, ProductSnapshotV1, ProductSnapshotMapper>("catalog.product-snapshot.v1");
            services.AddScoped<IProductRepository, ProductRepository>();
            services.AddPouryaKitEndpoints("/catalog", catalog);
            // v2 adds renaming a product; v1 is deprecated, and the Host configures its sunset.
            services.AddPouryaKitApiVersions(
                "/catalog",
                versions => versions.HasDeprecatedApiVersion(new ApiVersion(1, 0)).HasApiVersion(new ApiVersion(2, 0))
            );
            return services;
        }
    }
}
