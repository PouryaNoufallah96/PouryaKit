using Catalog.Contracts.Products;
using Catalog.Infrastructure.Data.Context;
using Catalog.Products.Features.GetProductSummary;
using Microsoft.Extensions.DependencyInjection;
using PouryaKit.Cqrs;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;
using PouryaKit.Validation;

namespace Catalog;

/// <summary>The Catalog Module's Composition Root.</summary>
public sealed class CatalogModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePostgres<CatalogDbContext>(connectionName: "app", schema: "catalog");
        module.AddPouryaKitCqrs(typeof(CatalogModule).Assembly);
        module.AddPouryaKitValidation(typeof(CatalogModule).Assembly);
        module.AddScoped<ICatalogProducts, CatalogProducts>();
    }
}
