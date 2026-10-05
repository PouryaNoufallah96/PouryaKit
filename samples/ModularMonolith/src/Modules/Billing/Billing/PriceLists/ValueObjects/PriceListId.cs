using PouryaKit.Domain;

namespace Billing.PriceLists.ValueObjects;

internal sealed record PriceListId(Guid Value) : TypedId<Guid>(Value);
