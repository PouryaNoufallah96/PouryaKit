using Billing.Infrastructure.Data.Context;
using Billing.PriceLists.Features.OpenPriceList;
using Billing.Quotes.Features.ExpireQuote;
using Billing.Quotes.Features.OpenWelcomeQuote;
using Catalog.Contracts.Products.Events.V1;
using Ledger.Contracts.Accounts.Events.V1;
using PouryaKit.Cqrs;
using PouryaKit.Messaging.Wolverine;
using PouryaKit.Modularity;
using PouryaKit.Persistence.Postgres;

namespace Billing;

#region billing-composition-root
/// <summary>The Billing Module's Composition Root.</summary>
public sealed class BillingModule : IModuleDefinition
{
    public void Configure(ModuleBuilder module)
    {
        module.UsePouryaKitPostgres<BillingDbContext>(connectionName: "app", schema: "billing");
        module.AddPouryaKitCqrs();
        module.Subscribe<ProductCreatedV1, OpenPriceListIntegrationEventHandler>("billing.product-created");
        module.Subscribe<AccountOpenedV1, OpenWelcomeQuoteIntegrationEventHandler>("billing.account-opened");
        module.AddPouryaKitDelayedCommand<ExpireQuoteCommand>("billing.expire-quote");
    }
}
#endregion
