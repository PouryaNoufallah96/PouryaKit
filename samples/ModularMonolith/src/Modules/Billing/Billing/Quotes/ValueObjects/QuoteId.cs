using PouryaKit.Domain;

namespace Billing.Quotes.ValueObjects;

internal sealed record QuoteId(Guid Value) : TypedId<Guid>(Value);
