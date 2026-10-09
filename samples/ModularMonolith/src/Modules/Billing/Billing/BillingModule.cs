using Billing.Infrastructure.Data.Context;
using Billing.PriceLists.Features.OpenPriceList;
using Billing.Quotes.Features.ExpireQuote;
using Catalog.Contracts.Products.Events.V1;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;

namespace Billing;

/// <summary>The Billing Module's Composition Root.</summary>
public sealed class BillingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePouryaKitPostgres<BillingDbContext>(connectionName: "app", schema: "billing");
        module.AddPouryaKitCqrs();
        module.Subscribe<ProductCreatedV1, OpenPriceListIntegrationEventHandler>("billing.product-created");
        module.AddPouryaKitDelayedCommand<ExpireQuoteCommand>("billing.expire-quote");
    }
}
