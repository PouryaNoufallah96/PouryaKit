using Catalog.Contracts.Products.Realtime;
using Microsoft.AspNetCore.Routing;
using PouryaKit.Realtime;
using PouryaKit.Web;

namespace Catalog.Products.Events.Realtime;

// Serves the new products AnnounceNewProductNotificationHandler pushes, to every caller of the product's tenant. It sends
// through no Gateway, so the monolith template generates it unchanged; its file is not named as a Gateway endpoint,
// which the monolith replaces with its own.
internal sealed class NewProductsRealtime : IEndpoint
{
    public void Map(IEndpointRouteBuilder routes) =>
        routes.MapPouryaKitRealtime("/events", (_, channel) => channel == NewProduct.Channel);
}
