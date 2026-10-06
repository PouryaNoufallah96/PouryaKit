using PouryaKit.Cqrs;

namespace Billing.Quotes.Features.ExpireQuote;

internal sealed record ExpireQuoteCommand(Guid QuoteId) : Command;
