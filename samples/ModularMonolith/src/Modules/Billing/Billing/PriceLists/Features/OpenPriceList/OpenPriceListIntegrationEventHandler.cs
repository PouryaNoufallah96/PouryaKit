using Billing.PriceLists.Entities;
using Billing.PriceLists.ValueObjects;
using Catalog.Contracts.Products.Events.V1;
using PouryaKit.Domain;
using PouryaKit.Messaging;
using PouryaKit.Persistence;

namespace Billing.PriceLists.Features.OpenPriceList;

// Opens a price list, not yet priced, for every product Catalog creates. Inventory subscribes to the same Integration
// Event under its own subscription: each is delivered, retried and acknowledged on its own, so neither's failure runs
// the other again.
internal sealed class OpenPriceListIntegrationEventHandler(
    IRepository<PriceList, PriceListId> priceLists
) : IIntegrationEventHandler<ProductCreatedV1>
{
    public async Task<Result> Handle(
        ProductCreatedV1 integrationEvent,
        CancellationToken cancellationToken
    )
    {
        await priceLists.AddAsync(
            PriceList.Open(integrationEvent.ProductId, integrationEvent.OccurredOn),
            cancellationToken
        );
        return Result.Success();
    }
}
