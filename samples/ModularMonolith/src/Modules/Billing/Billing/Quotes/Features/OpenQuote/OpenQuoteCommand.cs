using PouryaKit.Cqrs;

namespace Billing.Quotes.Features.OpenQuote;

internal sealed record OpenQuoteCommand(Guid QuoteId, TimeSpan ValidFor) : Command;
